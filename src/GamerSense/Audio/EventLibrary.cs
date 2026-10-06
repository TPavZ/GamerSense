using System.Text.Json;
using System.Threading.Channels;
using NAudio.Wave;

namespace GamerSense.Audio;

public sealed record SavedEvent
{
    public string Id { get; init; } = "";
    public DateTime CreatedUtc { get; init; }
    public string SourceName { get; init; } = "";
    public string SessionId { get; init; } = "";
    public double StartSeconds { get; init; }
    public double EndSeconds { get; init; }
    public AudioMarker Marker { get; init; } = new(0, 0, "", -100);
    public string Label { get; init; } = "mixed / uncertain";
    public string Intent { get; init; } = "Unsure";
    public string Notes { get; init; } = "";
    public bool Approved { get; init; }
    public bool HasManualEdits { get; init; }
    public EventSuggestion? AutoSuggestion { get; init; }
    public double RangeStart { get; init; }
    public double RangeEnd { get; init; }
    public long AudioBytes { get; init; }
    public bool PostContextComplete => EndSeconds >= Marker.Seconds + 3;
    [System.Text.Json.Serialization.JsonIgnore]
    public string Text => $"{CreatedUtc.ToLocalTime():MM/dd HH:mm:ss} • {(Approved ? "Approved" : HasManualEdits ? "Edited" : "Pending")} • {(HasManualEdits || Approved ? Label : AutoSuggestion?.Text ?? Label)} • {Marker.Kind}" + (PostContextComplete ? "" : " • short tail");
}

// Disk work has its own bounded worker. Clip producers never wait for storage.
// Each JSON file is the commit record for an immutable WAV. One-hour retention.
public sealed class EventLibrary : IDisposable
{
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GamerSense", "SavedEvents");
    public string DirectoryPath { get; }
    public long MaxAudioBytes { get; }
    public static TimeSpan Retention => TimeSpan.FromHours(1);
    private readonly Func<DateTime> _utcNow;
    private readonly Channel<EventClip> _queue;
    private readonly Thread _worker;
    private readonly object _gate = new();
    private Dictionary<string, SavedEvent>? _items;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };
    private long _revision, _skipped;
    private int _pending, _disposed, _enabled = 1;
    private string _lastError = "";
    private readonly Func<EventClip, EventSuggestion>? _categorizer;
    private int _categorize = 1;
    private long _captured;
    private EventSuggestion? _latestSuggestion;
    public bool CategorizeEnabled { get => Volatile.Read(ref _categorize) != 0; set => Volatile.Write(ref _categorize, value ? 1 : 0); }
    public long Captured => Interlocked.Read(ref _captured);
    public string CaptureStatus => !AutoSaveEnabled ? "Automatic spike capture off" :
        $"Captured {Captured} this run • {PendingWrites} awaiting processing" + (Volatile.Read(ref _latestSuggestion) is { } s ? "\nLast capture: " + s.Text : "\nListening for spikes and sudden level rises");
    public bool AutoSaveEnabled { get => Volatile.Read(ref _enabled) != 0; set => Volatile.Write(ref _enabled, value ? 1 : 0); }
    public long Revision => Interlocked.Read(ref _revision);
    public long Skipped => Interlocked.Read(ref _skipped);
    public int PendingWrites => Volatile.Read(ref _pending);
    public string LastError => Volatile.Read(ref _lastError);
    public EventLibrary(string? directory = null, long maxAudioBytes = 2L * 1024 * 1024 * 1024, int queueCapacity = 32, Func<DateTime>? utcNow = null,
        Func<EventClip, EventSuggestion>? categorizer = null)
    {
        if (maxAudioBytes <= 0 || queueCapacity <= 0) throw new ArgumentOutOfRangeException();
        DirectoryPath = Path.GetFullPath(directory ?? DefaultDirectory); MaxAudioBytes = maxAudioBytes;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _categorizer = categorizer;
        _queue = Channel.CreateBounded<EventClip>(new BoundedChannelOptions(queueCapacity)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        _worker = new Thread(Run) { IsBackground = true, Name = "GamerSense event saves", Priority = ThreadPriority.BelowNormal };
        _worker.Start();
    }
    public void ReportSkipped(string reason)
    {
        Interlocked.Increment(ref _skipped); Volatile.Write(ref _lastError, reason); Interlocked.Increment(ref _revision);
    }
    public void Enqueue(EventClip clip)
    {
        if (!AutoSaveEnabled || Volatile.Read(ref _disposed) != 0) return;
        Interlocked.Increment(ref _pending);
        if (!_queue.Writer.TryWrite(clip))
        { Interlocked.Decrement(ref _pending); ReportSkipped("Event save queue was full. This clip was not saved."); }
    }
    private void Run()
    {
        TryExpire();
        var ready = _queue.Reader.WaitToReadAsync().AsTask();
        while (true)
        {
            // Reuse the same pending channel wait during quiet sessions.
            if (!ready.Wait(TimeSpan.FromSeconds(30))) { TryExpire(); continue; }
            if (!ready.GetAwaiter().GetResult()) break;
            while (_queue.Reader.TryRead(out var clip))
            {
                try { SaveNow(clip); }
                catch (Exception ex) { ReportSkipped("Clip not saved: " + ex.Message); }
                finally { Interlocked.Decrement(ref _pending); }
            }
            TryExpire();
            ready = _queue.Reader.WaitToReadAsync().AsTask();
        }
    }
    private void TryExpire()
    {
        try { ExpireOldClips(); }
        catch (Exception ex) { Volatile.Write(ref _lastError, "Expired clip cleanup failed: " + ex.Message); Interlocked.Increment(ref _revision); }
    }
    public int ExpireOldClips()
    {
        lock (_gate)
        {
            int removed = 0;
            foreach (var item in List().Where(x => _utcNow() - x.CreatedUtc >= Retention))
            {
                // GUID validation keeps every delete inside the library root.
                Delete(item); removed++;
            }
            return removed;
        }
    }
    private void CheckFresh(SavedEvent item)
    {
        if (_utcNow() - item.CreatedUtc >= Retention)
        {
            Delete(item);
            throw new InvalidOperationException("This saved clip has expired after one hour. Export useful clips before they expire.");
        }
    }
    private string PathFor(string id, string extension)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidOperationException("Invalid saved clip ID.");
        return Path.Combine(DirectoryPath, id + extension);
    }
    private void WriteMetadata(SavedEvent item)
    {
        var path = PathFor(item.Id, ".json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(item, _json));
        File.Move(path + ".tmp", path, true);
    }
    public SavedEvent SaveNow(EventClip clip)
    {
        // Never classify under the storage lock or on the capture callback.
        EventSuggestion? suggestion = null;
        if (!CategorizeEnabled) suggestion = new() { Reason = "Category suggestions off; clip saved for manual review" };
        else if (_categorizer is not null)
        {
            try { suggestion = _categorizer(clip); }
            catch { suggestion = new() { Reason = "Category suggestion failed; clip saved for manual review" }; }
        }
        lock (_gate)
        {
            Directory.CreateDirectory(DirectoryPath);
            ExpireOldClips();
            long used = Directory.EnumerateFiles(DirectoryPath, "*.wav").Sum(p => new FileInfo(p).Length);
            if (used + clip.Audio.LongLength + 256 > MaxAudioBytes)
                throw new IOException("Saved clips reached the 2 GB budget. New saves pause until clips expire or you delete them.");
            var item = new SavedEvent { Id = Guid.NewGuid().ToString("N"), CreatedUtc = _utcNow(),
                SourceName = clip.SourceName, SessionId = clip.SessionId, StartSeconds = clip.StartSeconds, EndSeconds = clip.EndSeconds,
                Marker = clip.Marker, RangeEnd = clip.EndSeconds - clip.StartSeconds, AudioBytes = clip.Audio.LongLength, AutoSuggestion = suggestion };
            string wav = PathFor(item.Id, ".wav");
            try
            {
                using (var writer = new WaveFileWriter(wav + ".tmp", clip.Format)) writer.Write(clip.Audio, 0, clip.Audio.Length);
                File.Move(wav + ".tmp", wav);
                WriteMetadata(item);
            }
            catch { File.Delete(wav + ".tmp"); File.Delete(wav); File.Delete(PathFor(item.Id, ".json.tmp")); throw; }
            _items?.Add(item.Id, item);
            Volatile.Write(ref _lastError, "");
            Volatile.Write(ref _latestSuggestion, suggestion); Interlocked.Increment(ref _captured);
            Interlocked.Increment(ref _revision); return item;
        }
    }
    public SavedEvent[] List(bool refreshFromDisk = false)
    {
        lock (_gate)
        {
            if (refreshFromDisk) _items = null;
            if (_items is not null) return _items.Values.OrderByDescending(x => x.CreatedUtc).ToArray();
            if (!Directory.Exists(DirectoryPath)) { _items = new(); return Array.Empty<SavedEvent>(); }
            var items = new List<SavedEvent>();
            foreach (string path in Directory.EnumerateFiles(DirectoryPath, "*.json"))
            {
                try
                {
                    var item = JsonSerializer.Deserialize<SavedEvent>(File.ReadAllText(path));
                    if (item is not null && Path.GetFileNameWithoutExtension(path) == item.Id && File.Exists(PathFor(item.Id, ".wav"))) items.Add(item);
                }
                catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException)
                { Volatile.Write(ref _lastError, "A saved clip could not be listed: " + ex.Message); }
            }
            _items = items.ToDictionary(x => x.Id);
            return items.OrderByDescending(x => x.CreatedUtc).ToArray();
        }
    }
    public EventClip Load(SavedEvent item)
    {
        lock (_gate)
        {
            CheckFresh(item);
            using var reader = new WaveFileReader(PathFor(item.Id, ".wav"));
            var bytes = new byte[checked((int)reader.Length)]; int offset = 0, count;
            while (offset < bytes.Length && (count = reader.Read(bytes, offset, bytes.Length - offset)) > 0) offset += count;
            if (offset != bytes.Length) throw new IOException("Saved audio is incomplete.");
            return new EventClip(bytes, reader.WaveFormat, item.StartSeconds, item.EndSeconds, item.Marker, item.SourceName, item.SessionId);
        }
    }
    public SavedEvent Review(SavedEvent item, string label, string intent, string notes, double start, double end, bool approved)
    {
        lock (_gate)
        {
            var path = PathFor(item.Id, ".json");
            var current = JsonSerializer.Deserialize<SavedEvent>(File.ReadAllText(path)) ?? throw new IOException("Saved clip metadata missing.");
            CheckFresh(current);
            if (start < 0 || end <= start || end > current.EndSeconds - current.StartSeconds + .01 || !double.IsFinite(start) || !double.IsFinite(end))
                throw new InvalidOperationException("Choose a valid review range.");
            var updated = current with { Label = label, Intent = intent, Notes = notes, RangeStart = start, RangeEnd = end, Approved = approved, HasManualEdits = true };
            WriteMetadata(updated); if (_items is not null) _items[item.Id] = updated;
            Interlocked.Increment(ref _revision); return updated;
        }
    }
    public void Delete(SavedEvent item)
    {
        lock (_gate)
        {
            File.Delete(PathFor(item.Id, ".wav")); File.Delete(PathFor(item.Id, ".json"));
            _items?.Remove(item.Id); Interlocked.Increment(ref _revision);
        }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.Writer.TryComplete();
        if (Thread.CurrentThread != _worker) _worker.Join(); // Drain on close so saved clips are not abandoned.
    }
}
