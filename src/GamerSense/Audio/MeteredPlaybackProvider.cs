using NAudio.Wave;

namespace GamerSense.Audio;

// Same passthrough/zero-fill contract as BufferedWaveProvider.ReadFully=true,
// with exact short-read accounting. Optional output gain never waits or queues.
public sealed class MeteredPlaybackProvider : IWaveProvider
{
    private readonly BufferedWaveProvider _source;
    private readonly Action<byte[], int, int>? _transform;
    private long _reads, _shortReads, _missingBytes;
    public MeteredPlaybackProvider(BufferedWaveProvider source, Action<byte[], int, int>? transform = null)
    {
        if (source.ReadFully) throw new ArgumentException("Source must expose short reads.", nameof(source));
        _source = source;
        _transform = transform;
    }
    public WaveFormat WaveFormat => _source.WaveFormat;
    public int AvailableFrames => _source.BufferedBytes / WaveFormat.BlockAlign;
    public long ReadCount => Interlocked.Read(ref _reads);
    public long ShortReadCount => Interlocked.Read(ref _shortReads);
    public double MissingAudioMs => 1000.0 * Interlocked.Read(ref _missingBytes) / WaveFormat.AverageBytesPerSecond;
    public int Read(byte[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        if (count == 0) return 0;
        Interlocked.Increment(ref _reads);
        if (read < count)
        {
            Array.Clear(buffer, offset + read, count - read);
            Interlocked.Increment(ref _shortReads);
            Interlocked.Add(ref _missingBytes, count - read);
        }
        _transform?.Invoke(buffer, offset, read);
        return count;
    }
    public string Report() => $"PLAYBACK SUPPLY (retained after Stop)\nOutput reads: {ReadCount}\n" +
        $"Reads needing silence fill: {ShortReadCount}\nTotal silence fill: {MissingAudioMs:F1} ms\n" +
        "Silence fill indicates missing captured samples; source pauses also count. It is not a count of audible glitches.\n";
}
