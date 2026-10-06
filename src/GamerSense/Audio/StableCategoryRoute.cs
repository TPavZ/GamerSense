namespace GamerSense.Audio;

// Holds a known route through one short ambiguous estimate, not indefinitely.
// A different route still requires two fresh estimates. Clear evidence for
// other audio, silence, source gaps and disabling do not extend the hold.
public sealed class StableCategoryRoute
{
    public string? Category { get; private set; }
    private string? _candidate;
    private int _consecutive;
    private long _confirmedAt;
    public long ExpiresAt => _confirmedAt + 250;
    public void Reset() { Category = null; _candidate = null; _consecutive = 0; _confirmedAt = 0; }
    public string? Update(string? candidate, bool releaseNow, long now)
    {
        if (releaseNow) { Reset(); return null; }
        if (candidate is null) { _candidate = null; _consecutive = 0; }
        else if (candidate == Category) { _confirmedAt = now; _candidate = null; _consecutive = 0; }
        else
        {
            _consecutive = candidate == _candidate ? _consecutive + 1 : 1;
            _candidate = candidate;
            if (_consecutive >= 2) { Category = candidate; _confirmedAt = now; _candidate = null; _consecutive = 0; }
        }
        if (Category is not null && now - _confirmedAt > 250) Category = null;
        return Category;
    }
}
