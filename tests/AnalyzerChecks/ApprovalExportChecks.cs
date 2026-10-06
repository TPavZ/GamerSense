using GamerSense.Audio;
using NAudio.Wave;
using System.Text.Json;

public static class ApprovalExportChecks
{
    public static void Run()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "approval-export-test-" + Guid.NewGuid().ToString("N"));
        string exports = Path.Combine(root, "approved");
        Directory.CreateDirectory(root);
        try
        {
            DateTime clock = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
            using var library = new EventLibrary(Path.Combine(root, "temporary"), utcNow: () => clock);
            var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
            byte[] bytes = new byte[format.AverageBytesPerSecond * 5];
            for (int i = 0; i < bytes.Length; i += 4) BitConverter.GetBytes(.1f * MathF.Sin(i * .001f)).CopyTo(bytes, i);
            var full = new EventClip(bytes, format, 10, 15, new AudioMarker(12, 12, "Loud spike", -10), "live session", "test-session");
            var item = library.SaveNow(full);
            var first = format.AverageBytesPerSecond;
            var data = bytes.Skip(first).Take(first * 2).ToArray();
            var trimmed = full with { Audio = data, StartSeconds = 11, EndSeconds = 13 };
            string blocked = Path.Combine(root, "blocked"); File.WriteAllText(blocked, "not a directory");
            bool failed = false;
            try { ApprovedClipExporter.Approve(library, item, trimmed, blocked, "gunfire", "nearby shotgun"); }
            catch (IOException) { failed = true; }
            if (!failed || library.List().Length != 1 || !library.Load(item).Audio.SequenceEqual(bytes)) throw new Exception("Failed export removed source clip");
            failed = false;
            try { ApprovedClipExporter.Approve(library, item, trimmed, Path.Combine(library.DirectoryPath, "exports"), "gunfire", "nearby shotgun"); }
            catch (InvalidOperationException) { failed = true; }
            if (!failed || library.List().Length != 1) throw new Exception("Approval allowed exports inside expiring library");
            string wav = ApprovedClipExporter.Approve(library, item, trimmed, exports, "gunfire", "nearby shotgun");
            if (library.List().Length != 0 || File.Exists(Path.Combine(library.DirectoryPath, item.Id + ".wav")) || File.Exists(Path.Combine(library.DirectoryPath, item.Id + ".json")))
                throw new Exception("Approval did not remove source after exporting");
            using (var reader = new WaveFileReader(wav))
            {
                var saved = new byte[reader.Length]; int count = reader.Read(saved, 0, saved.Length);
                if (count != data.Length || !saved.SequenceEqual(data) || reader.WaveFormat.Encoding != WaveFormatEncoding.IeeeFloat) throw new Exception("Approval export changed native trimmed samples");
            }
            using (var json = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(wav, ".json"))))
            {
                var m = json.RootElement;
                if (!m.GetProperty("verified").GetBoolean() || m.GetProperty("label").GetString() != "gunfire" ||
                    m.GetProperty("notes").GetString() != "nearby shotgun" || m.GetProperty("sampleUse").GetString() != "useful training sample" ||
                    m.GetProperty("playbackPreference").GetString() != "unspecified" || m.GetProperty("clipStartSessionSeconds").GetDouble() != 11 ||
                    m.GetProperty("clipEndSessionSeconds").GetDouble() != 13 || m.GetProperty("audio").GetString() != Path.GetFileName(wav))
                    throw new Exception("Approval export labels/range/meaning incorrect");
            }
            var second = library.SaveNow(full);
            string another = ApprovedClipExporter.Approve(library, second, trimmed, exports, "gunfire", "another sample");
            if (another == wav || Directory.EnumerateFiles(exports, "*.wav").Count() != 2 || Directory.EnumerateDirectories(exports).Any())
                throw new Exception("Approval overwrote a previous export or left staging files");
            clock = clock.AddHours(2); library.ExpireOldClips();
            if (!File.Exists(wav) || !File.Exists(Path.ChangeExtension(wav, ".json"))) throw new Exception("Library expiry removed approved exports");
            Console.WriteLine("PASS approve/export: native trimmed WAV + verified useful-sample label, preserve source on failure, outside-library folder guard, remove source only after success, unique exports, permanent exports survive expiry");
        }
        finally
        {
            if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase)) throw new Exception("Test cleanup escaped workspace");
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
