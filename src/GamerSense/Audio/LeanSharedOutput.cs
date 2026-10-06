using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GamerSense.Audio;

// Experimental shared renderer. An allocated endpoint buffer is a capacity,
// not a requirement to fill it. Keep one device period queued instead.
public sealed class LeanSharedOutput : IDisposable
{
    private readonly AudioClient _client;
    private readonly AudioRenderClient _render;
    private readonly IWaveProvider _source;
    private readonly AutoResetEvent _ready = new(false), _sourceReady = new(false);
    private readonly ManualResetEvent _stop = new(false);
    private readonly int _capacity, _target;
    private readonly byte[] _bytes;
    private Thread? _thread;
    private bool _disposed;
    private long _writes, _paddingSum;
    private int _paddingMax;
    private int _mmcss;
    private readonly string _periodReport = "";
    private readonly bool _availableOnly;
    private long _supplyWaits;
    internal AudioClient Client => _client;
    public WaveFormat WaveFormat => _source.WaveFormat;
    public event Action<string>? Faulted;

    public LeanSharedOutput(MMDevice endpoint, IWaveProvider source, bool lowPeriod = false, bool realTimeRefill = false)
    {
        _source = source;
        _client = endpoint.AudioClient;
        try
        {
            int selectedFrames = 0;
            bool lowActive = lowPeriod && LowPeriodSupport.TryInitialize(_client, source.WaveFormat, out selectedFrames, out _periodReport);
            _availableOnly = lowActive || realTimeRefill;
            if (!lowActive)
            {
                if (lowPeriod) { _client.Dispose(); _client = endpoint.AudioClient; }
                // Allocate a margin but deliberately queue less than that capacity.
                _client.Initialize(AudioClientShareMode.Shared,
                    AudioClientStreamFlags.EventCallback | AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality,
                    300000, 0, source.WaveFormat, Guid.Empty);
            }
            _client.SetEventHandle(_ready.SafeWaitHandle.DangerousGetHandle());
            _capacity = _client.BufferSize;
            int periodFrames = (int)Math.Ceiling(_client.DefaultDevicePeriod * source.WaveFormat.SampleRate / 10000000.0);
            _target = Math.Min(_capacity, lowActive ? selectedFrames : Math.Max(periodFrames, source.WaveFormat.SampleRate / 100));
            _bytes = new byte[_capacity * source.WaveFormat.BlockAlign];
            _render = _client.AudioRenderClient;
        }
        catch
        {
            _client.Dispose(); _ready.Dispose(); _sourceReady.Dispose(); _stop.Dispose();
            throw;
        }
    }

    public static int FramesToWrite(int capacity, int target, int padding, int available = int.MaxValue)
    {
        if (capacity < 1 || target < 1 || padding < 0 || padding > capacity || available < 0)
            throw new ArgumentOutOfRangeException(nameof(padding));
        return Math.Min(available, Math.Max(0, Math.Min(capacity, target) - padding));
    }
    public void Play()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_thread is not null) return;
        _thread = new Thread(Run) { IsBackground = true, Name = "GamerSense lean output", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }
    public void NotifyAudio() => _sourceReady.Set();
    private void Fill()
    {
        int padding = _client.CurrentPadding;
        // A short render period can split a 10 ms capture batch. Queue only real
        // available frames, and wake again on source arrival rather than commit
        // future silence when the rest of a batch can arrive before consumption.
        int available = _availableOnly && _source is MeteredPlaybackProvider meter ? meter.AvailableFrames : int.MaxValue;
        int frames = FramesToWrite(_capacity, _target, padding, available);
        if (_availableOnly && frames == 0 && available == 0 && padding < _target) Interlocked.Increment(ref _supplyWaits);
        if (frames == 0) return;
        int count = frames * _source.WaveFormat.BlockAlign;
        int read = _source.Read(_bytes, 0, count);
        if (read < count) Array.Clear(_bytes, read, count - read);
        IntPtr destination = _render.GetBuffer(frames);
        try { Marshal.Copy(_bytes, 0, destination, count); }
        finally { _render.ReleaseBuffer(frames, AudioClientBufferFlags.None); }
        Interlocked.Increment(ref _writes);
        Interlocked.Add(ref _paddingSum, padding);
        // Only the renderer writes this value; readers take a volatile snapshot.
        if (padding > _paddingMax) Volatile.Write(ref _paddingMax, padding);
    }
    private void Run()
    {
        IntPtr task = IntPtr.Zero;
        bool started = false;
        try
        {
            uint index = 0;
            task = AvSetMmThreadCharacteristics("Pro Audio", ref index);
            Volatile.Write(ref _mmcss, task == IntPtr.Zero ? 0 : 1);
            Fill();
            _client.Start(); started = true;
            var handles = new WaitHandle[] { _stop, _ready, _sourceReady };
            while (WaitHandle.WaitAny(handles, 100) != 0)
                Fill();
        }
        catch (Exception ex) { Faulted?.Invoke(ex.Message); }
        finally
        {
            if (started) { try { _client.Stop(); _client.Reset(); } catch { } }
            if (task != IntPtr.Zero) AvRevertMmThreadCharacteristics(task);
        }
    }
    public void Stop()
    {
        _stop.Set();
        _thread?.Join();
    }
    public string Report()
    {
        double scale = 1000.0 / _source.WaveFormat.SampleRate;
        long writes = Interlocked.Read(ref _writes);
        return "LEAN OUTPUT (retained after Stop)\n" + _periodReport +
            $"Output queued-audio target: {_target * scale:F1} ms (capacity {_capacity * scale:F1} ms)\n" +
            $"Output writes: {writes}\nOutput padding avg / max before writes: {(writes == 0 ? 0 : Interlocked.Read(ref _paddingSum) * scale / writes):F1} / {Volatile.Read(ref _paddingMax) * scale:F1} ms\n" +
            $"Output multimedia scheduling enabled: {Volatile.Read(ref _mmcss) == 1}\n" +
            $"Output fill policy: {(_availableOnly ? "Available captured samples only" : "Silence padding on source shortfall")}\n" +
            (_availableOnly ? $"Refill attempts waiting for captured samples: {Interlocked.Read(ref _supplyWaits)} (includes source pauses; not audible glitch count)\nSilence-fill counts exclude these waits; Windows can still emit silence if its queue runs empty.\n" : "") +
            "Padding samples cover writes only and are not total end-to-end latency.\n";
    }
    public void Dispose()
    {
        if (_disposed) return;
        Stop(); _disposed = true;
        _client.Dispose(); _ready.Dispose(); _sourceReady.Dispose(); _stop.Dispose();
    }
    [DllImport("avrt.dll", CharSet = CharSet.Unicode, EntryPoint = "AvSetMmThreadCharacteristicsW", SetLastError = true)]
    private static extern IntPtr AvSetMmThreadCharacteristics(string taskName, ref uint taskIndex);
    [DllImport("avrt.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);
}
