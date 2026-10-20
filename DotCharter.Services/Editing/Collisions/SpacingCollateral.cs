namespace DotCharter.Services.Editing.Collisions;

/// <summary>
///     What <see cref="ChartSpacingEnforcer" /> changed on sounds and hand shapes
///     that an edit does NOT own — recorded so the edit's Undo can put them back.
///     <para>
///         Every collection action runs the enforcer over the whole active level in
///         its <c>OnPostExecute</c>, after both <c>Execute</c> AND <c>Undo</c>.
///         <c>EnforceSoundSpacing</c>'s non-link branch only ever SHORTENS, so
///         placing a note next to a 4-beat sustain truncated that NEIGHBOUR — a
///         sound the action does not hold and whose old value was recorded nowhere
///         — and undoing removed the placed note but could not lengthen anything
///         back. The neighbour stayed truncated, and repeating the gesture eroded
///         the chart. Same for hand-shape ends and for the bend curve the sustain
///         clamp collapses.
///     </para>
///     <para>
///         Recording is the enforcer's own write sites reporting to an ambient
///         <see cref="SpacingCollateralRecorder" /> scope, not a before/after diff
///         of the level: the pass already walks every sound on every committed
///         highway edit and must not grow an O(level) snapshot per keystroke. When
///         no scope is open the cost is one thread-static read per write site.
///     </para>
/// </summary>
public sealed class SpacingCollateral
{
    private readonly List<ShapeState> _shapes;
    private readonly List<SoundState> _sounds;

    internal SpacingCollateral(List<SoundState> sounds, List<ShapeState> shapes)
    {
        _sounds = sounds;
        _shapes = shapes;
    }

    public static SpacingCollateral Empty { get; } = new([], []);

    public bool IsEmpty => _sounds.Count == 0 && _shapes.Count == 0;

    /// <summary>Number of recorded sounds + hand shapes (diagnostics / tests).</summary>
    public int Count => _sounds.Count + _shapes.Count;

    /// <summary>
    ///     Puts every recorded sustain, bend curve and hand-shape end back.
    ///     <para>
    ///         Called LAST in an action's <c>Undo</c> — after the action's own
    ///         inverse and after the enforcer has run again — because the state
    ///         being restored IS the pre-edit state, which by definition already
    ///         satisfied the enforcer's invariants.
    ///     </para>
    /// </summary>
    public void Restore()
    {
        foreach (var state in _sounds)
        {
            state.Sound.Sustain = state.Sustain;
            if (state.Bends == null) continue;

            var bends = state.Sound.BendValues;
            bends.Clear();
            foreach (var bend in state.Bends) bends.Add(bend);
        }

        foreach (var state in _shapes) state.Shape.EndPosition = state.EndPosition;
    }

    /// <summary>
    ///     Folds a LATER record into this one, keeping THIS record's snapshot for
    ///     any element both touched — the earliest one, i.e. the state before the
    ///     gesture started.
    ///     <para>
    ///         A merged gesture is one undo node, and only the head action's
    ///         collateral survives the merge. Each tick's enforcer pass shortens
    ///         whatever that tick reached, so tick 1 may truncate neighbour A and
    ///         tick 7 neighbour B: without this, B's record went with the discarded
    ///         instance and Ctrl+Z restored the dragged element while B stayed
    ///         short — the exact erosion this type was added to stop, reopened by
    ///         gesture merging. And the LATER snapshot of an element both ticks
    ///         touched is already truncated, so overwriting with it would restore
    ///         the wrong value.
    ///     </para>
    /// </summary>
    internal SpacingCollateral MergeKeepingEarliest(SpacingCollateral? later)
    {
        if (later == null || later.IsEmpty) return this;
        if (IsEmpty) return later;

        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var state in _sounds) seen.Add(state.Sound);
        foreach (var state in _shapes) seen.Add(state.Shape);

        var sounds = new List<SoundState>(_sounds);
        var shapes = new List<ShapeState>(_shapes);
        foreach (var state in later._sounds)
        {
            if (seen.Add(state.Sound))
                sounds.Add(state);
        }

        foreach (var state in later._shapes)
        {
            if (seen.Add(state.Shape))
                shapes.Add(state);
        }

        return new SpacingCollateral(sounds, shapes);
    }

    internal readonly record struct SoundState(IGuitarSound Sound, FractionalPosition Sustain, BendValue[]? Bends);

    internal readonly record struct ShapeState(HandShape Shape, FractionalPosition EndPosition);
}

/// <summary>
///     Ambient, thread-scoped recording of <see cref="ChartSpacingEnforcer" />'s
///     collateral edits. Open one around a call that runs the enforcer, then
///     <see cref="Build" /> the record of what it touched.
///     <para>
///         Ambient rather than a parameter because the enforcer is reached through
///         an opaque <c>Action</c> post-execute delegate that ~35 call sites hand to
///         the chart actions; threading a recorder through all of them would change
///         every one of those signatures for a concern none of them has. The scope
///         is strictly <c>using</c>-bounded, nests correctly, and is
///         <c>[ThreadStatic]</c> — chart editing is UI-thread-synchronous, and a
///         save on a worker can never see another thread's scope.
///     </para>
/// </summary>
public sealed class SpacingCollateralRecorder : IDisposable
{
    [ThreadStatic] private static SpacingCollateralRecorder? _current;

    private readonly HashSet<object> _captured = new(ReferenceEqualityComparer.Instance);
    private readonly SpacingCollateralRecorder? _previous;
    private readonly List<SpacingCollateral.ShapeState> _shapes = [];
    private readonly List<SpacingCollateral.SoundState> _sounds = [];
    private bool _disposed;

    private SpacingCollateralRecorder(SpacingCollateralRecorder? previous) => _previous = previous;

    internal static SpacingCollateralRecorder? Current => _current;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _current = _previous;
    }

    public static SpacingCollateralRecorder Begin()
    {
        var recorder = new SpacingCollateralRecorder(_current);
        _current = recorder;
        return recorder;
    }

    /// <summary>Snapshots a sound's sustain + bend curve, once per scope.</summary>
    internal void CaptureSound(IGuitarSound sound)
    {
        if (!_captured.Add(sound)) return;
        var bends = sound.BendValues.Count == 0
            ? null
            : sound.BendValues.Select(b => b.Clone()).ToArray();
        _sounds.Add(new SpacingCollateral.SoundState(sound, sound.Sustain, bends));
    }

    internal void CaptureHandShapeEnd(HandShape shape)
    {
        if (!_captured.Add(shape)) return;
        _shapes.Add(new SpacingCollateral.ShapeState(shape, shape.EndPosition));
    }

    /// <summary>
    ///     The record of everything the enforcer actually CHANGED, minus the
    ///     elements <paramref name="owned" /> — the action's own items, whose old
    ///     state the action already carries. Restoring an owned element would fight
    ///     the action's inverse: a <c>MutateNotesAction</c>'s target is snapshotted
    ///     here holding the NEW state, so replaying it after <c>copyFn(old)</c>
    ///     would undo the undo.
    /// </summary>
    public SpacingCollateral Build(IEnumerable<object>? owned = null)
    {
        var excluded = ExpandOwned(owned);

        var sounds = new List<SpacingCollateral.SoundState>();
        foreach (var state in _sounds)
        {
            if (excluded.Contains(state.Sound)) continue;
            if (state.Sound.Sustain == state.Sustain && !BendsChanged(state)) continue;
            sounds.Add(state);
        }

        var shapes = new List<SpacingCollateral.ShapeState>();
        foreach (var state in _shapes)
        {
            if (excluded.Contains(state.Shape)) continue;
            if (state.Shape.EndPosition == state.EndPosition) continue;
            shapes.Add(state);
        }

        return sounds.Count == 0 && shapes.Count == 0
            ? SpacingCollateral.Empty
            : new SpacingCollateral(sounds, shapes);
    }

    private static HashSet<object> ExpandOwned(IEnumerable<object>? owned)
    {
        var set = new HashSet<object>(ReferenceEqualityComparer.Instance);
        if (owned == null) return set;

        foreach (var element in owned)
        {
            set.Add(element);
            // A Chord's identity for the enforcer is its ChordNotes — those are
            // what carry a sustain and a bend curve.
            if (element is Chord chord)
            {
                foreach (var chordNote in chord.ChordNotes)
                    set.Add(chordNote);
            }
        }

        return set;
    }

    private static bool BendsChanged(SpacingCollateral.SoundState state)
    {
        var before = state.Bends;
        var now = state.Sound.BendValues;
        if (before == null) return now.Count != 0;
        if (before.Length != now.Count) return true;

        for (var i = 0; i < before.Length; i++)
        {
            if (before[i].Position != now[i].Position || !before[i].Step.Equals(now[i].Step))
                return true;
        }

        return false;
    }
}
