using System.Security.Cryptography;
using System.Text;

namespace GamerSense.Audio;

public static class ApprovedClipExporter
{
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "GamerSense", "ApprovedClips");

    // Commit both exports before removing the source. A failed export always
    // leaves the pending clip available. Stable filenames allow safe retry.
    public static string Approve(EventLibrary library, SavedEvent item, EventClip range, string directory, string label, string notes)
    {
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var source = Path.TrimEndingDirectorySeparator(library.DirectoryPath);
        if (target.Equals(source, StringComparison.OrdinalIgnoreCase) || target.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose an export folder outside the temporary saved-clip library.");
        if (!Guid.TryParseExact(item.Id, "N", out _)) throw new InvalidOperationException("Invalid saved clip.");
        // The fresh source check prevents approving an expired/deleted item.
        var original = library.Load(item);
        if (range.SessionId != original.SessionId || range.Marker.Id != original.Marker.Id || range.StartSeconds < original.StartSeconds ||
            range.EndSeconds > original.EndSeconds + 1.0 / range.Format.SampleRate || range.Audio.Length == 0)
            throw new InvalidOperationException("Review range does not belong to this saved clip.");
        Directory.CreateDirectory(target);
        string fingerprint = Convert.ToHexString(SHA256.HashData(range.Audio)) + "\n" + label + "\n" + notes + "\n" + range.StartSeconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint)))[..12].ToLowerInvariant();
        string name = $"wardogs-{item.Id}-{hash}.wav";
        string wav = Path.Combine(target, name), json = Path.ChangeExtension(wav, ".json");
        string staging = Path.Combine(target, ".pending-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            string temporaryWav = Path.Combine(staging, name), temporaryJson = Path.ChangeExtension(temporaryWav, ".json");
            EventMonitor.Save(range, temporaryWav, label, "Unspecified", notes, true, "useful training sample", "unspecified");
            if (File.Exists(wav) || File.Exists(json))
            {
                if (!File.Exists(wav) || !File.Exists(json) ||
                    !SHA256.HashData(File.ReadAllBytes(wav)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(temporaryWav))) ||
                    File.ReadAllText(json) != File.ReadAllText(temporaryJson))
                    throw new IOException("An export with this name already exists and differs. Choose another folder; the source clip was kept.");
            }
            else
            {
                File.Move(temporaryWav, wav);
                try { File.Move(temporaryJson, json); }
                catch { File.Delete(wav); throw; }
            }
            // Source deletion failure leaves the permanent pair intact for retry.
            library.Delete(item);
            return wav;
        }
        finally
        {
            // Only the explicit files created in this operation are removed.
            File.Delete(Path.Combine(staging, name));
            File.Delete(Path.Combine(staging, Path.ChangeExtension(name, ".json")));
            Directory.Delete(staging);
        }
    }
}
