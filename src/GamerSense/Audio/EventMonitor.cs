using NAudio.Wave;
using System.Text.Json;
namespace GamerSense.Audio;

public sealed record AudioMarker(int Id, double Seconds, string Kind, float PeakDb);
public sealed record LevelPoint(double Seconds, float PeakDb);
public sealed record EventClip(byte[] Audio, WaveFormat Format, double StartSeconds, double EndSeconds, AudioMarker Marker,
    string SourceName = "live session", string SessionId = "");

// Native-format rolling copy, observed after playback. All times use captured audio frames.
public sealed class EventMonitor
{
    private readonly object _gate = new();
    private readonly WaveFormat _format;
    private readonly byte[] _ring;
    private readonly bool _float, _supported;
    private readonly List<AudioMarker> _markers = new();
    private readonly List<LevelPoint> _levels = new();
    private readonly List<long> _gaps = new();
    private readonly string _sourceName;
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private long _frames;
    private int _id;
    private double _lastAuto = -10, _lastLevel = -1;
    private double _baselineDb = -60;
    private bool _wasLoud;
    public double PeakThresholdDb { get; set; } = -18;
    public double RiseThresholdDb { get; set; } = 10;
    public bool Supported => _supported;
    public double Seconds { get { lock (_gate) return _frames / (double)_format.SampleRate; } }
    public EventMonitor(WaveFormat format, int seconds = 60, string sourceName = "live session")
    {
        if (seconds < 1 || seconds > 120) throw new ArgumentOutOfRangeException(nameof(seconds));
        _sourceName = sourceName;
        _format = format;
        _ring = new byte[checked(format.AverageBytesPerSecond * seconds)];
        var encoding = format.Encoding;
        if (format is WaveFormatExtensible ext)
        {
            if (ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71")) encoding = WaveFormatEncoding.IeeeFloat;
            if (ext.SubFormat == new Guid("00000001-0000-0010-8000-00aa00389b71")) encoding = WaveFormatEncoding.Pcm;
        }
        _float = encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32;
        _supported = _float || encoding == WaveFormatEncoding.Pcm && format.BitsPerSample is 16 or 24 or 32;
    }
    public void Tap(byte[] data, int count)
    {
        if (!_supported) return;
        count -= count % _format.BlockAlign;
        if (count <= 0 || count > data.Length) return;
        float peak = 0; double sum = 0; int values = 0;
        int bytes = _format.BitsPerSample / 8;
        for (int i = 0; i + bytes <= count; i += bytes)
        {
            float value = _float ? BitConverter.ToSingle(data, i) : bytes switch
            {
                2 => BitConverter.ToInt16(data, i) / 32768f,
                3 => ((data[i] | data[i+1] << 8 | data[i+2] << 16) << 8 >> 8) / 8388608f,
                4 => BitConverter.ToInt32(data, i) / 2147483648f,
                _ => 0
            };
            if (!float.IsFinite(value)) value = 0;
            peak = Math.Max(peak, Math.Abs(value)); sum += value * value; values++;
        }
        float peakDb = Db(peak); double rmsDb = Db((float)Math.Sqrt(sum / Math.Max(1, values)));
        lock (_gate)
        {
            long firstByte = _frames * _format.BlockAlign;
            int remaining = count, source = 0;
            while (remaining > 0)
            {
                int dest = (int)((firstByte + source) % _ring.Length);
                int length = Math.Min(remaining, _ring.Length - dest);
                Buffer.BlockCopy(data, source, _ring, dest, length); source += length; remaining -= length;
            }
            _frames += count / _format.BlockAlign;
            double time = _frames / (double)_format.SampleRate;
            if (time - _lastLevel >= .05)
            {
                _levels.Add(new LevelPoint(time, peakDb)); _lastLevel = time;
                _levels.RemoveAll(x => x.Seconds < time - 60);
            }
            bool loud = peakDb >= PeakThresholdDb && !_wasLoud;
            if (peakDb >= PeakThresholdDb) _wasLoud = true;
            else if (peakDb < PeakThresholdDb - 3) _wasLoud = false;
            bool rise = rmsDb >= -50 && rmsDb - _baselineDb >= RiseThresholdDb;
            if ((loud || rise) && time - _lastAuto >= 2)
            {
                Add(time - count / (2.0 * _format.AverageBytesPerSecond), loud ? "Loud spike" : "Level rise", peakDb);
                _lastAuto = time;
            }
            _baselineDb += (rmsDb - _baselineDb) * .08;
            _gaps.RemoveAll(x => x < _frames - _ring.Length / _format.BlockAlign);
        }
    }
    private void Add(double seconds, string kind, float db)
    {
        _markers.Add(new AudioMarker(++_id, Math.Max(0, seconds), kind, db));
        if (_markers.Count > 200) _markers.RemoveAt(0);
    }
    public void MarkGap()
    {
        lock (_gate) { _gaps.Add(_frames); _baselineDb = -60; _wasLoud = false; }
    }
    public AudioMarker Mark()
    {
        lock (_gate) { Add(_frames / (double)_format.SampleRate, "Manual mark", -100); return _markers[^1]; }
    }
    public AudioMarker[] Markers() { lock (_gate) return _markers.ToArray(); }
    public LevelPoint[] Levels() { lock (_gate) return _levels.ToArray(); }
    public EventClip Extract(AudioMarker marker, double before = 2, double after = 3)
    {
        lock (_gate)
        {
            long start = Math.Max(0, (long)((marker.Seconds - before) * _format.SampleRate));
            long requestedEnd = (long)((marker.Seconds + after) * _format.SampleRate);
            long end = Math.Min(_frames, requestedEnd);
            long earliest = Math.Max(0, _frames - _ring.Length / _format.BlockAlign);
            if (start < earliest || end <= start) throw new InvalidOperationException("This event has left the rolling recording. Review/save newer events.");
            if (_gaps.Any(x => x >= start && x < end)) throw new InvalidOperationException("An observer recording gap crosses this clip. Choose another event.");
            int count = checked((int)((end - start) * _format.BlockAlign));
            var audio = new byte[count];
            int position = (int)((start * _format.BlockAlign) % _ring.Length);
            int first = Math.Min(count, _ring.Length - position);
            Buffer.BlockCopy(_ring, position, audio, 0, first);
            if (first < count) Buffer.BlockCopy(_ring, 0, audio, first, count - first);
            return new EventClip(audio, _format, start / (double)_format.SampleRate, end / (double)_format.SampleRate, marker, _sourceName, _sessionId);
        }
    }
    public static void Save(EventClip clip, string wavPath, string label, string intent, string notes)
    {
        using (var writer = new WaveFileWriter(wavPath, clip.Format)) writer.Write(clip.Audio, 0, clip.Audio.Length);
        var metadata = new { game = "WARDOGS", audio = Path.GetFileName(wavPath), label, intent, notes, verified = true,
            sourceName = clip.SourceName, sessionId = clip.SessionId,
            labelSource = "manual event review", clipStartSessionSeconds = clip.StartSeconds, clipEndSessionSeconds = clip.EndSeconds,
            markerSessionSeconds = clip.Marker.Seconds, markerKind = clip.Marker.Kind,
            markerClipSeconds = clip.Marker.Seconds - clip.StartSeconds, scope = "whole extracted clip; may include overlapping sounds",
            markerInsideSavedRange = clip.Marker.Seconds >= clip.StartSeconds && clip.Marker.Seconds <= clip.EndSeconds,
            sampleRate = clip.Format.SampleRate, channels = clip.Format.Channels };
        File.WriteAllText(Path.ChangeExtension(wavPath, ".json"), JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static float Db(float value) => Math.Clamp(20 * MathF.Log10(Math.Max(value, .00001f)), -100, 12);
}
