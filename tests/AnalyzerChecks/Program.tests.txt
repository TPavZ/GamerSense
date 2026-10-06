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
