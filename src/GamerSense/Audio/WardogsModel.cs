using System.Numerics;
using System.Text.Json;

namespace GamerSense.Audio;

// The feature recipe is shared with the offline training script and parity-tested.
public sealed class WardogsModel
{
    private readonly double[] _mean, _scale;
    private readonly Dictionary<string, double[]> _centers;
    private readonly Dictionary<string, (double MinMargin, double MaxDistance)> _rejection;
    private WardogsModel(double[] mean, double[] scale, Dictionary<string, double[]> centers,
        Dictionary<string, (double MinMargin, double MaxDistance)> rejection)
        => (_mean, _scale, _centers, _rejection) = (mean, scale, centers, rejection);

    public static WardogsModel Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        if (root.GetProperty("featureVersion").GetString() != "box3-spectrum-v1" ||
            root.GetProperty("sampleRate").GetInt32() != 48000 || root.GetProperty("channels").GetInt32() != 2)
            throw new InvalidDataException("Incompatible model");
        static double[] Read(JsonElement element) => element.EnumerateArray().Select(x => x.GetDouble()).ToArray();
        var mean = Read(root.GetProperty("mean"));
        var scale = Read(root.GetProperty("scale"));
        var centers = root.GetProperty("centers").EnumerateObject().ToDictionary(x => x.Name, x => Read(x.Value));
        if (mean.Length != 47 || scale.Length != 47 || centers.Count == 0 ||
            mean.Any(x => !double.IsFinite(x)) || scale.Any(x => !double.IsFinite(x) || x <= 0) ||
            centers.Values.Any(x => x.Length != 47 || x.Any(v => !double.IsFinite(v))))
            throw new InvalidDataException("Invalid model values");
        if (!root.TryGetProperty("rejectionVersion", out var version) || version.GetString() != "margin-distance-v1")
            throw new InvalidDataException("Model needs rejection rules");
        var rejection = root.GetProperty("rejection").EnumerateObject().ToDictionary(x => x.Name,
            x => (MinMargin: x.Value.GetProperty("minMargin").GetDouble(), MaxDistance: x.Value.GetProperty("maxDistance").GetDouble()));
        if (centers.Count < 2 || centers.Keys.Any(c => !rejection.ContainsKey(c)) ||
            rejection.Values.Any(v => !double.IsFinite(v.MinMargin) || v.MinMargin < 0 || v.MinMargin > 1 ||
                !double.IsFinite(v.MaxDistance) || v.MaxDistance <= 0))
            throw new InvalidDataException("Invalid rejection rules");
        return new WardogsModel(mean, scale, centers, rejection);
    }

    public string Predict(float[] stereo)
    {
        var features = ExtractFeatures(stereo);
        var ranked = _centers.Select(pair => (Category: pair.Key,
            Distance: pair.Value.Select((v, i) => Math.Pow((features[i] - _mean[i]) / _scale[i] - v, 2)).Average()))
            .OrderBy(x => x.Distance).ToArray();
        var best = ranked[0];
        double margin = (ranked[1].Distance - best.Distance) / Math.Max(ranked[1].Distance, 1e-12);
        var gate = _rejection[best.Category];
        return margin >= gate.MinMargin && best.Distance <= gate.MaxDistance ? best.Category : "ambience / mixed audio";
    }

    public static double[] ExtractFeatures(float[] stereo)
    {
        if (stereo.Length != 48000) throw new ArgumentException("Expected 0.5 seconds of 48 kHz stereo");
        var audio = new double[8000, 2];
        for (int i = 0; i < 8000; i++)
            for (int c = 0; c < 2; c++)
                audio[i, c] = (stereo[i * 6 + c] + (double)stereo[i * 6 + 2 + c] + stereo[i * 6 + 4 + c]) / 3;
        const int frames = 47;
        var bands = new double[frames, 20];
        var levels = new double[frames];
        var zcrs = new double[frames];
        var crests = new double[frames];
        var fft = new Complex[512];
        var power = new double[257];
        for (int frame = 0; frame < frames; frame++)
        {
            Array.Clear(power);
            double sum = 0, peak = 0, crossings = 0;
            for (int c = 0; c < 2; c++)
            {
                for (int i = 0; i < 512; i++)
                {
                    double sample = audio[frame * 160 + i, c];
                    sum += sample * sample;
                    peak = Math.Max(peak, Math.Abs(sample));
                    if (i > 0 && (sample < 0) != (audio[frame * 160 + i - 1, c] < 0)) crossings++;
                    fft[i] = new Complex(sample * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / 511)), 0);
                }
                Transform(fft);
                for (int i = 0; i < power.Length; i++) power[i] += fft[i].Magnitude * fft[i].Magnitude / 2;
            }
            double rms = Math.Sqrt(sum / 1024 + 1e-12);
            levels[frame] = 20 * Math.Log10(rms);
            zcrs[frame] = crossings / 1022;
            crests[frame] = peak / (rms + 1e-8);
            double bandMean = 0;
            for (int b = 0; b < 20; b++)
            {
                double low = 50 * Math.Pow(160, b / 20.0), high = 50 * Math.Pow(160, (b + 1) / 20.0), energy = 0;
                for (int i = 0; i < power.Length; i++)
                    if (i * 31.25 >= low && i * 31.25 < high) energy += power[i];
                bands[frame, b] = Math.Log10(energy + 1e-10);
                bandMean += bands[frame, b] / 20;
            }
            for (int b = 0; b < 20; b++) bands[frame, b] -= bandMean;
        }
        var features = new double[47];
        for (int b = 0; b < 20; b++)
        {
            var values = Enumerable.Range(0, frames).Select(i => bands[i, b]).ToArray();
            features[b] = values.Average(); features[b + 20] = Std(values);
        }
        features[40] = levels.Average(); features[41] = Std(levels);
        Array.Sort(levels);
        features[42] = Percentile(levels, .1); features[43] = Percentile(levels, .9);
        features[44] = zcrs.Average(); features[45] = Std(zcrs); features[46] = crests.Average();
        return features;
    }
    private static double Std(double[] values)
    {
        double mean = values.Average();
        return Math.Sqrt(values.Select(x => (x - mean) * (x - mean)).Average());
    }
    private static double Percentile(double[] sorted, double fraction)
    {
        double index = (sorted.Length - 1) * fraction;
        int low = (int)index;
        return sorted[low] + (sorted[Math.Min(low + 1, sorted.Length - 1)] - sorted[low]) * (index - low);
    }
    private static void Transform(Complex[] values)
    {
        for (int i = 1, j = 0; i < values.Length; i++)
        {
            int bit = values.Length >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (values[i], values[j]) = (values[j], values[i]);
        }
        for (int length = 2; length <= values.Length; length <<= 1)
        {
            var root = Complex.FromPolarCoordinates(1, -2 * Math.PI / length);
            for (int start = 0; start < values.Length; start += length)
            {
                var factor = Complex.One;
                for (int j = 0; j < length / 2; j++)
                {
                    var a = values[start + j]; var b = values[start + j + length / 2] * factor;
                    values[start + j] = a + b; values[start + j + length / 2] = a - b;
                    factor *= root;
                }
            }
        }
    }
}
