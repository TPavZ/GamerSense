using NAudio.Wave;
using System.Security.Cryptography;
using System.Text.Json;

namespace GamerSense.Audio;

public sealed record CategoryCandidate(string Category, string Label, double Distance);
public sealed record EventSuggestion
{
    public string SuggestedLabel { get; init; } = "mixed / uncertain";
    public CategoryCandidate[] Alternatives { get; init; } = [];
    public string ModelId { get; init; } = "";
    public string Reason { get; init; } = "No automatic suggestion";
    public double AnalysisStartClipSeconds { get; init; }
    public double AnalysisEndClipSeconds { get; init; }
    public int WindowsAnalyzed { get; init; }
    public bool NearTie { get; init; }
    public bool MachineGenerated { get; init; } = true;
    public bool IsVerified { get; init; } = false;
    [System.Text.Json.Serialization.JsonIgnore]
    public string Text => Alternatives.Length == 0 ? Reason :
        $"Tentative: {Alternatives[0].Label}" + (NearTie ? " • close alternatives" : "");
}

// Categorize frozen spike clips only, on the event-storage worker. No audio
// callback, signal modifications, automatic approvals or confidence percentages.
public sealed class EventCategorizer
{
    private readonly double[] _mean = [], _scale = [];
    private readonly Dictionary<string, double[][]> _profiles = new();
    private readonly string _modelId = "";
    private readonly string? _unavailable;
    public bool Available => _unavailable is null;
    public EventCategorizer(string path)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            using var doc = JsonDocument.Parse(bytes); var root = doc.RootElement;
            if (root.GetProperty("featureVersion").GetString() != "box3-spectrum-v1-short-padding" ||
                root.GetProperty("sampleRate").GetInt32() != 48000 || root.GetProperty("channels").GetInt32() != 2)
                throw new InvalidDataException("Unsupported feature recipe.");
            static double[] Values(JsonElement element) => element.EnumerateArray().Select(x => x.GetDouble()).ToArray();
            _mean = Values(root.GetProperty("mean")); _scale = Values(root.GetProperty("scale"));
            _profiles = root.GetProperty("prototypes").EnumerateObject().ToDictionary(x => x.Name, x => x.Value.EnumerateArray().Select(Values).ToArray());
            if (_mean.Length != 47 || _scale.Length != 47 || _profiles.Count < 2 ||
                _mean.Any(x => !double.IsFinite(x)) || _scale.Any(x => !double.IsFinite(x) || x <= 0) ||
                _profiles.Values.Any(p => p.Length == 0 || p.Any(v => v.Length != 47 || v.Any(x => !double.IsFinite(x)))))
                throw new InvalidDataException("Invalid profiles.");
            _modelId = "combined-profiles-" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }
        catch { _unavailable = "Category model unavailable; clip saved for manual review"; }
    }
    public static string LabelFor(string category) => category switch
    {
        "footsteps" => "footsteps", "body_movement" => "movement", "gunfire" => "gunfire",
        "air_vehicles" => "air vehicle", "ground_vehicles" => "ground vehicle", "explosions" => "explosions / mortars",
        "other_ambience" => "ambience", "horn_alarm" => "horn / alarm", "building_repair" => "building / repair",
        "detonator_activation" => "detonator activation", "grenade_handling_throw" => "grenade handling / throw",
        "voice" => "speech", "reload" => "reload", "chambering" => "chambering", _ => "mixed / uncertain"
    };
    public CategoryCandidate[] Rank(float[] window)
    {
        if (_unavailable is not null) throw new InvalidOperationException(_unavailable);
        if (window.Any(v => !float.IsFinite(v))) throw new ArgumentException("Nonfinite audio.");
        var feature = WardogsModel.ExtractFeatures(window);
        return _profiles.Select(p => new CategoryCandidate(p.Key, LabelFor(p.Key),
            p.Value.Min(profile => profile.Select((v,i) => Math.Pow((feature[i] - _mean[i]) / _scale[i] - v, 2)).Average())))
            .OrderBy(c => c.Distance).ToArray();
    }
    public EventSuggestion Categorize(EventClip clip)
    {
        EventSuggestion Unknown(string reason) => new() { Reason = reason, ModelId = _modelId };
        if (_unavailable is not null) return Unknown(_unavailable);
        var format = clip.Format; var encoding = format.Encoding;
        if (format is WaveFormatExtensible ext)
        {
            if (ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71")) encoding = WaveFormatEncoding.IeeeFloat;
            if (ext.SubFormat == new Guid("00000001-0000-0010-8000-00aa00389b71")) encoding = WaveFormatEncoding.Pcm;
        }
        bool floating = encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32;
        if (format.SampleRate != 48000 || format.Channels != 2 || !(floating || encoding == WaveFormatEncoding.Pcm && format.BitsPerSample is 16 or 24 or 32))
            return Unknown("Category suggestions need 48 kHz stereo; native clip saved for manual review");
        int frames = clip.Audio.Length / format.BlockAlign;
        if (frames == 0) return Unknown("Empty clip; no category suggestion");
        int anchor = Math.Clamp((int)Math.Round((clip.Marker.Seconds - clip.StartSeconds - .1) * 48000), 0, Math.Max(0, frames - 24000));
        // Three bounded half-second views near the marker, not the entire five-
        // second background. These are tentative mixed-audio similarity ranks.
        var starts = new[] { anchor, Math.Min(anchor + 12000, Math.Max(0, frames - 24000)), Math.Min(anchor + 24000, Math.Max(0, frames - 24000)) }.Distinct().ToArray();
        var distances = new Dictionary<string, List<double>>(); int used = 0, firstUsed = 0, lastUsed = 0;
        int sampleBytes = format.BitsPerSample / 8;
        foreach (int start in starts)
        {
            var samples = new float[48000]; int length = Math.Min(24000, frames - start); double sum = 0;
            for (int frame = 0; frame < length; frame++)
                for (int channel = 0; channel < 2; channel++)
                {
                    int i = (start + frame) * format.BlockAlign + channel * sampleBytes;
                    float value = floating ? BitConverter.ToSingle(clip.Audio, i) : sampleBytes switch
                    { 2 => BitConverter.ToInt16(clip.Audio, i) / 32768f,
                      3 => ((clip.Audio[i] | clip.Audio[i+1] << 8 | clip.Audio[i+2] << 16) << 8 >> 8) / 8388608f,
                      4 => BitConverter.ToInt32(clip.Audio, i) / 2147483648f, _ => 0 };
                    if (!float.IsFinite(value)) value = 0;
                    samples[frame * 2 + channel] = value; sum += value * value;
                }
            if (20 * Math.Log10(Math.Sqrt(sum / Math.Max(1, length * 2)) + 1e-12) < -65) continue;
            foreach (var c in Rank(samples))
            {
                if (!distances.TryGetValue(c.Category, out var values)) distances[c.Category] = values = new();
                values.Add(c.Distance);
            }
            if (used == 0) firstUsed = start;
            lastUsed = start + length; used++;
        }
        if (used == 0) return Unknown("Quiet near the marker; no category suggestion");
        var ranked = distances.Select(p => new CategoryCandidate(p.Key, LabelFor(p.Key), p.Value.Average())).OrderBy(c => c.Distance).Take(3).ToArray();
        bool tie = (ranked[1].Distance - ranked[0].Distance) / Math.Max(ranked[1].Distance, 1e-12) < .15;
        return new EventSuggestion { SuggestedLabel = tie ? "mixed / uncertain" : ranked[0].Label, Alternatives = ranked, ModelId = _modelId,
            AnalysisStartClipSeconds = firstUsed / 48000.0, AnalysisEndClipSeconds = lastUsed / 48000.0, WindowsAnalyzed = used, NearTie = tie,
            Reason = tie ? "Close profiles; review alternatives. The tie rule is uncalibrated." : "Closest learned profiles near the spike; requires human review" };
    }
}
