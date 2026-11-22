using DotCharter.Services.State;

namespace DotCharter.Services.Editing.Tones;

/// <summary>
///     The pure core of the Tone-row drag: moves a set of tone changes rigidly by a grid-snapped
///     delta while the pointer is held, and turns the drop into ONE undo step.
///     <para>
///         <b>The rules are <see cref="ToneChangeNudge" />'s.</b> A drop never stacks two changes on one
///         time (exact position or the I30 <see cref="ValidationTimeMatch.Coincide" /> slop), never swaps
///         a moved change past one that stays, and never goes before position zero. The start is CLAMPED
///         (the delta shortens for the whole set, like every other timeline drag); a stack or a swap is
///         REFUSED: the preview keeps the last legal delta, so the changes stop short of the neighbour.
///         The moved set keeps its own order because it moves rigidly, so the collection never needs a
///         re-sort (the <see cref="MoveToneChangeAction" /> contract).
///     </para>
/// </summary>
public sealed class ToneChangeDrag
{
    private readonly IReadOnlyList<ToneChange> _all;
    private readonly List<(ToneChange Change, FractionalPosition From)> _moving;

    /// <param name="all">Every tone change of the arrangement.</param>
    /// <param name="moving">The changes the drag carries (the selection); ones not in <paramref name="all" /> are ignored.</param>
    public ToneChangeDrag(IReadOnlyList<ToneChange> all, IEnumerable<ToneChange> moving)
    {
        ArgumentNullException.ThrowIfNull(all);
        ArgumentNullException.ThrowIfNull(moving);
        _all = all;
        _moving = moving.Distinct().Where(c => all.Contains(c)).Select(c => (c, c.Position)).ToList();
    }

    /// <summary>The delta the live positions show now (zero until a legal move was previewed).</summary>
    public FractionalPosition Applied { get; private set; } = FractionalPosition.Zero;

    /// <summary>How many changes the drag carries.</summary>
    public int Count => _moving.Count;

    /// <summary>
    ///     Shows <paramref name="requestedDelta" /> on the live changes: clamped so the earliest lands no
    ///     earlier than zero, refused (the last legal delta stays) when it would stack or swap.
    ///     Returns the delta now applied.
    /// </summary>
    public FractionalPosition Preview(FractionalPosition requestedDelta, IList<BeatMarker> beats)
    {
        ArgumentNullException.ThrowIfNull(beats);
        if (_moving.Count == 0) return Applied;
        var earliest = _moving.Min(m => m.From);
        var delta = earliest + requestedDelta < FractionalPosition.Zero ? FractionalPosition.Zero - earliest : requestedDelta;
        if (!IsLegal(delta, beats)) return Applied;
        Applied = delta;
        foreach (var (change, from) in _moving) change.Position = from + delta;
        return Applied;
    }

    /// <summary>
    ///     True when every moved change, shifted by <paramref name="delta" />, stays strictly between the
    ///     non-moving changes around its original position and coincides with none of them.
    /// </summary>
    public bool IsLegal(FractionalPosition delta, IList<BeatMarker> beats)
    {
        ArgumentNullException.ThrowIfNull(beats);
        if (delta == FractionalPosition.Zero) return true;
        foreach (var (_, from) in _moving)
        {
            var to = from + delta;
            if (to < FractionalPosition.Zero) return false;
            var toMs = to.GetPositionMs(beats);
            foreach (var other in _all)
            {
                if (IsMoving(other)) continue;
                var at = other.Position;
                var crosses = (from < at && to >= at) || (from > at && to <= at);
                if (crosses || ValidationTimeMatch.Coincide(at.GetPositionMs(beats), toMs)) return false;
            }
        }

        return true;
    }

    /// <summary>Puts every carried change back where the drag found it (Esc, capture loss, a click).</summary>
    public void Restore()
    {
        foreach (var (change, from) in _moving) change.Position = from;
        Applied = FractionalPosition.Zero;
    }

    /// <summary>
    ///     Restores the live positions and returns the drop as ONE action (a composite of
    ///     <see cref="MoveToneChangeAction" />s), or null when the drop moves nothing.
    /// </summary>
    public IChartAction? Commit()
    {
        var delta = Applied;
        Restore();
        if (delta == FractionalPosition.Zero) return null;
        var moves = _moving.Select(m => (IChartAction)new MoveToneChangeAction(m.Change, m.From + delta)).ToList();
        return moves.Count == 1 ? moves[0] : new CompositeAction(moves);
    }

    private bool IsMoving(ToneChange change)
    {
        foreach (var (moving, _) in _moving)
        {
            if (ReferenceEquals(moving, change)) return true;
        }

        return false;
    }
}
