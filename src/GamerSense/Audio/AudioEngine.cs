using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GamerSense.Audio;

public sealed class AudioEngine : IDisposable
{
    private WasapiCapture? _capture;
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

        // VB-CABLE exposes the application's playback endpoint as CABLE Input,
        // while GamerSense captures from the matching CABLE Output endpoint.
        // If the selected endpoint supports loopback, this also works as a useful
        // development fallback for testing the routing pipeline.
        _capture = new WasapiLoopbackCapture(captureEndpoint);
        _buffer = new BufferedWaveProvider(_capture.WaveFormat)
        {
            BufferDuration = TimeSpan.FromMilliseconds(250),
            DiscardOnBufferOverflow = true
        };

        _capture.DataAvailable += (_, e) =>
        {
            _buffer?.AddSamples(e.Buffer, 0, e.BytesRecorded);
            LevelChanged?.Invoke(CalculatePeak(e.Buffer, e.BytesRecorded, _capture.WaveFormat));
        };
        _capture.RecordingStopped += (_, e) =>
        {
            if (e.Exception is not null) Faulted?.Invoke(e.Exception.Message);
        };

        _output = new WasapiOut(outputEndpoint, AudioClientShareMode.Shared, true, 30);
        _output.Init(_buffer);
        _capture.StartRecording();
        _output.Play();
        IsRunning = true;
    }

    public void Stop()
    {
        IsRunning = false;
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
