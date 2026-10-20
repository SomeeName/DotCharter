namespace DotCharter.Services.Export.Rocksmith;

/// <summary>
///     Per-frame access to <see cref="RepeatChords" /> for a render loop. The
///     decision is whole-level (the previous sound, the hand shape, the previous
///     chord), so it cannot be windowed like the snapshot's own projections; it
///     is recomputed when the level's collections change identity or count, and
///     when the caller's content version moves — the editor's undo-history
///     revision, so an IN-PLACE edit (a fret, a technique, a resized hand shape)
///     shows on the very frame it requests. Versioned, that is ALL: playback
///     recomputes nothing (the rule costs ~10-16 ms on a 1,000-chord level, a
///     dropped frame if it ran on a timer). Unversioned (a negative version) it
///     also recomputes every <see cref="StaleAfterMs" />, the contract
///     <c>SnapshotUpdateContext.TemplatesVersion</c>'s unversioned latch has.
///     One instance per render loop; not thread-safe.
/// </summary>
public sealed class RepeatChordLatch
{
    /// <summary>The staleness bound for an UNVERSIONED caller's in-place edits.</summary>
    public const long StaleAfterMs = 250;

    private HashSet<Chord> _current = [];
    private bool _built;
    private long _builtAtMs;
    private long _contentVersion;
    private object? _templates, _notes, _chords, _handShapes, _beats;
    private int _templateCount, _noteCount, _chordCount, _handShapeCount, _beatCount;

    /// <summary>How many times the set was recomputed — a test seam.</summary>
    public int ComputeCount { get; private set; }

    /// <summary>The repeat chords of the level these collections describe.
    /// <paramref name="contentVersion" /> is any counter that moves on an edit,
    /// or negative for "unversioned";
    /// <paramref name="nowMs" /> is any monotonic millisecond clock
    /// (<c>Environment.TickCount64</c> in the app).</summary>
    public IReadOnlySet<Chord> Get(IList<ChordTemplate> templates, IList<GuitarNote> notes, IList<Chord> chords,
        IList<HandShape> handShapes, IList<BeatMarker> beats, long contentVersion, long nowMs)
    {
        if (_built && _contentVersion == contentVersion
                   && (contentVersion >= 0 || nowMs - _builtAtMs < StaleAfterMs)
                   && ReferenceEquals(_templates, templates) && _templateCount == templates.Count
                   && ReferenceEquals(_notes, notes) && _noteCount == notes.Count
                   && ReferenceEquals(_chords, chords) && _chordCount == chords.Count
                   && ReferenceEquals(_handShapes, handShapes) && _handShapeCount == handShapes.Count
                   && ReferenceEquals(_beats, beats) && _beatCount == beats.Count)
        {
            return _current;
        }

        _current = RepeatChords.Compute(templates, notes, chords, handShapes, beats);
        ComputeCount++;
        _built = true;
        _builtAtMs = nowMs;
        _contentVersion = contentVersion;
        (_templates, _templateCount) = (templates, templates.Count);
        (_notes, _noteCount) = (notes, notes.Count);
        (_chords, _chordCount) = (chords, chords.Count);
        (_handShapes, _handShapeCount) = (handShapes, handShapes.Count);
        (_beats, _beatCount) = (beats, beats.Count);
        return _current;
    }
}
