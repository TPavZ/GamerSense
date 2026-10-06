using System.Reflection;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GamerSense.Audio;

// NAudio 2.2.1 keeps its initialized clients private. Diagnostics only:
// never initialize, change, or dispose these borrowed clients. MMDevice.AudioClient
// would create a different, uninitialized stream and report the wrong information.
internal static class EndpointTimingReport
{
    public static string Read(WasapiCapture capture, object output) =>
        "WINDOWS STREAM SETTINGS (retained after Stop)\n" +
        ReadClient("Capture", capture, typeof(WasapiCapture), capture.WaveFormat.SampleRate) +
        ReadClient("Output", output, typeof(WasapiOut), output is LeanSharedOutput lean ? lean.WaveFormat.SampleRate : ((WasapiOut)output).OutputWaveFormat.SampleRate) +
        "Allocated capacity and driver-reported latency are partial readings; do not add them to estimate total audible delay.\n" +
        "A zero driver latency means no useful latency value was reported, not zero end-to-end delay.\n";

    private static string ReadClient(string name, object stream, Type owner, int sampleRate)
    {
        try
        {
            var client = stream is LeanSharedOutput lean ? lean.Client :
                owner.GetField("audioClient", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(stream) as AudioClient;
            if (client is null) return $"{name}: initialized client unavailable with this NAudio version.\n";
            // Query once after Init/StartRecording, outside the audio callback.
            string ReadValue(Func<string> get)
            {
                try { return get(); }
                catch (Exception ex) { return $"unavailable ({ex.GetType().Name})"; }
            }
            var capacity = ReadValue(() => $"{client.BufferSize} frames / {1000.0 * client.BufferSize / sampleRate:F1} ms");
            var latency = ReadValue(() => $"{client.StreamLatency / 10000.0:F1} ms");
            var period = ReadValue(() => $"{client.DefaultDevicePeriod / 10000.0:F1} ms");
            var minimum = ReadValue(() => $"{client.MinimumDevicePeriod / 10000.0:F1} ms");
            return $"{name} actual allocated buffer: {capacity}\n{name} driver-reported stream latency: {latency}\n" +
                $"{name} default / minimum device period: {period} / {minimum}\n";
        }
        catch (Exception ex) { return $"{name} timing unavailable ({ex.GetType().Name}). Playback settings unchanged.\n"; }
    }
}
