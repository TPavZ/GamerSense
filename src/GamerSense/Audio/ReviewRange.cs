using NAudio.Wave;
using System.Text.Json;

namespace GamerSense.Audio;

public static class ReviewRange
{
    public static EventClip Select(EventClip source, double start, double end)
    {
        double duration = source.Audio.Length / (double)source.Format.AverageBytesPerSecond;
        if (!double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end <= start || end > duration + 1.0 / source.Format.SampleRate)
            throw new InvalidOperationException("Choose a start and end within the clip.");
        int frames = source.Audio.Length / source.Format.BlockAlign;
        int first = (int)Math.Round(start * source.Format.SampleRate);
        int last = Math.Min(frames, (int)Math.Round(end * source.Format.SampleRate));
        if (first >= last) throw new InvalidOperationException("Select at least one audio frame.");
        var audio = source.Audio.AsSpan(first * source.Format.BlockAlign, (last - first) * source.Format.BlockAlign).ToArray();
        return source with { Audio = audio, StartSeconds = source.StartSeconds + first / (double)source.Format.SampleRate,
            EndSeconds = source.StartSeconds + last / (double)source.Format.SampleRate };
    }

    public static (EventClip Clip, string Label, string Notes) OpenWav(string path)
    {
        using var reader = new WaveFileReader(path);
        bool floating = reader.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat || reader.WaveFormat is WaveFormatExtensible ext && ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");
        bool pcm = reader.WaveFormat.Encoding == WaveFormatEncoding.Pcm || reader.WaveFormat is WaveFormatExtensible pcmExt && pcmExt.SubFormat == new Guid("00000001-0000-0010-8000-00aa00389b71");
        if (!(floating && reader.WaveFormat.BitsPerSample == 32 || pcm && reader.WaveFormat.BitsPerSample is 8 or 16 or 24 or 32))
            throw new InvalidOperationException("Use a PCM or 32-bit float WAV clip.");
        if (reader.TotalTime.TotalSeconds <= 0 || reader.TotalTime.TotalSeconds > 60)
            throw new InvalidOperationException("Choose a WAV clip up to 60 seconds long.");
        var audio = new byte[checked((int)reader.Length)];
        int position = 0;
        while (position < audio.Length)
        {
            int read = reader.Read(audio, position, audio.Length - position);
            if (read == 0) throw new IOException("The WAV ended before its declared audio length.");
            position += read;
        }
        string label = "mixed / uncertain", notes = "", session = Guid.NewGuid().ToString("N"), source = Path.GetFileName(path), kind = "Imported clip";
        double start = 0, marker = 0;
        string json = Path.ChangeExtension(path, ".json");
        if (File.Exists(json))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(json));
            var m = doc.RootElement;
            string Text(string key, string fallback) => m.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? fallback : fallback;
            double Number(string key, double fallback) => m.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var n) && double.IsFinite(n) ? n : fallback;
            label = Text("label", label); notes = Text("notes", notes); session = Text("sessionId", session); source = Text("sourceName", source);
            start = Math.Max(0, Number("clipStartSessionSeconds", 0)); marker = Number("markerSessionSeconds", start); kind = Text("markerKind", kind);
        }
        double duration = audio.Length / (double)reader.WaveFormat.AverageBytesPerSecond;
        return (new EventClip(audio, reader.WaveFormat, start, start + duration, new AudioMarker(0, marker, kind, -100), source, session), label, notes);
    }
}

// Review-only provider. Repetition reuses the selected native bytes and never
// joins the game playback pipeline or changes the exported waveform.
public sealed class ReviewWaveProvider : IWaveProvider
{
    private readonly byte[] _audio;
    private int _position;
    private volatile bool _repeat;
    public WaveFormat WaveFormat { get; }
    public bool Repeat { get => _repeat; set => _repeat = value; }
    public double PositionSeconds => Volatile.Read(ref _position) / (double)WaveFormat.AverageBytesPerSecond;
    public ReviewWaveProvider(EventClip clip, bool repeat)
    {
        if (clip.Audio.Length == 0 || clip.Audio.Length % clip.Format.BlockAlign != 0) throw new ArgumentException("Invalid review audio.");
        _audio = clip.Audio; WaveFormat = clip.Format; Repeat = repeat;
    }
    public int Read(byte[] buffer, int offset, int count)
    {
        int written = 0;
        while (written < count)
        {
            if (_position == _audio.Length)
            {
                if (!Repeat) break;
                _position = 0;
            }
            int copy = Math.Min(count - written, _audio.Length - _position);
            Buffer.BlockCopy(_audio, _position, buffer, offset + written, copy);
            _position += copy; written += copy;
        }
        return written;
    }
}
