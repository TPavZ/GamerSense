using GamerSense.Audio;
using NAudio.Wave;
using System.Text.Json;

public static class ReviewRangeChecks
{
    public static void Run()
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        var data = new byte[format.AverageBytesPerSecond * 5];
        for (int i = 0; i < data.Length; i += 4) BitConverter.GetBytes(MathF.Sin(i * .01f) * .2f).CopyTo(data, i);
        var full = new EventClip(data, format, 10, 15, new AudioMarker(1, 12, "Loud spike", -8), "test", "session");
        var original = data.ToArray();
        var range = ReviewRange.Select(full, 1.875, 2.625);
        if (range.Audio.Length != format.AverageBytesPerSecond * .75 || range.StartSeconds != 11.875 || range.EndSeconds != 12.625 ||
            !range.Audio.SequenceEqual(data.Skip(format.AverageBytesPerSecond * 15 / 8).Take(range.Audio.Length)) || !data.SequenceEqual(original))
            throw new Exception("Selected frame offsets or source bytes changed.");
        foreach (var (a,b) in new[] { (-1.0, 2.0), (2.0, 1.0), (0.0, 6.0), (double.NaN, 2.0), (0.0, double.PositiveInfinity), (0.0, 0.0) })
        {
            bool rejected = false; try { ReviewRange.Select(full, a, b); } catch (InvalidOperationException) { rejected = true; }
            if (!rejected) throw new Exception("Invalid range accepted.");
        }
        var tiny = ReviewRange.Select(full, 0, 3.0 / 48000);
        var loop = new ReviewWaveProvider(tiny, true);
        byte[] output = new byte[tiny.Audio.Length * 4 + format.BlockAlign];
        if (loop.Read(output, 0, output.Length) != output.Length ||
            !output.SequenceEqual(Enumerable.Range(0, output.Length).Select(i => tiny.Audio[i % tiny.Audio.Length])))
            throw new Exception("Repeating range lost or changed frames.");
        loop.Repeat = false;
        if (loop.Read(output, 0, output.Length) != tiny.Audio.Length - format.BlockAlign || loop.Read(output, 0, output.Length) != 0)
            throw new Exception("Disabling repeat failed to reach EOF.");
        var once = new ReviewWaveProvider(tiny, false);
        if (once.Read(output, 0, output.Length) != tiny.Audio.Length || !output.Take(tiny.Audio.Length).SequenceEqual(tiny.Audio) || once.Read(output, 0, output.Length) != 0)
            throw new Exception("Single playback changed or repeated the range.");
        string root = Path.Combine(AppContext.BaseDirectory, "review-range-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "approved.wav");
            EventMonitor.Save(full, path, "explosions / mortars", "Unspecified", "C4 on left", true, "useful training sample", "unspecified");
            byte[] wavBefore = File.ReadAllBytes(path), jsonBefore = File.ReadAllBytes(Path.ChangeExtension(path, ".json"));
            var imported = ReviewRange.OpenWav(path);
            if (imported.Label != "explosions / mortars" || imported.Notes != "C4 on left" || imported.Clip.SessionId != "session" || !imported.Clip.Audio.SequenceEqual(data))
                throw new Exception("Approved WAV/metadata import changed context.");
            string selected = ApprovedClipExporter.ExportImported(imported.Clip, ReviewRange.Select(imported.Clip, 1.875, 2.625), path, root, imported.Label, imported.Notes);
            if (selected == path || !File.ReadAllBytes(path).SequenceEqual(wavBefore) || !File.ReadAllBytes(Path.ChangeExtension(path, ".json")).SequenceEqual(jsonBefore))
                throw new Exception("Refining an imported sample overwrote its original.");
            using var reader = new WaveFileReader(selected);
            var saved = new byte[reader.Length]; if (reader.Read(saved,0,saved.Length) != saved.Length) throw new Exception("Short export read.");
            if (!saved.SequenceEqual(range.Audio)) throw new Exception("Exported selection changed native samples.");
            using var meta = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(selected, ".json")));
            var m = meta.RootElement;
            if (m.GetProperty("reviewRangeStartClipSeconds").GetDouble() != 1.875 || m.GetProperty("reviewRangeEndClipSeconds").GetDouble() != 2.625 ||
                m.GetProperty("sourceAudioFile").GetString() != "approved.wav" || !m.GetProperty("selectedRangeReviewed").GetBoolean() || m.GetProperty("markerClipSeconds").GetDouble() != .125)
                throw new Exception("Selected range provenance or marker offset lost.");
            if (ApprovedClipExporter.ExportImported(imported.Clip, range, path, root, imported.Label, imported.Notes) != selected)
                throw new Exception("Retry did not reuse the identical selected export.");
        }
        finally { if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase)) throw new Exception("Cleanup escaped test root."); Directory.Delete(root, true); }
        Console.WriteLine("PASS review ranges: frame-exact trim, invalid boundaries, immutable original, repeat wrap/EOF, approved WAV reopen, selected export and marker provenance, safe retry");
    }
}


