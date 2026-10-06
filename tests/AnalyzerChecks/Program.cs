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

using (var entered = new ManualResetEventSlim())
using (var release = new ManualResetEventSlim())
{
    int captured = -1, resets = 0;
    using var tap = new AnalysisTap((data, count) =>
    {
        Interlocked.Exchange(ref captured, data[0]);
        entered.Set(); release.Wait(1000);
    }, () => Interlocked.Increment(ref resets), capacity: 1);
    byte[] source = { 7, 0, 0, 0 };
    tap.Enqueue(source, source.Length);
    if (!entered.Wait(1000)) throw new Exception("Tap did not consume");
    source[0] = 9;
    try
    {
        var enqueue = Task.Run(() => { tap.Enqueue(new byte[] { 2 }, 1); tap.Enqueue(new byte[] { 3 }, 1); });
        if (await Task.WhenAny(enqueue, Task.Delay(1000)) != enqueue) throw new Exception("Analysis queue blocked producer");
        await enqueue;
        if (captured != 7 || tap.DroppedPackets < 1) throw new Exception("Pooled buffer or bounded queue failed");
        await Task.Delay(150); // Force the queued observer packet to become stale.
    }
    finally { release.Set(); }
    long timeout = Environment.TickCount64 + 1000;
    while (tap.DroppedPackets < 2 && Environment.TickCount64 < timeout) await Task.Delay(10);
    if (tap.DroppedPackets < 2) throw new Exception("Stale observer packet was not dropped");
    tap.Enqueue(new byte[] { 4 }, 1);
    timeout = Environment.TickCount64 + 1000;
    while (Volatile.Read(ref captured) != 4 && Environment.TickCount64 < timeout) await Task.Delay(10);
    if (captured != 4 || resets == 0) throw new Exception("Dropped packet gap was not reset");
    tap.Dispose(); tap.Enqueue(new byte[] { 5 }, 1);
}
Console.WriteLine("PASS pooled analysis queue: immutable copies, bounded backlog, nonblocking producer, stale drop, gap reset, disposal");

// Compare the monitored adapter byte-for-byte to the former playback provider,
// including reads that cross the available audio boundary and nonzero offsets.
foreach (var format in new[] { WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), new WaveFormat(48000, 16, 2), new WaveFormat(48000, 24, 2) })
{
    var reference = new BufferedWaveProvider(format) { ReadFully = true };
    var monitored = new BufferedWaveProvider(format) { ReadFully = false };
    var provider = new MeteredPlaybackProvider(monitored);
    var originalAudio = Enumerable.Range(0, 10 * format.BlockAlign).Select(n => (byte)(n + 1)).ToArray();
    var preserved = originalAudio.ToArray();
    reference.AddSamples(originalAudio, 0, originalAudio.Length);
    monitored.AddSamples(originalAudio, 0, originalAudio.Length);
    foreach (int frames in new[] { 4, 8, 1 })
    {
        int count = frames * format.BlockAlign;
        var oldBytes = Enumerable.Repeat((byte)0xCC, count + 12).ToArray();
        var newBytes = oldBytes.ToArray();
        if (reference.Read(oldBytes, 5, count) != provider.Read(newBytes, 5, count) || !oldBytes.SequenceEqual(newBytes))
            throw new Exception("Playback adapter changed audio or offset guards");
    }
    if (!preserved.SequenceEqual(originalAudio) || provider.ReadCount != 3 || provider.ShortReadCount != 2 ||
        Math.Abs(provider.MissingAudioMs - 1000.0 * 3 / format.SampleRate) > 1e-9)
        throw new Exception("Playback starvation accounting incorrect");
}
Console.WriteLine("PASS playback supply meter: byte-identical passthrough/zero fill, offset guards, partial/empty reads, duration accounting");

// Sweep allocated capacities and existing padding: never overwrite queued frames
// or fill beyond the target. A second pass without consumption must write nothing.
foreach (int capacity in new[] { 480, 1056, 1440 })
foreach (int target in new[] { 240, 480, 2000 })
for (int padding = 0; padding <= capacity; padding++)
{
    int write = LeanSharedOutput.FramesToWrite(capacity, target, padding);
    if (write < 0 || write > capacity - padding || padding + write > Math.Max(padding, Math.Min(target, capacity)) ||
        LeanSharedOutput.FramesToWrite(capacity, target, padding + write) != 0)
        throw new Exception("Lean renderer write would exceed free space or queue target");
}
if (LeanSharedOutput.FramesToWrite(1056, 480, 0) != 480 || LeanSharedOutput.FramesToWrite(1056, 480, 240) != 240)
    throw new Exception("Lean renderer filled allocated capacity instead of target");
if (LeanSharedOutput.FramesToWrite(288, 144, 0, 48) != 48 || LeanSharedOutput.FramesToWrite(288, 144, 48, 0) != 0 ||
    LeanSharedOutput.FramesToWrite(288, 144, 48, 1000) != 96)
    throw new Exception("Short-period renderer precommitted unavailable samples");
try { LeanSharedOutput.FramesToWrite(480, 480, 481); throw new Exception("Invalid padding accepted"); }
catch (ArgumentOutOfRangeException) { }
Console.WriteLine("PASS lean render scheduling: capacity/target/padding sweep, no overwrite, idempotent refill, invalid padding rejected");

// Source pauses must not consume future zero-filled packets. Drain partial
// batches at a render target smaller than capture, then verify ordered bytes.
var directFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
var directBuffer = new BufferedWaveProvider(directFormat) { ReadFully = false };
var directMeter = new MeteredPlaybackProvider(directBuffer);
var directInput = Enumerable.Range(0, 1440 * directFormat.BlockAlign).Select(n => (byte)(n % 251 + 1)).ToArray();
var directResult = new List<byte>();
for (int packet = 0; packet < 3; packet++)
{
    if (LeanSharedOutput.FramesToWrite(1440, 144, 0, directMeter.AvailableFrames) != 0)
        throw new Exception("Direct refill wrote into a source pause");
    directBuffer.AddSamples(directInput, packet * 480 * directFormat.BlockAlign, 480 * directFormat.BlockAlign);
    while (directMeter.AvailableFrames > 0)
    {
        int frames = LeanSharedOutput.FramesToWrite(1440, 144, 0, directMeter.AvailableFrames);
        var bytes = new byte[frames * directFormat.BlockAlign];
        directMeter.Read(bytes, 0, bytes.Length); directResult.AddRange(bytes);
    }
}
if (!directInput.SequenceEqual(directResult) || directMeter.ShortReadCount != 0 || directMeter.MissingAudioMs != 0)
    throw new Exception("Direct refill introduced silence, lost samples, or reordered packets");
Console.WriteLine("PASS direct refill: source gaps, partial packets, ordered byte-identical output, no future silence committed");

if (LowPeriodSupport.SelectMinimum(4, 48, 448) != 48 || LowPeriodSupport.SelectMinimum(4, 49, 448) != 52)
    throw new Exception("Shared engine period alignment failed");
foreach (var range in new[] { (0u, 48u, 448u), (4u, 49u, 50u), (4u, 0u, 100u), (4u, 100u, 48u) })
{
    try { LowPeriodSupport.SelectMinimum(range.Item1, range.Item2, range.Item3); throw new Exception("Invalid engine range accepted"); }
    catch (ArgumentException) { }
}
var nativeFloatMix = new WaveFormatExtensible(48000, 32, 2);
if (!LowPeriodSupport.CanPassFloatThrough(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), nativeFloatMix) ||
    LowPeriodSupport.CanPassFloatThrough(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2), nativeFloatMix) ||
    LowPeriodSupport.CanPassFloatThrough(new WaveFormat(48000, 32, 2), nativeFloatMix) ||
    LowPeriodSupport.CanPassFloatThrough(WaveFormat.CreateIeeeFloatWaveFormat(48000, 1), nativeFloatMix))
    throw new Exception("Low-period format guard failed");
var interop = typeof(LowPeriodSupport).Assembly.GetType("GamerSense.Audio.ISharedPeriodClient")!;
var nativeMethods = interop.GetMethods().OrderBy(method => method.MetadataToken).ToArray();
if (!interop.IsImport || nativeMethods.Length != 18 ||
    nativeMethods[15].Name != "GetSharedModeEnginePeriod" || nativeMethods[16].Name != "GetCurrentSharedModeEnginePeriod" ||
    nativeMethods[17].Name != "InitializeSharedAudioStream" || nativeMethods.Any(method => method.ReturnType != typeof(int)))
    throw new Exception("IAudioClient3 vtable ordering incorrect");
Console.WriteLine("PASS low-period support: aligned supported ranges, format guards, native COM method slots");

if (AudioTimingProfile.Stable != new AudioTimingProfile(100, 30, 200, 40)) throw new Exception("Stable settings changed");
if (AudioTimingProfile.Responsive.PrebufferMs != AudioTimingProfile.Stable.PrebufferMs ||
    AudioTimingProfile.Responsive.OutputLatencyMs != AudioTimingProfile.Stable.OutputLatencyMs ||
    AudioTimingProfile.Responsive.CaptureBufferMs >= AudioTimingProfile.Stable.CaptureBufferMs ||
    AudioTimingProfile.Responsive.BufferCapacityMs <= AudioTimingProfile.Responsive.PrebufferMs)
    throw new Exception("Invalid responsive profile");
using (var engine = new AudioEngine())
{
    if (engine.LowerLatency || engine.ActiveTiming != AudioTimingProfile.Stable) throw new Exception("Stable default changed");
    if (!engine.AudioDetails.Contains("not total end-to-end latency")) throw new Exception("Misleading timing report");
}
var legacySettings = System.Text.Json.JsonSerializer.Deserialize<GamerSense.Settings.AppSettings>("{\"InputDeviceId\":\"input-123\",\"OutputDeviceId\":\"output-456\"}")!;
if (legacySettings.LowerLatency || legacySettings.InputDeviceId != "input-123" || legacySettings.OutputDeviceId != "output-456") throw new Exception("Legacy selection compatibility failed");
var eventSettings = System.Text.Json.JsonSerializer.Deserialize<GamerSense.Settings.AppSettings>("{\"LowerLatency\":true}")!;
if (!eventSettings.LowerLatency || eventSettings.FastestLatency || legacySettings.FastestLatency) throw new Exception("Legacy mode migration failed");
var fastestSettings = System.Text.Json.JsonSerializer.Deserialize<GamerSense.Settings.AppSettings>(
    System.Text.Json.JsonSerializer.Serialize(new GamerSense.Settings.AppSettings { LowerLatency = true, FastestLatency = true, InputDeviceId = "saved-input" }))!;
if (!fastestSettings.FastestLatency || !fastestSettings.LowerLatency || fastestSettings.InputDeviceId != "saved-input") throw new Exception("Fastest mode memory failed");
if (fastestSettings.LeanOutput || eventSettings.LeanOutput || legacySettings.LeanOutput) throw new Exception("Existing modes changed to lean output");
var leanSettings = System.Text.Json.JsonSerializer.Deserialize<GamerSense.Settings.AppSettings>(
    System.Text.Json.JsonSerializer.Serialize(new GamerSense.Settings.AppSettings { LeanOutput = true, InputDeviceId = "saved-input" }))!;
if (!leanSettings.LeanOutput || leanSettings.InputDeviceId != "saved-input") throw new Exception("Lean mode memory failed");
if (leanSettings.LowEnginePeriod || fastestSettings.LowEnginePeriod || eventSettings.LowEnginePeriod) throw new Exception("Existing modes changed to low-period mode");
var periodSettings = System.Text.Json.JsonSerializer.Deserialize<GamerSense.Settings.AppSettings>(
    System.Text.Json.JsonSerializer.Serialize(new GamerSense.Settings.AppSettings { LowEnginePeriod = true, OutputDeviceId = "saved-output" }))!;
if (!periodSettings.LowEnginePeriod || periodSettings.OutputDeviceId != "saved-output") throw new Exception("Low-period mode memory failed");
if (periodSettings.RealTimeRefill || leanSettings.RealTimeRefill || eventSettings.RealTimeRefill) throw new Exception("Existing modes changed to direct refill");
var directSettings = System.Text.Json.JsonSerializer.Deserialize<GamerSense.Settings.AppSettings>(
    System.Text.Json.JsonSerializer.Serialize(new GamerSense.Settings.AppSettings { RealTimeRefill = true, InputDeviceId = "saved-input" }))!;
if (!directSettings.RealTimeRefill || directSettings.InputDeviceId != "saved-input") throw new Exception("Direct refill mode memory failed");
Console.WriteLine("PASS stable defaults, shorter responsive profile, diagnostic labeling, legacy device-selection compatibility");

var retained = new PlaybackDiagnostics(AudioTimingProfile.Stable, "input", "output", "float48k", "float48k");
retained.Record(40, 50); retained.Record(70, 50);
string prior = retained.Report();
if (!prior.Contains("40.0 / 55.0 / 70.0") || !prior.Contains("50.0 / 50.0 / 50.0") || !prior.Contains("Capture packets observed: 2")) throw new Exception("Session metrics incorrect");
retained.Record(0, 0); retained.Record(double.NaN, 1);
if (retained.Report() != prior) throw new Exception("Stopped/invalid metrics overwrote retained session");
var fresh = new PlaybackDiagnostics(AudioTimingProfile.Stable, "input", "output", "float48k", "float48k");
if (!fresh.Report().Contains("No audio packets")) throw new Exception("New session retained prior metrics");
Console.WriteLine("PASS retained session timing, valid sample accounting, and fresh session reset");

var eventFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
var monitor = new EventMonitor(eventFormat, seconds: 6);
byte[] signal = new byte[eventFormat.AverageBytesPerSecond / 10];
for (int i = 0; i < signal.Length; i += 4) Buffer.BlockCopy(BitConverter.GetBytes(.05f), 0, signal, i, 4);
for (int i = 0; i < 30; i++) monitor.Tap(signal, signal.Length);
var manual = monitor.Mark();
for (int i = 0; i < signal.Length; i += 4) Buffer.BlockCopy(BitConverter.GetBytes(.8f), 0, signal, i, 4);
var originalSignal = signal.ToArray();
for (int i = 0; i < 30; i++) monitor.Tap(signal, signal.Length);
if (!signal.SequenceEqual(originalSignal)) throw new Exception("Event monitor changed source bytes");
var markers = monitor.Markers();
if (!markers.Any(x => x.Kind == "Loud spike") || !markers.Any(x => x.Kind == "Manual mark")) throw new Exception("Event markers missing");
if (markers.Count(x => x.Kind == "Loud spike") != 1) throw new Exception("Sustained loud audio repeatedly marked");
var clip = monitor.Extract(manual, 1, 2);
if (Math.Abs(clip.StartSeconds - 2) > .001 || Math.Abs(clip.EndSeconds - 5) > .001 || clip.Audio.Length != eventFormat.AverageBytesPerSecond * 3) throw new Exception("Event extraction bounds wrong");
var savePath = Path.Combine(AppContext.BaseDirectory, "event-export-test.wav");
try
{
    EventMonitor.Save(clip, savePath, "explosions / mortars", "Reduce", "movement overlap");
    using var wav = new WaveFileReader(savePath);
    if (Math.Abs(wav.TotalTime.TotalSeconds - 3) > .001 || wav.WaveFormat.SampleRate != 48000) throw new Exception("Saved WAV invalid");
    using var metadata = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(savePath, ".json")));
    if (metadata.RootElement.GetProperty("intent").GetString() != "Reduce") throw new Exception("Intent label missing");
}
finally { File.Delete(savePath); File.Delete(Path.ChangeExtension(savePath, ".json")); }
monitor.MarkGap();
var gapMarker = monitor.Mark(); monitor.Tap(signal, signal.Length);
bool blockedGap = false;try { monitor.Extract(gapMarker, 1, 0); } catch (InvalidOperationException) { blockedGap = true; }
// A gap exactly at the clip end is outside the extracted interval. Include post-event data.
try { monitor.Extract(gapMarker, 1, .1); } catch (InvalidOperationException) { blockedGap = true; }
if (!blockedGap) throw new Exception("Recording gap accepted");
for (int i = 0; i < 70; i++) monitor.Tap(signal, signal.Length);
bool expired = false; try { monitor.Extract(manual, 1, 2); } catch (InvalidOperationException) { expired = true; }
if (!expired) throw new Exception("Expired event accepted");
if (!clip.Audio.Any(x => x != 0)) throw new Exception("Frozen clip lost contents after ring wrap");
Console.WriteLine("PASS event monitoring: spike/manual markers, sustained audio cooldown, immutable bytes, context extraction, WAV/JSON intent export, gap rejection, ring expiry, frozen clip retention");

using (var demo = new AudioFileReader(Path.Combine(AppContext.BaseDirectory, "Samples", "demo-vehicles.wav")))
{
    var offline = new EventMonitor(demo.WaveFormat, sourceName: "demo-vehicles.wav");
    var bytes = new byte[demo.WaveFormat.AverageBytesPerSecond / 20]; int count;
    while ((count = demo.Read(bytes, 0, bytes.Length)) > 0) offline.Tap(bytes, count);
    if (offline.Markers().Length == 0 || Math.Abs(offline.Seconds - demo.TotalTime.TotalSeconds) > .01) throw new Exception("Offline demo import failed");
    var eventClip = offline.Extract(offline.Markers()[0]);
    if (eventClip.SourceName != "demo-vehicles.wav" || eventClip.Audio.Length == 0) throw new Exception("Offline event review failed");
}
Console.WriteLine("PASS offline vehicle demo: decoding, activity markers, duration, and context extraction");
