using GamerSense.Audio;
using NAudio.Wave;

public static class BulkDeleteChecks
{
    public static void Run()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "bulk-delete-" + Guid.NewGuid().ToString("N"));
        using var library = new EventLibrary(root);
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        var clip = new EventClip(new byte[format.AverageBytesPerSecond], format, 0, 1, new AudioMarker(1, .5, "Loud spike", -8));
        var pending = library.SaveNow(clip);
        var edited = library.SaveNow(clip);
        library.Review(edited, "gunfire", "Unspecified", "pending edit", 0, 1, false);
        var approved = library.SaveNow(clip);
        var snapshot = library.List();
        library.Review(approved, "gunfire", "Unspecified", "approved after snapshot", 0, 1, true);
        var later = library.SaveNow(clip);
        var permanent = Path.Combine(root, "approved-exports"); Directory.CreateDirectory(permanent);
        var export = Path.Combine(permanent, "keep.wav"); File.WriteAllBytes(export, [1, 2, 3]);
        if (library.DeletePending(snapshot) != 2) throw new Exception("Bulk deletion did not include edited pending clips or recheck approval.");
        var kept = library.List(true);
        if (kept.Length != 2 || !kept.Any(x => x.Id == approved.Id) || !kept.Any(x => x.Id == later.Id) || !File.Exists(export))
            throw new Exception("Bulk deletion removed approved exports, approved items or later captures.");
        if (File.Exists(Path.Combine(root, pending.Id + ".wav")) || File.Exists(Path.Combine(root, pending.Id + ".json")) || library.DeletePending(snapshot) != 0)
            throw new Exception("Bulk deletion did not remove both files or handle a repeated snapshot.");
        Console.WriteLine("PASS bulk pending deletion: edited clips, WAV/label removal, approval recheck, later captures and approved exports preserved");
    }
}
