using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GamerSense.Audio;

public sealed class AudioEngine : IDisposable
{
    private WasapiCapture? _capture;
    private WasapiOut? _output;
    private BufferedWaveProvider? _buffer;
    private MMDeviceEnumerator? _enumerator;
    private bool _outputStarted;
    private LiveAnalyzer? _analyzer;
    private AnalysisTap? _tap;
    public double QueuedAudioMs => _buffer?.BufferedDuration.TotalMilliseconds ?? 0;
    private double _captureBatchMs;
    public double CaptureBatchMs => Volatile.Read(ref _captureBatchMs);
    public bool LowerLatency { get; set; }
    public AudioTimingProfile ActiveTiming { get; private set; } = AudioTimingProfile.Stable;
    private string _captureFormat = "Not started", _outputMixFormat = "Not started";
    private PlaybackDiagnostics? _diagnostics;
    public EventMonitor? Events { get; private set; }
    public string AudioDetails => $"GamerSense v0.4.1\nRunning now: {IsRunning}\nMode: {(ActiveTiming == AudioTimingProfile.Responsive ? "Lower latency" : "Stable")}\n" +
        $"Requested capture buffer: {ActiveTiming.CaptureBufferMs} ms\nRequested output buffer: {ActiveTiming.OutputLatencyMs} ms\n" +
        $"Prebuffer target: {ActiveTiming.PrebufferMs} ms\nPlayback buffer capacity: {ActiveTiming.BufferCapacityMs} ms\n" +
        $"Queued audio now: {QueuedAudioMs:F1} ms\nLast capture batch: {CaptureBatchMs:F1} ms\n" +
        $"Capture format: {_captureFormat}\nOutput device mix format: {_outputMixFormat}\nMatching enabled: {DetectionEnabled}\n" +
        "Queue/batch values are partial diagnostics, not total end-to-end latency.\n\n" +
        "PLAYBACK SESSION SUMMARY (retained after Stop)\n" + (_diagnostics?.Report() ?? "No playback session recorded yet.");
    public AnalysisFrame? Analysis => _analyzer?.Latest;
    private ExperimentalDetector? _detector;
    private bool _detectionEnabled = true;
    public string Detection => _detector?.Latest ?? "Sound matching idle";
    public bool DetectionEnabled
    {
        get => _detectionEnabled;
        set { _detectionEnabled = value; if (_detector is not null) _detector.Enabled = value; }
    }

    public bool IsRunning { get; private set; }
    public event Action<string>? Faulted;

    public void Start(string captureDeviceId, string outputDeviceId)
    {
        Stop();
        ActiveTiming = LowerLatency ? AudioTimingProfile.Responsive : AudioTimingProfile.Stable;

        _enumerator = new MMDeviceEnumerator();
        var captureEndpoint = _enumerator.GetDevice(captureDeviceId);
        var outputEndpoint = _enumerator.GetDevice(outputDeviceId);
        _outputMixFormat = outputEndpoint.AudioClient.MixFormat.ToString();

        _capture = LowerLatency ? new ResponsiveLoopbackCapture(captureEndpoint, ActiveTiming.CaptureBufferMs)
            : new WasapiLoopbackCapture(captureEndpoint);
        _captureFormat = _capture.WaveFormat.ToString();
        _diagnostics = new PlaybackDiagnostics(ActiveTiming, captureEndpoint.FriendlyName, outputEndpoint.FriendlyName,
            _captureFormat, _outputMixFormat);
        _buffer = new BufferedWaveProvider(_capture.WaveFormat)
        {
            BufferDuration = TimeSpan.FromMilliseconds(ActiveTiming.BufferCapacityMs),
            DiscardOnBufferOverflow = true,
            ReadFully = true
        };

        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;

        // Shared-mode event sync is appropriate for the prototype and avoids
        // forcing the physical device into a format it does not natively use.
        _output = new WasapiOut(outputEndpoint, AudioClientShareMode.Shared, true, ActiveTiming.OutputLatencyMs);
        _output.Init(_buffer);

        _outputStarted = false;
        _analyzer = new LiveAnalyzer(_capture.WaveFormat);
        _detector = new ExperimentalDetector(_capture.WaveFormat,
            Path.Combine(AppContext.BaseDirectory, "Models", "wardogs-model.json")) { Enabled = _detectionEnabled };
        var analyzer = _analyzer;
        var detector = _detector;
        var events = Events = new EventMonitor(_capture.WaveFormat);
        _tap = new AnalysisTap((data, count) => { analyzer.Tap(data, count); detector.Tap(data, count); events.Tap(data, count); },
            () => { analyzer.Reset(); detector.Reset(); events.MarkGap(); });
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
            Volatile.Write(ref _captureBatchMs, 1000.0 * e.BytesRecorded / _capture.WaveFormat.AverageBytesPerSecond);
            _diagnostics?.Record(_buffer.BufferedDuration.TotalMilliseconds, CaptureBatchMs);

            // Give the output a small amount of real audio before it begins reading.
            // Starting against an empty buffer was causing repeated starvation on
            // some devices, heard as skipping/crackling.
            if (!_outputStarted && _output is not null && _buffer.BufferedDuration.TotalMilliseconds >= ActiveTiming.PrebufferMs)
            {
                _output.Play();
                _outputStarted = true;
            }
            _tap?.Enqueue(e.Buffer, e.BytesRecorded);
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
        Volatile.Write(ref _captureBatchMs, 0);
        Interlocked.Exchange(ref _tap, null)?.Dispose();
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

    public void Dispose() => Stop();
}
