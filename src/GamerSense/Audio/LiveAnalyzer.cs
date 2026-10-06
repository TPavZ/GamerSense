using NAudio.Wave;
using NAudio.Dsp;

namespace GamerSense.Audio;

// Read-only tap: never writes into the capture buffer or waits for analysis.
public sealed class LiveAnalyzer : IDisposable
{
    public const int FftSize = 2048;
    private readonly object _gate = new();
    private readonly float[] _ring = new float[FftSize];
    private readonly float[] _frame = new float[FftSize];
    private readonly Complex[] _fft = new Complex[FftSize];
    private readonly System.Threading.Timer _timer;
    private readonly WaveFormat _format;
    private readonly bool _float;
    private readonly bool _supported;
    private int _position, _count;
    private long _lastInput;
    private int _busy;
    private bool _disposed;
    private AnalysisFrame _latest;
    public AnalysisFrame Latest => Volatile.Read(ref _latest);
    public void Reset()
    {
        lock (_gate) { _count = 0; _lastInput = 0; }
    }

    public LiveAnalyzer(WaveFormat format)
    {
        _format = format;
        var encoding = format.Encoding;
        if (format is WaveFormatExtensible ext)
        {
            if (ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71")) encoding = WaveFormatEncoding.IeeeFloat;
            if (ext.SubFormat == new Guid("00000001-0000-0010-8000-00aa00389b71")) encoding = WaveFormatEncoding.Pcm;
        }
        _float = encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32;
        _supported = _float || encoding == WaveFormatEncoding.Pcm && format.BitsPerSample is 16 or 24 or 32;
        _latest = Empty();
        _timer = new System.Threading.Timer(Analyze, null, 0, 50);
    }

    private AnalysisFrame Empty()
    {
        var bins = new float[FftSize / 2];
        Array.Fill(bins, -100f);
        return new(bins, _format.SampleRate, -100, -100, 0, _supported);
    }

    public void Tap(byte[] data, int count)
    {
        if (!_supported || !Monitor.TryEnter(_gate)) return;
        try
        {
            if (_disposed) return;
            int bytes = _format.BitsPerSample / 8;
            // Retain the channel with greatest magnitude per frame to avoid stereo phase cancellation.
            for (int offset = 0; offset + _format.BlockAlign <= count; offset += _format.BlockAlign)
            {
                float sample = 0;
                for (int channel = 0; channel < _format.Channels; channel++)
                {
                    int i = offset + channel * bytes;
                    float value = _float ? BitConverter.ToSingle(data, i) : bytes switch
                    {
                        2 => BitConverter.ToInt16(data, i) / 32768f,
                        3 => ((data[i] | data[i + 1] << 8 | data[i + 2] << 16) << 8 >> 8) / 8388608f,
                        4 => BitConverter.ToInt32(data, i) / 2147483648f,
                        _ => 0
                    };
                    if (float.IsFinite(value) && Math.Abs(value) > Math.Abs(sample)) sample = value;
                }
                _ring[_position] = sample;
                _position = (_position + 1) % FftSize;
                _count = Math.Min(FftSize, _count + 1);
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
            lock (_gate)
            {
                if (_disposed) return;
                if (_count < FftSize || Environment.TickCount64 - _lastInput > 300)
                {
                    Volatile.Write(ref _latest, Empty());
                    return;
                }
                for (int i = 0; i < FftSize; i++) _frame[i] = _ring[(_position + i) % FftSize];
            }
            double sum = 0;
            float peak = 0;
            for (int i = 0; i < FftSize; i++)
            {
                float s = _frame[i];
                sum += s * s;
                peak = Math.Max(peak, Math.Abs(s));
                _fft[i].X = (float)(s * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FftSize - 1))));
                _fft[i].Y = 0;
            }
            FastFourierTransform.FFT(true, 11, _fft);
            var bins = new float[FftSize / 2];
            int dominant = 0;
            float largest = 0;
            for (int i = 0; i < bins.Length; i++)
            {
                float magnitude = 4 * MathF.Sqrt(_fft[i].X * _fft[i].X + _fft[i].Y * _fft[i].Y);
                bins[i] = Db(magnitude);
                if (i > 0 && magnitude > largest) { largest = magnitude; dominant = i; }
            }
            Volatile.Write(ref _latest, new AnalysisFrame(bins, _format.SampleRate, Db(peak), Db((float)Math.Sqrt(sum / FftSize)), largest > 0.0001f ? dominant * _format.SampleRate / (float)FftSize : 0, _supported));
        }
        finally { Volatile.Write(ref _busy, 0); }
    }

    private static float Db(float value) => Math.Clamp(20 * MathF.Log10(Math.Max(value, 0.00001f)), -100, 12);
    public void Dispose()
    {
        lock (_gate) _disposed = true;
        _timer.Dispose();
    }
}

public sealed record AnalysisFrame(float[] BinsDb, int SampleRate, float PeakDb, float RmsDb, float DominantHz, bool Supported);
