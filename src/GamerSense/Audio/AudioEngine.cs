using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GamerSense.Audio;

public sealed class AudioEngine : IDisposable
{
    private WasapiCapture? _capture;
    private WasapiOut? _output;
    private LeanSharedOutput? _leanOutput;
    private string _leanReport = "";
    private BufferedWaveProvider? _buffer;
    private MMDeviceEnumerator? _enumerator;
    private bool _outputStarted;
    private LiveAnalyzer? _analyzer;
    private AnalysisTap? _tap;
    public double QueuedAudioMs => _buffer?.BufferedDuration.TotalMilliseconds ?? 0;
    private double _captureBatchMs;
    public double CaptureBatchMs => Volatile.Read(ref _captureBatchMs);
    public bool LowerLatency { get; set; }
    public bool FastestLatency { get; set; }
    public bool LeanOutput { get; set; }
    public bool LowEnginePeriod { get; set; }
    public bool RealTimeRefill { get; set; }
    public bool DirectCableCapture { get; set; }
    private string _captureRoute = "Not started";
    public AudioTimingProfile ActiveTiming { get; private set; } = AudioTimingProfile.Stable;
    private string _captureFormat = "Not started", _outputMixFormat = "Not started";
    private PlaybackDiagnostics? _diagnostics;
    private MeteredPlaybackProvider? _playbackMeter;
    private string _endpointTiming = "No Windows stream settings recorded yet.\n";
    public EventMonitor? Events { get; private set; }
    public EventLibrary SavedEvents { get; } = new();
    public string AudioDetails => $"GamerSense v0.4.12\nRunning now: {IsRunning}\nMode: {ActiveTiming.DisplayName}\n" +
        $"Requested capture buffer: {ActiveTiming.CaptureBufferMs} ms\nRequested output buffer: {ActiveTiming.OutputLatencyMs} ms\n" +
        (ActiveTiming.LowEnginePeriod ? "Low-period mode: Windows chooses capacity from its supported period; 30 ms request applies only to fallback.\n" : "") +
        $"Prebuffer target: {ActiveTiming.PrebufferMs} ms\nPlayback buffer capacity: {ActiveTiming.BufferCapacityMs} ms\n" +
        $"Queued audio now: {QueuedAudioMs:F1} ms\nLast capture batch: {CaptureBatchMs:F1} ms\n" +
        $"Capture route: {_captureRoute}\nCapture format: {_captureFormat}\nOutput device mix format: {_outputMixFormat}\nMatching enabled: {DetectionEnabled}\n" +
        "Queue/batch values are partial diagnostics, not total end-to-end latency.\n\n" +
        _endpointTiming + "\n" + (_leanOutput?.Report() ?? _leanReport) + "\n" + (_playbackMeter?.Report() ?? "No playback supply readings yet.\n") +
        "\nPLAYBACK SESSION SUMMARY (retained after Stop)\n" + (_diagnostics?.Report() ?? "No playback session recorded yet.");
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
        _endpointTiming = "Windows stream settings unavailable: session startup did not complete.\n";
        _playbackMeter = null;
        _leanReport = "";
        ActiveTiming = DirectCableCapture ? AudioTimingProfile.DirectCable : RealTimeRefill ? AudioTimingProfile.RealTime : LowEnginePeriod ? AudioTimingProfile.LowPeriod : LeanOutput ? AudioTimingProfile.Lean : FastestLatency ? AudioTimingProfile.Fastest : LowerLatency ? AudioTimingProfile.Responsive : AudioTimingProfile.Stable;

        _enumerator = new MMDeviceEnumerator();
        var captureEndpoint = _enumerator.GetDevice(captureDeviceId);
        var outputEndpoint = _enumerator.GetDevice(outputDeviceId);
        if (outputEndpoint.DataFlow != DataFlow.Render)
            throw new InvalidOperationException("Choose a playback device for True output.");
        if (captureEndpoint.DataFlow != (DirectCableCapture ? DataFlow.Capture : DataFlow.Render))
            throw new InvalidOperationException(DirectCableCapture
                ? "Direct cable capture needs a recording device. Choose CABLE Output."
                : "This mode needs a playback device. Choose CABLE Input.");
        _captureRoute = DirectCableCapture ? "Recording endpoint (CABLE Output); no loopback" : "Playback endpoint loopback";
        using (var mixClient = outputEndpoint.AudioClient)
            _outputMixFormat = mixClient.MixFormat.ToString();

        _capture = DirectCableCapture ? new WasapiCapture(captureEndpoint, true, ActiveTiming.CaptureBufferMs)
            : ActiveTiming != AudioTimingProfile.Stable ? new ResponsiveLoopbackCapture(captureEndpoint, ActiveTiming.CaptureBufferMs)
            : new WasapiLoopbackCapture(captureEndpoint);
        _captureFormat = _capture.WaveFormat.ToString();
        _diagnostics = new PlaybackDiagnostics(ActiveTiming, captureEndpoint.FriendlyName, outputEndpoint.FriendlyName,
            _captureFormat, _outputMixFormat);
        _buffer = new BufferedWaveProvider(_capture.WaveFormat)
        {
            BufferDuration = TimeSpan.FromMilliseconds(ActiveTiming.BufferCapacityMs),
            DiscardOnBufferOverflow = true,
            ReadFully = false
        };
        _playbackMeter = new MeteredPlaybackProvider(_buffer);

        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;

        // Shared-mode event sync is appropriate for the prototype and avoids
        // forcing the physical device into a format it does not natively use.
        if (LeanOutput || LowEnginePeriod || RealTimeRefill || DirectCableCapture)
        {
            _leanOutput = new LeanSharedOutput(outputEndpoint, _playbackMeter, ActiveTiming.LowEnginePeriod, ActiveTiming.RealTimeRefill);
            _leanOutput.Faulted += message => Faulted?.Invoke(message);
        }
        else
        {
            _output = new WasapiOut(outputEndpoint, AudioClientShareMode.Shared, true, ActiveTiming.OutputLatencyMs);
            _output.Init(_playbackMeter);
        }

        _outputStarted = false;
        _analyzer = new LiveAnalyzer(_capture.WaveFormat);
        _detector = new ExperimentalDetector(_capture.WaveFormat,
            Path.Combine(AppContext.BaseDirectory, "Models", "wardogs-model.json")) { Enabled = _detectionEnabled };
        var analyzer = _analyzer;
        var detector = _detector;
        var events = Events = new EventMonitor(_capture.WaveFormat);
        events.ClipReady += SavedEvents.Enqueue;
        events.ClipSkipped += SavedEvents.ReportSkipped;
        _tap = new AnalysisTap((data, count) => { analyzer.Tap(data, count); detector.Tap(data, count); events.Tap(data, count); },
            () => { analyzer.Reset(); detector.Reset(); events.MarkGap(); });
        _capture.StartRecording();
        _endpointTiming = EndpointTimingReport.Read(_capture, (object?)_leanOutput ?? _output!) +
            $"Capture scheduling: {(DirectCableCapture || (ActiveTiming != AudioTimingProfile.Stable && ResponsiveLoopbackCapture.UsesEventSync) ? "Windows audio events" : "Polling")}\n";
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
            if (!_outputStarted && (_output is not null || _leanOutput is not null) && _buffer.BufferedDuration.TotalMilliseconds >= ActiveTiming.PrebufferMs)
            {
                if (_leanOutput is not null) _leanOutput.Play(); else _output!.Play();
                _outputStarted = true;
            }
            _leanOutput?.NotifyAudio();
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
        Events?.CompletePending(true);
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
        try { _leanOutput?.Stop(); } catch { }
        if (_leanOutput is not null) _leanReport = _leanOutput.Report();

        _capture?.Dispose();
        _output?.Dispose();
        _leanOutput?.Dispose();
        _enumerator?.Dispose();

        _capture = null;
        _output = null;
        _leanOutput = null;
        _buffer = null;
        _enumerator = null;
    }

    public void Dispose() { Stop(); SavedEvents.Dispose(); }
}

