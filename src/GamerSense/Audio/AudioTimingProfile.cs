using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GamerSense.Audio;

public sealed record AudioTimingProfile(int CaptureBufferMs, int OutputLatencyMs, int BufferCapacityMs, int PrebufferMs)
{
    public static AudioTimingProfile Stable { get; } = new(100, 30, 200, 40);
    public static AudioTimingProfile Responsive { get; } = new(20, 20, 80, 20);
}

// NAudio's standard loopback constructor does not expose the capture-buffer size.
// Keep shared-mode loopback flags, but request a shorter polling buffer in test mode.
public sealed class ResponsiveLoopbackCapture : WasapiCapture
{
    public ResponsiveLoopbackCapture(MMDevice endpoint, int bufferMs)
        : base(endpoint, false, bufferMs) { }
    protected override AudioClientStreamFlags GetAudioClientStreamFlags()
        => AudioClientStreamFlags.Loopback | base.GetAudioClientStreamFlags();
}
