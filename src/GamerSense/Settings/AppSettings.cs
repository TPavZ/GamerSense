using System.Text.Json;

namespace GamerSense.Settings;

public sealed class AppSettings
{
    public string? InputDeviceId { get; set; }
    public string? OutputDeviceId { get; set; }
    public bool LowerLatency { get; set; }
    public bool FastestLatency { get; set; }
    public bool LeanOutput { get; set; }
    public bool LowEnginePeriod { get; set; }
    public bool RealTimeRefill { get; set; }
    public bool DirectCableCapture { get; set; }
    public string? DirectCaptureDeviceId { get; set; }
    public bool AutoSaveEvents { get; set; } = true;
    public bool AutoCategorizeEvents { get; set; } = true;
    public string? ApprovedExportDirectory { get; set; }
    public GamerSense.Audio.VolumeLevels Volumes { get; set; } = new();

    private static string SettingsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GamerSense");

    private static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new AppSettings();

            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Settings persistence should never prevent GamerSense from running.
        }
    }
}
