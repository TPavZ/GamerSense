using GamerSense.Audio;
using NAudio.Wave;
using System.Text.Json;

public static class EventCategorizerChecks
{
    public static void Run()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "categorizer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        var audio = new byte[format.AverageBytesPerSecond];
        for (int i = 0; i < audio.Length; i += 4) BitConverter.GetBytes(.2f * MathF.Sin(i * .07f)).CopyTo(audio, i);
        var clip = new EventClip(audio, format, 0, 1, new AudioMarker(1, .2, "Loud spike", -8), "synthetic", "test");
        var categorizer = new EventCategorizer(Path.Combine(AppContext.BaseDirectory, "Models", "spike-profiles.json"));
        var suggestion = categorizer.Categorize(clip);
        if (suggestion.WindowsAnalyzed == 0 || suggestion.Alternatives.Length != 3 || suggestion.IsVerified ||
            !suggestion.MachineGenerated || !suggestion.Alternatives.All(x => double.IsFinite(x.Distance)) ||
            !suggestion.Alternatives.SequenceEqual(suggestion.Alternatives.OrderBy(x => x.Distance)))
            throw new Exception("Combined-profile ranking or unverified status failed.");
        if (categorizer.Categorize(clip with { Audio = new byte[audio.Length] }).WindowsAnalyzed != 0 ||
            categorizer.Categorize(clip with { Format = new WaveFormat(44100, 16, 1) }).WindowsAnalyzed != 0 ||
            new EventCategorizer(Path.Combine(root, "missing.json")).Categorize(clip).Alternatives.Length != 0)
            throw new Exception("Unknown/unsupported fallback failed.");
        File.WriteAllText(Path.Combine(root, "bad.json"), "{}");
        if (new EventCategorizer(Path.Combine(root, "bad.json")).Categorize(clip).Alternatives.Length != 0)
            throw new Exception("Invalid model fallback failed.");
        var proposed = new EventSuggestion { SuggestedLabel = "gunfire", Alternatives = [new("gunfire", "gunfire", 1)], ModelId = "test-model", WindowsAnalyzed = 1 };
        using (var library = new EventLibrary(Path.Combine(root, "library"), categorizer: _ => proposed))
        {
            var item = library.SaveNow(clip);
            if (item.Approved || item.Label != "mixed / uncertain" || item.AutoSuggestion?.SuggestedLabel != "gunfire" || !library.Load(item).Audio.SequenceEqual(audio))
                throw new Exception("Capture altered samples or treated machine guess as approval.");
            item = library.Review(item, "explosions / mortars", "Unspecified", "corrected", 0, 1, false);
            using (var reopened = new EventLibrary(library.DirectoryPath))
            {
                var restored = reopened.List().Single();
                if (!restored.HasManualEdits || restored.Approved || restored.Label != item.Label || restored.AutoSuggestion?.SuggestedLabel != "gunfire")
                    throw new Exception("Pending correction and original suggestion did not survive reopening.");
            }
            string wav = ApprovedClipExporter.Approve(library, item, clip, Path.Combine(root, "exports"), item.Label, item.Notes);
            using var metadata = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(wav, ".json")));
            var m = metadata.RootElement;
            if (!m.GetProperty("verified").GetBoolean() || !m.GetProperty("humanCorrectedSuggestion").GetBoolean() ||
                m.GetProperty("autoSuggestion").GetProperty("SuggestedLabel").GetString() != "gunfire" || library.List().Length != 0)
                throw new Exception("Approval lost suggestion/correction provenance.");
        }
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using (var library = new EventLibrary(Path.Combine(root, "automatic"), categorizer: categorizer.Categorize))
        {
            var monitor = new EventMonitor(format);
            monitor.ClipReady += library.Enqueue;
            byte[] quiet = new byte[format.AverageBytesPerSecond / 10];
            for (int i = 0; i < 20; i++) monitor.Tap(quiet, quiet.Length);
            monitor.Tap(audio, audio.Length);
            for (int i = 0; i < 40; i++) monitor.Tap(quiet, quiet.Length);
            if (!SpinWait.SpinUntil(() => library.Captured > 0 && library.PendingWrites == 0, 10000)) throw new Exception("Automatic spike was not frozen, classified and saved.");
            var item = library.List().First();
            if (item.Approved || item.AutoSuggestion?.WindowsAnalyzed is not > 0 || library.Load(item).Audio.Length == 0)
                throw new Exception("Automatic pipeline produced no reviewable unverified sample.");
        }
        using (var library = new EventLibrary(Path.Combine(root, "worker"), queueCapacity: 2, categorizer: _ => { entered.Set(); release.Wait(); return proposed; }))
        {
            library.Enqueue(clip);
            if (!entered.Wait(5000)) throw new Exception("Background categorizer did not start.");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 10; i++) library.Enqueue(clip);
            if (watch.ElapsedMilliseconds > 250 || library.Skipped == 0) throw new Exception("Analysis blocked producer or queue was unbounded.");
            release.Set();
            if (!SpinWait.SpinUntil(() => library.PendingWrites == 0, 10000)) throw new Exception("Background captures failed to drain.");
            if (library.List().Any(x => x.Approved)) throw new Exception("Worker automatically approved a sample.");
        }
        Console.WriteLine("PASS captured-sound categorization: combined profiles, unknown fallback, unchanged native audio, pending corrections, original suggestion provenance, explicit approval and bounded background queue");
    }
}
