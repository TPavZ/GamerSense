using NAudio.Wave;
using System.Text.Json;
namespace GamerSense.Audio;

public sealed record LiveRoute(string? Category, string Text, long UpdatedAt, long ExpiresAt = 0);

// The analysis worker fills a native ring. A separate timer ranks the latest
// half-second without holding the ring lock or delaying the output stream.
public sealed class LiveCategoryRouter : IDisposable
{
    private readonly object _gate = new();
    private readonly byte[] _ring;
    private readonly WaveFormat _format;
    private readonly EventCategorizer _model;
    private readonly VolumeControls _controls;
    private readonly Func<EventClip, EventSuggestion> _predict;
    private readonly Dictionary<string, double> _limits = new();
    private readonly System.Threading.Timer _timer;
    private int _position, _count, _busy, _generation;
    private bool _disposed;
    private long _lastInput;
    private long _lastDecisionInput;
    private readonly StableCategoryRoute _stable = new();
    private LiveRoute _latest = new(null, "Category volumes off", 0);
    public LiveRoute Latest => Volatile.Read(ref _latest);
    public string? CurrentCategory { get { var latest = Latest; long now = Environment.TickCount64; return now - latest.UpdatedAt <= 300 && now <= latest.ExpiresAt ? latest.Category : null; } }
    public string Status => !_controls.Levels.Enabled ? "Category volumes off • Overall volume is active" :
        Environment.TickCount64 - Latest.UpdatedAt > 300 ? "No recent category estimate • normal category volume" :
        Latest.Category is not null && CurrentCategory is null ? "Estimate released • normal category volume" : Latest.Text;
    public LiveCategoryRouter(WaveFormat format, VolumeControls controls, string modelPath,
        Func<EventClip, EventSuggestion>? predict = null)
    {
        _format = format; _controls = controls; _model = new(modelPath); _predict = predict ?? _model.Categorize;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(modelPath));
            foreach (var p in doc.RootElement.GetProperty("liveRoutingMaxDistance").EnumerateObject())
                if (p.Value.GetDouble() is var d && double.IsFinite(d) && d > 0) _limits[p.Name] = d;
        }
        catch { } // No routing limits means neutral category gain, not guessing.
        _ring = new byte[Math.Max(format.BlockAlign, format.AverageBytesPerSecond / 2)];
        _timer = new System.Threading.Timer(Analyze, null, 125, 125);
    }
    public void Reset()
    {
        lock (_gate) { _count = 0; _stable.Reset(); _lastDecisionInput = 0; _generation++; }
        Volatile.Write(ref _latest, new(null, "Collecting a fresh category estimate", Environment.TickCount64));
    }
    public void Tap(byte[] data, int count)
    {
        if (!_controls.Levels.Enabled) { Reset(); return; }
        count -= count % _format.BlockAlign;
        if (count <= 0 || count > data.Length) return;
        lock (_gate)
        {
            if (_disposed) return;
            if (Environment.TickCount64 - _lastInput > 300) { _count = 0; _stable.Reset(); _generation++; }
            // Oversized packets keep only the newest half-second.
            int start = Math.Max(0, count - _ring.Length);
            for (int i = start; i < count;)
            {
                int length = Math.Min(count-i,_ring.Length-_position);
                Buffer.BlockCopy(data,i,_ring,_position,length);
                i+=length; _position=(_position+length)%_ring.Length;
            }
            _count = Math.Min(_ring.Length, _count + count - start); _lastInput = Environment.TickCount64;
        }
    }
    private void Analyze(object? state)
    {
        if (Interlocked.Exchange(ref _busy,1) != 0) return;
        try
        {
            if (!_controls.Levels.Enabled) return;
            byte[] audio; int generation; long captured;
            lock (_gate)
            {
                if (_disposed) return;
                captured = _lastInput; generation = _generation;
                if (_count < _ring.Length || Environment.TickCount64 - captured > 300) { _stable.Reset(); return; }
                if (captured <= _lastDecisionInput) return;
                audio = new byte[_ring.Length];
                int tail = _ring.Length - _position;
                Buffer.BlockCopy(_ring,_position,audio,0,tail); Buffer.BlockCopy(_ring,0,audio,tail,_position);
            }
            var suggestion = _predict(new(audio,_format,0,.5,new(0,0,"Live routing",-100)));
            var candidates = suggestion.Alternatives;
            var best = candidates.FirstOrDefault();
            bool strong = best is not null && candidates.Length > 1 && suggestion.WindowsAnalyzed > 0 &&
                !suggestion.NearTie && _limits.TryGetValue(best.Category,out double limit) && best.Distance <= limit &&
                (candidates[1].Distance-best.Distance)/Math.Max(candidates[1].Distance,1e-9) >= .30;
            string? candidate = strong && best!.Category is "gunfire" or "explosions" or "footsteps" or "ground_vehicles" or "air_vehicles" ? best!.Category : null;
            lock (_gate)
            {
                if (_disposed || generation != _generation || !_controls.Levels.Enabled || Environment.TickCount64 - captured > 300) return;
                bool clearOther = best is null || candidates.Length > 1 && !suggestion.NearTie &&
                    best.Category is not ("gunfire" or "explosions" or "footsteps" or "ground_vehicles" or "air_vehicles") &&
                    (candidates[1].Distance-best.Distance)/Math.Max(candidates[1].Distance,1e-9) >= .30;
                string? routed = _stable.Update(candidate, clearOther, captured);
                _lastDecisionInput = captured;
                string text = routed is not null ? (candidate == routed ? "Estimated section: " : "Briefly holding estimate: ") + EventCategorizer.LabelFor(routed) :
                    best is null ? suggestion.Reason + " • normal category volume" : "Uncertain / other section • normal category volume";
                Volatile.Write(ref _latest,new(routed,text,captured,_stable.ExpiresAt));
            }
        }
        catch { Reset(); }
        finally { Volatile.Write(ref _busy,0); }
    }
    public void Dispose() { lock (_gate) { _disposed = true; _generation++; } _timer.Dispose(); }
}
