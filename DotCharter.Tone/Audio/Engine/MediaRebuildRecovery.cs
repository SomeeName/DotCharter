namespace DotCharter.Tone.Audio.Engine;

/// <summary>Retains rebuild intent across failed engine opens and incomplete media restoration.</summary>
internal sealed class MediaRebuildRecovery<TStems> where TStems : class
{
    internal sealed record Snapshot(string? Path, MediaLoadRequest Request, TStems? Stems);

    private Snapshot? _pending;
    private readonly Func<TStems, TStems, TStems>? _mergeStems;

    public MediaRebuildRecovery(Func<TStems, TStems, TStems>? mergeStems = null) => _mergeStems = mergeStems;

    public bool HasPending => _pending is not null;

    public void ClearStems()
    {
        if (_pending is not null) _pending = _pending with { Stems = null };
    }

    public void Observe(MediaLoadRequest requested)
    {
        if (_pending is not null && !SameSong(_pending.Request, requested)) _pending = null;
    }

    public Snapshot? Capture(string? path, MediaLoadRequest loaded, MediaLoadRequest requested, TStems? stems)
    {
        Observe(requested);
        if (!SameSong(loaded, requested)) return _pending;
        if (_pending is null)
        {
            if (path is not null || stems is not null) _pending = new(path, loaded, stems);
        }
        else
        {
            if (stems is not null && _mergeStems is not null)
            {
                var merged = _pending.Stems is { } retained ? _mergeStems(retained, stems) : stems;
                _pending = _pending with { Stems = merged };
            }
            if (path is not null && loaded != _pending.Request) _pending = _pending with { Path = path, Request = loaded };
        }
        return _pending;
    }

    public void Complete(Snapshot snapshot, bool restored)
    {
        // A stem clear during the replay leaves the same intent without its stems.
        if (restored && (ReferenceEquals(_pending, snapshot) || _pending == (snapshot with { Stems = null }))) _pending = null;
    }

    private static bool SameSong(MediaLoadRequest first, MediaLoadRequest second) =>
        string.Equals(first.Identity, second.Identity, StringComparison.OrdinalIgnoreCase)
        && (first.Identity is not null || first.Generation == second.Generation);
}
