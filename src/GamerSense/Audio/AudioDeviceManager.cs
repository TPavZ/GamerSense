using NAudio.CoreAudioApi;

namespace GamerSense.Audio;

public sealed class AudioDeviceManager
{
    public IReadOnlyList<AudioDeviceInfo> GetActiveRenderDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator
            .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Select(d => new AudioDeviceInfo(d.ID, d.FriendlyName))
            .OrderBy(d => d.Name)
            .ToList();
    }

    public AudioDeviceInfo? FindVirtualCable()
    {
        return GetActiveRenderDevices().FirstOrDefault(d =>
            d.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase) ||
            d.Name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase));
    }
}
