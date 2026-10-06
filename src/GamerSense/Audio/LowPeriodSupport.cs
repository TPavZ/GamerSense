using System.Reflection;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GamerSense.Audio;

public static class LowPeriodSupport
{
    private static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");
    public static bool CanPassFloatThrough(WaveFormat source, WaveFormat mix) =>
        source.Encoding == WaveFormatEncoding.IeeeFloat && source.BitsPerSample == 32 && source.Channels <= 2 &&
        (mix.Encoding == WaveFormatEncoding.IeeeFloat || mix is WaveFormatExtensible extended && extended.SubFormat == FloatSubFormat) &&
        source.SampleRate == mix.SampleRate && source.Channels == mix.Channels &&
        source.BitsPerSample == mix.BitsPerSample && source.BlockAlign == mix.BlockAlign;

    public static uint SelectMinimum(uint fundamental, uint minimum, uint maximum)
    {
        if (fundamental == 0 || minimum == 0 || minimum > maximum) throw new ArgumentException("Invalid engine period range.");
        ulong selected = ((ulong)minimum + fundamental - 1) / fundamental * fundamental;
        if (selected > maximum) throw new ArgumentException("No aligned engine period fits this range.");
        return (uint)selected;
    }

    internal static bool TryInitialize(AudioClient client, WaveFormat source, out int frames, out string report)
    {
        frames = 0;
        IntPtr format = IntPtr.Zero;
        try
        {
            var mix = client.MixFormat;
            if (!CanPassFloatThrough(source, mix))
            {
                report = "Low-period output unavailable: capture/output float formats differ; using Lean output.\n";
                return false;
            }
            var native = typeof(AudioClient).GetField("audioClientInterface", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(client);
            if (native is not ISharedPeriodClient periodClient)
            {
                report = "IAudioClient3 unavailable; using Lean output.\n";
                return false;
            }
            format = WaveFormat.MarshalToPtr(mix);
            Marshal.ThrowExceptionForHR(periodClient.GetSharedModeEnginePeriod(format, out uint normal, out uint fundamental, out uint minimum, out uint maximum));
            double scale = 1000.0 / mix.SampleRate;
            report = $"IAudioClient3 shared periods default / minimum / maximum: {normal * scale:F2} / {minimum * scale:F2} / {maximum * scale:F2} ms\n";
            uint selected = SelectMinimum(fundamental, minimum, maximum);
            if (selected >= normal)
            {
                report += "No shorter shared engine period supported; using Lean output.\n";
                return false;
            }
            // This API only supports EventCallback; use the native mix format
            // with verified identical sample layout. No automatic conversion.
            Marshal.ThrowExceptionForHR(periodClient.InitializeSharedAudioStream(AudioClientStreamFlags.EventCallback, selected, format, IntPtr.Zero));
            frames = checked((int)selected);
            report += $"Low-period shared initialization active; selected period: {selected * scale:F2} ms\n";
            // Verify the current engine period; free the returned COM allocation.
            if (periodClient.GetCurrentSharedModeEnginePeriod(out IntPtr currentFormat, out uint currentFrames) >= 0)
            {
                try { report += $"Current shared engine period: {currentFrames * scale:F2} ms\n"; }
                finally { if (currentFormat != IntPtr.Zero) Marshal.FreeCoTaskMem(currentFormat); }
            }
            return true;
        }
        catch (Exception ex)
        {
            report = $"Low-period shared initialization unavailable ({ex.GetType().Name}, 0x{ex.HResult:X8}); using Lean output.\n";
            return false;
        }
        finally { if (format != IntPtr.Zero) Marshal.FreeHGlobal(format); }
    }
}

// Flattened native IAudioClient -> IAudioClient2 -> IAudioClient3 vtable.
// Keep unused base slots: removing/reordering them would call the wrong method.
[ComImport, Guid("7ED4EE07-8E67-4CD4-8C1A-2B7A5987AD42"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISharedPeriodClient
{
    [PreserveSig] int Initialize(AudioClientShareMode mode, AudioClientStreamFlags flags, long duration, long period, IntPtr format, IntPtr session);
    [PreserveSig] int GetBufferSize(out uint frames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint padding);
    [PreserveSig] int IsFormatSupported(AudioClientShareMode mode, IntPtr format, out IntPtr closest);
    [PreserveSig] int GetMixFormat(out IntPtr format);
    [PreserveSig] int GetDevicePeriod(out long normal, out long minimum);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr handle);
    [PreserveSig] int GetService(IntPtr iid, out IntPtr service);
    [PreserveSig] int IsOffloadCapable(int category, out int capable);
    [PreserveSig] int SetClientProperties(IntPtr properties);
    [PreserveSig] int GetBufferSizeLimits(IntPtr format, [MarshalAs(UnmanagedType.Bool)] bool events, out long minimum, out long maximum);
    [PreserveSig] int GetSharedModeEnginePeriod(IntPtr format, out uint normal, out uint fundamental, out uint minimum, out uint maximum);
    [PreserveSig] int GetCurrentSharedModeEnginePeriod(out IntPtr format, out uint frames);
    [PreserveSig] int InitializeSharedAudioStream(AudioClientStreamFlags flags, uint frames, IntPtr format, IntPtr session);
}
