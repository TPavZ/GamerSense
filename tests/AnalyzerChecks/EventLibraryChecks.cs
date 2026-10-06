using GamerSense.Audio;
using NAudio.Wave;

public static class EventLibraryChecks
{
    public static void Run()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "event-library-test-" + Guid.NewGuid().ToString("N"));
        var format = new WaveFormat(8000, 16, 1);
        byte[] quiet = new byte[format.AverageBytesPerSecond / 10];
        for (int i = 0; i < quiet.Length; i += 2) BitConverter.GetBytes((short)500).CopyTo(quiet, i);
        var observer = new EventMonitor(format, seconds: 6, sourceName: "test source");
        observer.PeakThresholdDb = 0; observer.RiseThresholdDb = 1000;
        var original = quiet.ToArray();
        try
        {
            EventClip? frozen = null;
            using (var library = new EventLibrary(root))
            {
                observer.ClipReady += c => { frozen = c; library.Enqueue(c); };
                for (int i = 0; i < 30; i++) observer.Tap(quiet, quiet.Length);
                var mark = observer.Mark();
                for (int i = 0; i < 29; i++) observer.Tap(quiet, quiet.Length);
                if (frozen is not null) throw new Exception("Saved before complete post-event context");
                observer.Tap(quiet, quiet.Length);
                if (frozen is null || frozen.Marker.Id != mark.Id || frozen.StartSeconds != 1 || frozen.EndSeconds != 6)
                    throw new Exception("Saved event context incorrect");
                for (int i = 0; i < 100; i++) observer.Tap(quiet, quiet.Length);
                if (!quiet.SequenceEqual(original)) throw new Exception("Saving changed captured bytes");
            } // close drains queued disk writes
            using (var reopened = new EventLibrary(root))
            {
                var items = reopened.List();
                if (items.Length != 1 || items[0].Approved || items[0].Label != "mixed / uncertain") throw new Exception("Pending clip did not survive restart/ring expiry");
                var loaded = reopened.Load(items[0]);
                if (!loaded.Audio.SequenceEqual(frozen!.Audio) || loaded.Format.SampleRate != 8000 || loaded.SourceName != "test source" || loaded.SessionId != frozen.SessionId)
                    throw new Exception("Saved native WAV or provenance changed");
                var reviewed = reopened.Review(items[0], "explosions / mortars", "Reduce", "nearby impact and own footsteps", .5, 4, true);
                var reloaded = reopened.List().Single();
                var persisted = System.Text.Json.JsonSerializer.Deserialize<SavedEvent>(File.ReadAllText(Path.Combine(root, reviewed.Id + ".json")))!;
                if (!reloaded.Approved || reloaded.Notes != reviewed.Notes || reloaded.Intent != "Reduce" || reloaded.RangeStart != .5 || reloaded.RangeEnd != 4)
                    throw new Exception("Approval/tags/range did not persist");
                if (!persisted.Approved || persisted.Notes != reviewed.Notes || persisted.RangeStart != .5 || persisted.RangeEnd != 4)
                    throw new Exception("Review remained in memory instead of committing to disk");
                bool blocked = false;
                try { reopened.Review(reloaded, "x", "x", "", 4, 1, true); } catch (InvalidOperationException) { blocked = true; }
                if (!blocked || reopened.List().Single().RangeStart != .5) throw new Exception("Invalid review damaged previous metadata");
                blocked = false;
                try { reopened.Delete(reloaded with { Id = "../../outside" }); } catch (InvalidOperationException) { blocked = true; }
                if (!blocked || reopened.List().Length != 1) throw new Exception("Invalid ID escaped library");
                reopened.Delete(reloaded);
                if (reopened.List().Length != 0 || Directory.EnumerateFiles(root, "*.wav").Any() || Directory.EnumerateFiles(root, "*.json").Any()) throw new Exception("Discard left saved audio or tags behind");
                reopened.AutoSaveEnabled = false; reopened.Enqueue(loaded);
                if (reopened.PendingWrites != 0) throw new Exception("Disabled autosave queued data");
            }
            var tail = new EventMonitor(format); tail.RiseThresholdDb = 1000; tail.PeakThresholdDb = 0;
            EventClip? partial = null; tail.ClipReady += c => partial = c;
            for (int i = 0; i < 10; i++) tail.Tap(quiet, quiet.Length);
            tail.Mark(); tail.CompletePending(true);
            if (partial is null || partial.Audio.Length != format.AverageBytesPerSecond) throw new Exception("Stop lost final pending event");
            partial = null; tail.CompletePending(true);
            if (partial is not null) throw new Exception("Repeated stop saved duplicate event");
            int skipped = 0;
            tail.ClipSkipped += _ => skipped++;
            tail.Mark(); tail.MarkGap(); tail.Tap(quiet, quiet.Length); tail.CompletePending(true);
            if (partial is not null || skipped != 1) throw new Exception("Observer gap persisted as valid audio");
            string quotaRoot = Path.Combine(root, "quota");
            using (var limited = new EventLibrary(quotaRoot, frozen!.Audio.Length + 256))
            {
                limited.SaveNow(frozen);
                limited.Enqueue(frozen);
            }
            using (var limited = new EventLibrary(quotaRoot))
                if (limited.List().Length != 1 || Directory.EnumerateFiles(quotaRoot, "*.tmp").Any()) throw new Exception("Storage budget removed existing clip or committed partial save");
            DateTime testTime = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
            string expiryRoot = Path.Combine(root, "expiry");
            SavedEvent approved;
            using (var expiring = new EventLibrary(expiryRoot, frozen!.Audio.Length + 256, utcNow: () => testTime))
            {
                var first = expiring.SaveNow(frozen);
                approved = expiring.Review(first, "gunfire", "Keep", "approved", 0, 2, true);
                testTime = testTime.AddMinutes(59).AddSeconds(59);
                if (expiring.ExpireOldClips() != 0 || expiring.List().Length != 1) throw new Exception("Clip expired before one hour");
                bool full = false;
                try { expiring.SaveNow(frozen); } catch (IOException) { full = true; }
                if (!full || expiring.List().Length != 1) throw new Exception("Full library evicted unexpired clip");
                testTime = testTime.AddSeconds(1);
                // Save reclaims expired audio first, then resumes within budget.
                var resumed = expiring.SaveNow(frozen);
                if (expiring.List().Length != 1 || expiring.List()[0].Id != resumed.Id ||
                    File.Exists(Path.Combine(expiryRoot, approved.Id + ".wav")) || File.Exists(Path.Combine(expiryRoot, approved.Id + ".json")))
                    throw new Exception("One-hour approved expiry or storage resume failed");
            }
            testTime = testTime.AddHours(1);
            using (var restarted = new EventLibrary(expiryRoot, utcNow: () => testTime))
            {
                restarted.ExpireOldClips();
                if (restarted.List().Length != 0 || Directory.EnumerateFiles(expiryRoot, "*.wav").Any()) throw new Exception("Expired clips survived app restart");
                var pending = restarted.SaveNow(frozen);
                testTime = testTime.AddHours(1);
                bool expiredLoad = false;
                try { restarted.Load(pending); } catch (InvalidOperationException) { expiredLoad = true; }
                if (!expiredLoad || restarted.List().Length != 0) throw new Exception("Expired clip loaded from cache");
            }
            // A slow/unavailable disk must never block the producer. Hold the
            // storage lock to simulate a worker stalled in a file operation.
            using (var pressured = new EventLibrary(Path.Combine(root, "pressure"), queueCapacity: 1))
            {
                var gate = typeof(EventLibrary).GetField("_gate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(pressured)!;
                lock (gate)
                {
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    for (int i = 0; i < 20; i++) pressured.Enqueue(frozen);
                    if (clock.ElapsedMilliseconds > 200 || pressured.Skipped == 0) throw new Exception("Slow disk blocked clip producer or unbounded queue");
                }
            }
            Console.WriteLine("PASS saved event library: post-context freeze, native bytes, ring expiry/restart, pending/approval/tags/range, discard, stop tail, gap rejection, storage budget, nonblocking bounded disk queue");
        }
        finally
        {
            if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase))
                throw new Exception("Test cleanup escaped workspace");
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
