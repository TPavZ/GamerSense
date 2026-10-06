using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GamerSense.Audio;

public sealed class AudioEngine : IDisposable
{
    private WasapiLoopbackCapture? _capture;
    private WasapiOut? _output;
    private BufferedWaveProvider? _buffer;
    private MMDeviceEnumerator? _enumerator;
    private bool _outputStarted;
    private LiveAnalyzer? _analyzer;
    public AnalysisFrame? Analysis => _analyzer?.Latest;
    private ExperimentalDetector? _detector;
    private bool _detectionEnabled = true;
    public string Detection => _detector?.Latest ?? "Sound matching idle";
    public bool DetectionEnabled
    {
        get => _detectionEnabled;
        set { _detectionEnabled = value; if (_detector is not null) _detector.Enabled = value; }
    }

    // Stability-first values for the prototype. Once passthrough is clean we can
    // measure and tune these downward instead of guessing at ultra-low latency.
    private const int OutputLatencyMs = 30;
    private const int BufferDurationMs = 200;
    private const int PrebufferMs = 40;

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
        _buffer = new BufferedWaveProvider(_capture.WaveFormat)
        {
            BufferDuration = TimeSpan.FromMilliseconds(BufferDurationMs),
            DiscardOnBufferOverflow = true,
            ReadFully = true
        };

        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;

        // Shared-mode event sync is appropriate for the prototype and avoids
        // forcing the physical device into a format it does not natively use.
        _output = new WasapiOut(outputEndpoint, AudioClientShareMode.Shared, true, OutputLatencyMs);
        _output.Init(_buffer);

        _outputStarted = false;
        _analyzer = new LiveAnalyzer(_capture.WaveFormat);
        _detector = new ExperimentalDetector(_capture.WaveFormat,
            Path.Combine(AppContext.BaseDirectory, "Models", "wardogs-model.json")) { Enabled = _detectionEnabled };
        _capture.StartRecording();
        IsRunning = true;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_buffer is null || _capture is null || e.BytesRecorded <= 0)
            return;

        try
        {
            _buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            LevelChanged?.Invoke(CalculatePeak(e.Buffer, e.BytesRecorded, _capture.WaveFormat));

            // Give the output a small amount of real audio before it begins reading.
            // Starting against an empty buffer was causing repeated starvation on
            // some devices, heard as skipping/crackling.
            if (!_outputStarted && _output is not null && _buffer.BufferedDuration.TotalMilliseconds >= PrebufferMs)
            {
                _output.Play();
                _outputStarted = true;
            }
            _analyzer?.Tap(e.Buffer, e.BytesRecorded);
            _detector?.Tap(e.Buffer, e.BytesRecorded);
        }
        catch (Exception ex)
        {
            Faulted?.Invoke(ex.Message);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
            Faulted?.Invoke(e.Exception.Message);
    }

    public void Stop()
    {
        IsRunning = false;
        _detector?.Dispose();
        _detector = null;
        _analyzer?.Dispose();
        _analyzer = null;
        _outputStarted = false;

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
