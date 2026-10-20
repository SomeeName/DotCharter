namespace DotCharter.Services.Editing.Tracks;

/// <summary>
///     Whether an event list carries any SECTION and any PHRASE — the two facts
///     <c>ChartRenderer.TopRibbon</c> auto-collapses its rows on, read on every frame and by
///     every hit test. The answer is a walk of the list, and on a chart with phrases but no
/// sections the walk never stops early, so it is latched: it reruns only when the
///     list changes identity or count, the caller's content version moves (the editor's
///     undo-history revision, which an in-place event edit moves), or <see cref="StaleAfterMs" />
///     has passed — the bound on an edit that bypasses the history. Same contract as
///     <see cref="HandShapes.HandShapeSuggestionLatch" />. One instance per renderer; not
///     thread-safe.
/// </summary>
public sealed class RibbonEventContentLatch
{
    /// <summary>The staleness bound for an in-place edit no content version saw.</summary>
    public const long StaleAfterMs = 250;

    private IReadOnlyList<EventPoint>? _events;
    private int _count;
    private long _contentVersion;
    private long _builtAtMs;
    private bool _hasSections;
    private bool _hasPhrases;

    /// <summary>How many times the list was walked — a test seam.</summary>
    public int ScanCount { get; private set; }

    /// <summary>Whether <paramref name="events" /> has a non-empty section, and a non-empty phrase.</summary>
    public (bool HasSections, bool HasPhrases) Get(IReadOnlyList<EventPoint> events, long contentVersion, long nowMs)
    {
        if (ReferenceEquals(_events, events) && _count == events.Count && _contentVersion == contentVersion &&
            nowMs - _builtAtMs < StaleAfterMs)
        {
            return (_hasSections, _hasPhrases);
        }

        var hasSections = false;
        var hasPhrases = false;
        for (var i = 0; i < events.Count && !(hasSections && hasPhrases); i++)
        {
            if (!string.IsNullOrEmpty(events[i].Section)) hasSections = true;
            if (!string.IsNullOrEmpty(events[i].Phrase)) hasPhrases = true;
        }

        ScanCount++;
        (_events, _count, _contentVersion, _builtAtMs) = (events, events.Count, contentVersion, nowMs);
        (_hasSections, _hasPhrases) = (hasSections, hasPhrases);
        return (hasSections, hasPhrases);
    }
}
