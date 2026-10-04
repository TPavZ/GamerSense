namespace GamerSense.Audio;

public sealed record AudioDeviceInfo(string Id, string Name)
{
    public override string ToString() => Name;
}
