using GamerSense.Audio;
using NAudio.Wave;

static async Task Check(WaveFormat format, Func<float, byte[]> encode, string name)
{
    using var analyzer = new LiveAnalyzer(format);
    var data = new List<byte>();
    for (int i = 0; i < 4096; i++)
    {
        float s = 0.5f * MathF.Sin(2 * MathF.PI * 1000 * i / format.SampleRate);
        data.AddRange(encode(s));
        data.AddRange(encode(-s));
    }
    byte[] bytes = data.ToArray(), original = bytes.ToArray();
    analyzer.Tap(bytes, bytes.Length);
    await Task.Delay(120);
    var frame = analyzer.Latest;
    if (!bytes.SequenceEqual(original)) throw new Exception("Capture bytes modified");
    if (Math.Abs(frame.DominantHz - 1000) > 30 || Math.Abs(frame.PeakDb + 6.02) > 0.2 || Math.Abs(frame.RmsDb + 9.03) > 0.3)
        throw new Exception($"{name}: wrong results {frame}");
    await Task.Delay(400);
    if (analyzer.Latest.DominantHz != 0 || analyzer.Latest.PeakDb != -100) throw new Exception("Stale signal");
    analyzer.Tap(new byte[bytes.Length], bytes.Length);
    await Task.Delay(100);
    if (analyzer.Latest.DominantHz != 0) throw new Exception("Silence failed");
    Console.WriteLine($"PASS {name}: tone, opposite-phase stereo, levels, immutable input, idle reset, silence");
}
await Check(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), BitConverter.GetBytes, "float32");
await Check(new WaveFormat(48000, 16, 2), s => BitConverter.GetBytes((short)(s * 32767)), "PCM16");
await Check(new WaveFormat(48000, 24, 2), s => BitConverter.GetBytes((int)(s * 8388607)).Take(3).ToArray(), "PCM24");
await Check(new WaveFormat(48000, 32, 2), s => BitConverter.GetBytes((int)(s * 2147483647)), "PCM32");
await Check(new WaveFormatExtensible(48000, 32, 2), BitConverter.GetBytes, "extensible float32");
using var unsupported = new LiveAnalyzer(new WaveFormat(48000, 8, 2));
if (unsupported.Latest.Supported) throw new Exception("Unsupported format incorrectly accepted");
Console.WriteLine("PASS unsupported format");

var modelPath = Path.Combine(AppContext.BaseDirectory, "Models", "wardogs-model.json");
var model = WardogsModel.Load(modelPath);
using var expectations = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "expected.json")));
foreach (var item in expectations.RootElement.EnumerateArray())
{
    string name = item.GetProperty("name").GetString()!;
    var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name + ".bin"));
    var samples = new float[bytes.Length / 4]; Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
    var features = WardogsModel.ExtractFeatures(samples);
    var expected = item.GetProperty("features").EnumerateArray().Select(x => x.GetDouble()).ToArray();
    double difference = features.Zip(expected).Max(pair => Math.Abs(pair.First - pair.Second));
    if (difference > 1e-6) throw new Exception($"Feature mismatch {name}: {difference}");
    if (model.Predict(samples) != item.GetProperty("prediction").GetString()) throw new Exception("Prediction mismatch");
    Console.WriteLine($"PASS {name}: 47 feature values match Python; prediction matches; max difference {difference:E2}");
}
var floatFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
using (var live = new ExperimentalDetector(floatFormat, modelPath))
{
    var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "g3.bin")); var original = bytes.ToArray();
    await AwaitStatus(live, () => live.Latest.StartsWith("Closest pattern:") || live.Latest == "Ambience / mixed audio", bytes);
    if ((!live.Latest.StartsWith("Closest pattern:") && live.Latest != "Ambience / mixed audio") || !bytes.SequenceEqual(original)) throw new Exception("Live tap failed");
    live.Enabled = false; await AwaitStatus(live, () => live.Latest == "Sound matching off");
    if (live.Latest != "Sound matching off") throw new Exception("Disable failed");
    live.Enabled = true; await AwaitStatus(live, () => live.Latest == "Quiet audio — no match", new byte[bytes.Length]);
    if (live.Latest != "Quiet audio — no match") throw new Exception("Silence failed: " + live.Latest);
    await AwaitStatus(live, () => live.Latest == "No recent audio");
    if (live.Latest != "No recent audio") throw new Exception("Stale reset failed");
    Console.WriteLine("PASS live tap: immutable buffer, matching, disable, silence, stale reset");
}
using (var missing = new ExperimentalDetector(floatFormat, "missing-model.json"))
{
    await AwaitStatus(missing, () => missing.Latest.Contains("unavailable"));
    if (!missing.Latest.Contains("unavailable")) throw new Exception("Missing model failed");
}
using (var differentRate = new ExperimentalDetector(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2), modelPath))
{
    await AwaitStatus(differentRate, () => differentRate.Latest.Contains("48 kHz"));
    if (!differentRate.Latest.Contains("48 kHz")) throw new Exception("Unsupported format failed");
}
Console.WriteLine("PASS missing model and unsupported sample rate");


static async Task AwaitStatus(ExperimentalDetector detector, Func<bool> condition, byte[]? feed = null)
{
    long until = Environment.TickCount64 + 3000;
    while (!condition() && Environment.TickCount64 < until)
    {
        if (feed is not null) detector.Tap(feed, feed.Length);
        await Task.Delay(40);
    }
    if (!condition()) throw new Exception("Status timeout: " + detector.Latest);
}


var gateInput = new float[48000];
Buffer.BlockCopy(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "g6.bin")), 0, gateInput, 0, 48000 * 4);
var gateFeatures = WardogsModel.ExtractFeatures(gateInput);
var gateRoot = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(modelPath))!;
var gateMean = gateRoot["mean"]!.AsArray().Select(x => x!.GetValue<double>()).ToArray();
var gateScale = gateRoot["scale"]!.AsArray().Select(x => x!.GetValue<double>()).ToArray();
var normalized = gateFeatures.Select((x, i) => (x - gateMean[i]) / gateScale[i]).ToArray();
var gatePath = Path.Combine(AppContext.BaseDirectory, "gate-check.json");
try
{
    foreach (string scenario in new[] { "clear", "ambiguous", "distant" })
    {
        var centers = gateRoot["centers"]!.AsObject();
        foreach (string category in centers.Select(x => x.Key).ToArray())
        {
            double offset = scenario == "ambiguous" ? 0 : category == "reload" ? scenario == "clear" ? 0 : 2 : 10;
            centers[category] = System.Text.Json.JsonSerializer.SerializeToNode(normalized.Select(x => x + offset).ToArray());
        }
        File.WriteAllText(gatePath, gateRoot.ToJsonString());
        string expected = scenario == "clear" ? "reload" : "ambience / mixed audio";
        if (WardogsModel.Load(gatePath).Predict(gateInput) != expected) throw new Exception("Gate failed: " + scenario);
    }
}
finally { File.Delete(gatePath); }
Console.WriteLine("PASS clear reload accepted; ambiguous and distant matches use ambience fallback");
