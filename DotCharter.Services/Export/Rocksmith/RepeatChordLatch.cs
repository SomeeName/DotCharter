using System.Runtime.CompilerServices;

namespace DotCharter.Services.Export.Rocksmith;

/// <summary>
///     Access to <see cref="RepeatChords" /> for every render loop that draws a level. The
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
///     ONE instance is shared by the 2D and 3D loops (the editor owns it), so an edit
///     costs one run per level, not one per loop. Thread-safe: the 3D loop reads on
///     its render thread. A result is published whole by a reference write and a
///     recompute is serialised per level.
/// </summary>
public sealed class RepeatChordLatch
{
    /// <summary>The staleness bound for an UNVERSIONED caller's in-place edits.</summary>
    public const long StaleAfterMs = 250;

    private readonly ConditionalWeakTable<IList<GuitarNote>, Slot> _slots = [];
    private int _computeCount;

    /// <summary>How many times the set was recomputed, over every level — a test seam.</summary>
    public int ComputeCount => Volatile.Read(ref _computeCount);

    /// <summary>The repeat chords of the level these collections describe.
    /// <paramref name="contentVersion" /> is any counter that moves on an edit,
    /// or negative for "unversioned";
    /// <paramref name="nowMs" /> is any monotonic millisecond clock
    /// (<c>Environment.TickCount64</c> in the app).</summary>
    public IReadOnlySet<Chord> Get(IList<ChordTemplate> templates, IList<GuitarNote> notes, IList<Chord> chords,
        IList<HandShape> handShapes, IList<BeatMarker> beats, long contentVersion, long nowMs)
    {
        var slot = _slots.GetValue(notes, static _ => new Slot());
        if (Volatile.Read(ref slot.Built) is { } built
            && built.Serves(templates, notes, chords, handShapes, beats, contentVersion, nowMs))
        {
            return built.Set;
        }

        lock (slot)
        {
            if (slot.Built is { } again
                && again.Serves(templates, notes, chords, handShapes, beats, contentVersion, nowMs))
            {
                return again.Set;
            }

            var fresh = new Built(RepeatChords.Compute(templates, notes, chords, handShapes, beats), templates, notes,
                chords, handShapes, beats, contentVersion, nowMs);
            Volatile.Write(ref slot.Built, fresh);
            Interlocked.Increment(ref _computeCount);
            return fresh.Set;
        }
    }

    private sealed class Slot
    {
        public Built? Built;
    }

    /// <summary>One published result and the inputs it answers.</summary>
    private sealed class Built(IReadOnlySet<Chord> set, IList<ChordTemplate> templates, IList<GuitarNote> notes,
        IList<Chord> chords, IList<HandShape> handShapes, IList<BeatMarker> beats, long contentVersion, long builtAtMs)
    {
        private readonly int _templateCount = templates.Count;
        private readonly int _noteCount = notes.Count;
        private readonly int _chordCount = chords.Count;
        private readonly int _handShapeCount = handShapes.Count;
        private readonly int _beatCount = beats.Count;

        public IReadOnlySet<Chord> Set { get; } = set;

        public bool Serves(IList<ChordTemplate> forTemplates, IList<GuitarNote> forNotes, IList<Chord> forChords,
            IList<HandShape> forHandShapes, IList<BeatMarker> forBeats, long version, long nowMs) =>
            contentVersion == version
            && (version >= 0 || nowMs - builtAtMs < StaleAfterMs)
            && ReferenceEquals(templates, forTemplates) && _templateCount == forTemplates.Count
            && ReferenceEquals(notes, forNotes) && _noteCount == forNotes.Count
            && ReferenceEquals(chords, forChords) && _chordCount == forChords.Count
            && ReferenceEquals(handShapes, forHandShapes) && _handShapeCount == forHandShapes.Count
            && ReferenceEquals(beats, forBeats) && _beatCount == forBeats.Count;
    }
}
