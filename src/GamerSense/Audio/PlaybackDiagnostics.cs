namespace GamerSense.Audio;

// Retained after Stop. Capture adds metrics without waiting for the report reader.
public sealed class PlaybackDiagnostics
{
    private readonly object _gate = new();
    private readonly string _context;
    private readonly long _started = Environment.TickCount64;
    private long _packets;
    private double _queueMin = double.PositiveInfinity, _queueMax, _queueSum, _lastQueue;
    private double _batchMin = double.PositiveInfinity, _batchMax, _batchSum, _lastBatch;
    private double _elapsed;
    public PlaybackDiagnostics(AudioTimingProfile timing, string input, string output, string captureFormat, string outputFormat)
    {
        _context = $"Session mode: {timing.DisplayName}\n" +
            $"Session input: {input}\nSession output: {output}\nCapture format: {captureFormat}\nOutput mix format: {outputFormat}\n" +
            $"Requested buffers: capture {timing.CaptureBufferMs} ms, output {timing.OutputLatencyMs} ms\n" +
            $"Prebuffer: {timing.PrebufferMs} ms; playback capacity: {timing.BufferCapacityMs} ms\n";
    }
    public void Record(double queuedMs, double batchMs)
    {
        if (!double.IsFinite(queuedMs) || !double.IsFinite(batchMs) || queuedMs < 0 || batchMs <= 0 || !Monitor.TryEnter(_gate)) return;
        try
        {
            _packets++;
            _queueMin = Math.Min(_queueMin, queuedMs); _queueMax = Math.Max(_queueMax, queuedMs); _queueSum += queuedMs; _lastQueue = queuedMs;
            _batchMin = Math.Min(_batchMin, batchMs); _batchMax = Math.Max(_batchMax, batchMs); _batchSum += batchMs; _lastBatch = batchMs;
            _elapsed = (Environment.TickCount64 - _started) / 1000.0;
        }
        finally { Monitor.Exit(_gate); }
    }
    public string Report()
    {
        long packets; double queueMin, queueMax, queueAvg, lastQueue, batchMin, batchMax, batchAvg, lastBatch, elapsed;
        lock (_gate)
        {
            packets = _packets;
            queueMin = packets > 0 ? _queueMin : 0; queueMax = _queueMax; queueAvg = packets > 0 ? _queueSum / packets : 0; lastQueue = _lastQueue;
            batchMin = packets > 0 ? _batchMin : 0; batchMax = _batchMax; batchAvg = packets > 0 ? _batchSum / packets : 0; lastBatch = _lastBatch;
            elapsed = _elapsed;
        }
        if (packets == 0) return _context + "No audio packets recorded in this session yet.";
        return _context + $"Observed session duration: {elapsed:F1} s\nCapture packets observed: {packets}\n" +
            $"Last active queue after capture delivery: {lastQueue:F1} ms\nQueue min / avg / max after delivery: {queueMin:F1} / {queueAvg:F1} / {queueMax:F1} ms\n" +
            $"Last active capture batch: {lastBatch:F1} ms\nCapture batch min / avg / max: {batchMin:F1} / {batchAvg:F1} / {batchMax:F1} ms\n" +
            "These readings survive Stop. Queue is measured after adding each capture batch, not total end-to-end latency.";
    }
}
