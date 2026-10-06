using NAudio.Wave;

namespace GamerSense.Audio;

// Independent observer. It never alters captured bytes or blocks the playback callback.
public sealed class ExperimentalDetector : IDisposable
{
    private readonly object _gate = new();
    private readonly float[] _ring = new float[48000];
    private readonly float[] _frame = new float[48000];
    private readonly WaveFormat _format;
    private readonly bool _float, _supported;
    private readonly WardogsModel? _model;
    private readonly System.Threading.Timer _timer;
    private int _position, _count, _busy, _reset, _enabled = 1;
    private long _lastInput;
    private bool _disposed;
    private string _latest = "Collecting audio…";
    private readonly string? _unavailable;
    public string Latest => Volatile.Read(ref _latest);
    public bool Enabled
    {
        get => Volatile.Read(ref _enabled) != 0;
        set { Volatile.Write(ref _enabled, value ? 1 : 0); Interlocked.Exchange(ref _reset, 1); }
    }
    public ExperimentalDetector(WaveFormat format, string modelPath)
    {
        _format = format;
        var encoding = format.Encoding;
        if (format is WaveFormatExtensible ext)
        {
            if (ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71")) encoding = WaveFormatEncoding.IeeeFloat;
            if (ext.SubFormat == new Guid("00000001-0000-0010-8000-00aa00389b71")) encoding = WaveFormatEncoding.Pcm;
        }
        _float = encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32;
        _supported = format.SampleRate == 48000 && format.Channels == 2 &&
            (_float || encoding == WaveFormatEncoding.Pcm && format.BitsPerSample is 16 or 24 or 32);
        if (!_supported) _unavailable = "Sound matching needs a 48 kHz stereo input";
        else
        {
            try { _model = WardogsModel.Load(modelPath); }
            catch { _unavailable = "Sound model unavailable; playback continues"; }
        }
        _timer = new System.Threading.Timer(Analyze, null, 250, 250);
    }
    public void Tap(byte[] data, int count)
    {
        if (!Enabled || !_supported || _model is null) return;
        if (!Monitor.TryEnter(_gate)) { Interlocked.Exchange(ref _reset, 1); return; }
        try
        {
            if (_disposed) return;
            if (Interlocked.Exchange(ref _reset, 0) != 0 || Environment.TickCount64 - _lastInput > 300) _count = 0;
            int bytes = _format.BitsPerSample / 8;
            for (int offset = 0; offset + _format.BlockAlign <= count; offset += _format.BlockAlign)
            {
                for (int c = 0; c < 2; c++)
                {
                    int i = offset + c * bytes;
                    float sample = _float ? BitConverter.ToSingle(data, i) : bytes switch
                    {
                        2 => BitConverter.ToInt16(data, i) / 32768f,
                        3 => ((data[i] | data[i + 1] << 8 | data[i + 2] << 16) << 8 >> 8) / 8388608f,
                        4 => BitConverter.ToInt32(data, i) / 2147483648f,
                        _ => 0
                    };
                    _ring[_position] = float.IsFinite(sample) ? sample : 0;
                    _position = (_position + 1) % _ring.Length;
                    _count = Math.Min(_ring.Length, _count + 1);
                }
            }
            _lastInput = Environment.TickCount64;
        }
        finally { Monitor.Exit(_gate); }
    }
    private void Analyze(object? state)
    {
        if (Interlocked.Exchange(ref _busy, 1) != 0) return;
        try
        {
            if (!Enabled) { Volatile.Write(ref _latest, "Sound matching off"); return; }
            if (_unavailable is not null) { Volatile.Write(ref _latest, _unavailable); return; }
            lock (_gate)
            {
                if (_disposed) return;
                if (Environment.TickCount64 - _lastInput > 300)
                {
                    _count = 0; Volatile.Write(ref _latest, "No recent audio"); return;
                }
                if (_count < _ring.Length || Volatile.Read(ref _reset) != 0)
                {
                    Volatile.Write(ref _latest, "Collecting audio…"); return;
                }
                for (int i = 0; i < _frame.Length; i++) _frame[i] = _ring[(_position + i) % _ring.Length];
            }
            double rms = Math.Sqrt(_frame.Select(x => (double)x * x).Average());
            var match = rms < .001 ? null : _model!.Predict(_frame);
            var text = match is null ? "Quiet audio — no match" : match == "ambience / mixed audio" ? "Ambience / mixed audio" : "Closest pattern: " + match;
            if (Enabled && Volatile.Read(ref _reset) == 0) Volatile.Write(ref _latest, text);
        }
        catch { Volatile.Write(ref _latest, "Sound matching unavailable; playback continues"); }
        finally { Volatile.Write(ref _busy, 0); }
    }
    public void Dispose()
    {
        lock (_gate) _disposed = true;
        _timer.Dispose();
    }
}
