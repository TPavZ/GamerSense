using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GamerSense.Audio;

public sealed record AudioTimingProfile(int CaptureBufferMs, int OutputLatencyMs, int BufferCapacityMs, int PrebufferMs, bool LowEnginePeriod = false)
{
    public static AudioTimingProfile Stable { get; } = new(100, 30, 200, 40);
    // Shorten capture delivery without reducing the clean Stable output timing.
    // The previous 20 ms output/prebuffer test produced crackling on some devices.
    public static AudioTimingProfile Responsive { get; } = new(50, 30, 150, 40);
    public static AudioTimingProfile Fastest { get; } = new(20, 10, 80, 20);
    public static AudioTimingProfile Lean { get; } = new(50, 30, 80, 10);
    public static AudioTimingProfile LowPeriod { get; } = new(50, 30, 80, 10, true);
    public string DisplayName => this == Stable ? "Stable" : this == LowPeriod ? "Low-period output" : this == Lean ? "Lean output" : this == Fastest ? "Minimum delay" : "Event-driven capture";
}

// NAudio's standard loopback constructor does not expose the capture-buffer size.
// Modern Windows supports event-driven loopback: read when Windows signals that
// audio is ready, instead of waiting half the allocated buffer between reads.
// Older Windows retains polling compatibility; Stable still uses NAudio's stock path.
public sealed class ResponsiveLoopbackCapture : WasapiCapture
{
    public static bool UsesEventSync => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 15063);
    public ResponsiveLoopbackCapture(MMDevice endpoint, int bufferMs)
        : base(endpoint, UsesEventSync, bufferMs) { }
    protected override AudioClientStreamFlags GetAudioClientStreamFlags()
        => AudioClientStreamFlags.Loopback | base.GetAudioClientStreamFlags();
}
