namespace DotCharter.Services.Audio.Coordination;

/// <summary>
///     The section and the phrase in force at the playhead, for
///     <see cref="AudioPlaybackCoordinator" />'s ≤ 60 Hz UI publish (the status line's
///     "Section / Phrase"). A cursor walks to the last event at or before the time, in either
///     direction, so a seek re-finds its place on its own.
///     <para>
///         <b>The owners are latched.</b> Walking BACK from the cursor to the nearest event that
///         names a section, and again for a phrase, on every publish would cost a walk per
///         publish — and on a chart with phrases but no sections the section walk would go to the
///         start of the song every time. So they are walked again only when
///         the cursor crosses an event, the list changes identity or count, or
///         <see cref="StaleAfterMs" /> has passed (the bound on an in-place edit, which raises
///         nothing). The owner's name is read fresh, so a rename shows at once.
///     </para>
///     One instance per coordinator, UI thread only.
/// </summary>
public sealed class PlaybackMarkerCursor
{
    /// <summary>The staleness bound for an in-place event edit.</summary>
    public const long StaleAfterMs = 250;

    private IReadOnlyList<EventPoint>? _events;
    private int _count;
    private int _cursor;
    private int _resolvedCursor = -1;
    private bool _resolvedReached;
    private long _resolvedAtMs;
    private int _sectionOwner = -1;
    private int _phraseOwner = -1;

    /// <summary>How many times the owners were walked for — a test seam.</summary>
    public int ScanCount { get; private set; }

    /// <summary>The section and the phrase at <paramref name="timeMs" />; empty where none has started.</summary>
    public (string Section, string Phrase) Resolve(IReadOnlyList<EventPoint> events, IList<BeatMarker> beats,
        double timeMs, long nowMs)
    {
        if (events.Count == 0)
        {
            _resolvedCursor = -1;
            return ("", "");
        }

        if (_cursor >= events.Count) _cursor = events.Count - 1;
        if (_cursor < 0) _cursor = 0;
        while (_cursor + 1 < events.Count && events[_cursor + 1].Position.GetPositionMs(beats) <= timeMs)
            _cursor++;
        while (_cursor > 0 && events[_cursor].Position.GetPositionMs(beats) > timeMs)
            _cursor--;

        // Only the first event can lie after the time once the cursor has settled.
        var reached = events[_cursor].Position.GetPositionMs(beats) <= timeMs;

        if (!ReferenceEquals(_events, events) || _count != events.Count || _resolvedCursor != _cursor ||
            _resolvedReached != reached || nowMs - _resolvedAtMs >= StaleAfterMs)
        {
            Scan(events, reached ? _cursor : _cursor - 1);
            (_events, _count, _resolvedCursor, _resolvedReached, _resolvedAtMs) =
                (events, events.Count, _cursor, reached, nowMs);
        }

        return (_sectionOwner >= 0 ? events[_sectionOwner].Section : "",
            _phraseOwner >= 0 ? events[_phraseOwner].Phrase : "");
    }

    private void Scan(IReadOnlyList<EventPoint> events, int from)
    {
        ScanCount++;
        _sectionOwner = -1;
        _phraseOwner = -1;
        for (var i = from; i >= 0 && (_sectionOwner < 0 || _phraseOwner < 0); i--)
        {
            if (_sectionOwner < 0 && !string.IsNullOrEmpty(events[i].Section)) _sectionOwner = i;
            if (_phraseOwner < 0 && !string.IsNullOrEmpty(events[i].Phrase)) _phraseOwner = i;
        }
    }
}
