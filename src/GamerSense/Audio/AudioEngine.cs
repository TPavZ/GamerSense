using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GamerSense.Audio;

public sealed class AudioEngine : IDisposable
{
    private WasapiLoopbackCapture? _capture;
    private WasapiOut? _output;
    private BufferedWaveProvider? _buffer;
    private MMDeviceEnumerator? _enumerator;

    public bool IsRunning { get; private set; }
    public event Action<float>? LevelChanged;
    public event Action<string>? Faulted;

    public void Start(string captureDeviceId, string outputDeviceId)
    {
        Stop();
        _enumerator = new MMDeviceEnumerator();
        var captureEndpoint = _enumerator.GetDevice(captureDeviceId);
        var outputEndpoint = _enumerator.GetDevice(outputDeviceId);

        _capture = new WasapiLoopbackCapture(captureEndpoint);

        // Keep this buffer deliberately short. The original 250 ms buffer could
        // accumulate enough queued game audio to sound like an echo/delayed copy.
        // GamerSense is latency-sensitive, so we target a small safety buffer and
        // let WASAPI shared-mode event sync handle the pacing.
        _buffer = new BufferedWaveProvider(_capture.WaveFormat)
        {
            BufferDuration = TimeSpan.FromMilliseconds(60),
            DiscardOnBufferOverflow = true,
            ReadFully = true
        };

        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;

        // Event-sync output with a smaller requested latency for gaming.
        _output = new WasapiOut(outputEndpoint, AudioClientShareMode.Shared, true, 10);
        _output.Init(_buffer);

        // Start playback first so captured samples are consumed immediately rather
        // than building a backlog before the output client begins reading.
        _output.Play();
        _capture.StartRecording();
        IsRunning = true;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_buffer is null || _capture is null || e.BytesRecorded <= 0)
            return;

        _buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
        LevelChanged?.Invoke(CalculatePeak(e.Buffer, e.BytesRecorded, _capture.WaveFormat));
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
            Faulted?.Invoke(e.Exception.Message);
    }

    public void Stop()
    {
        IsRunning = false;

        if (_capture is not null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
        }

        try { _capture?.StopRecording(); } catch { }
        try { _output?.Stop(); } catch { }

        _capture?.Dispose();
        _output?.Dispose();
        _enumerator?.Dispose();

        _capture = null;
        _output = null;
        _buffer = null;
        _enumerator = null;
    }

    private static float CalculatePeak(byte[] data, int count, WaveFormat format)
    {
        if (format.Encoding != WaveFormatEncoding.IeeeFloat || format.BitsPerSample != 32)
            return 0f;

        var peak = 0f;
        for (var i = 0; i + 3 < count; i += 4)
            peak = Math.Max(peak, Math.Abs(BitConverter.ToSingle(data, i)));

        return Math.Clamp(peak, 0f, 1f);
    }

    public void Dispose() => Stop();
}
