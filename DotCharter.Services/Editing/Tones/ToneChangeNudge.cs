using DotCharter.Services.State;

namespace DotCharter.Services.Editing.Tones;

/// <summary>Why <see cref="ToneChangeNudge" /> cannot move a tone change a 32nd note earlier.</summary>
public enum ToneChangeNudgeBlock
{
    /// <summary>Nothing stands in the way: the fix moves the change.</summary>
    None,

    /// <summary>A 32nd note earlier lies before time 0, where the song has not started.</summary>
    ChartStart,

    /// <summary>Another tone change sits in the 32nd before it (or exactly a 32nd earlier):
    /// moving would stack two changes or swap their order.</summary>
    ToneChange,

    /// <summary>A phrase or section starts strictly inside the 32nd before it: moving would carry
    /// the change into the previous phrase.</summary>
    PhraseStart,

    /// <summary>A note or chord on any level also starts a 32nd earlier: the change would land on
    /// that note and raise the same warning there.</summary>
    Note,

    /// <summary>More than one tone change sits on the flagged onset: moving one would leave the other
    ///     on the note and the warning standing.</summary>
    SeveralChanges
}

/// <summary>
///     The one-click fix for I30 (<see cref="ArrangementIssueType.ToneChangeOnNote" />): moves the
///     tone change on the flagged note ONE 32nd note earlier, so the new tone is already on when the
///     note sounds. About a 32nd is the offset experienced charters use (it was a 16th before).
///     <para>
///         <b>A 32nd is measured on the beat grid, not in milliseconds.</b> A beat is one
///         1/<c>NoteDenominator</c> note, so a 32nd is <c>NoteDenominator/32</c> of the beat it lies
///         in: an eighth of a 4/4 beat, a quarter of a 6/8 beat. When the 32nd reaches back over a beat
///         line, the part before the line is measured on the PREVIOUS beat (its own tempo and meter),
///         so the move is exactly a 32nd of music at every tempo. Before the first beat it is measured
///         on the lead-in the timeline extrapolates from the first interval and the first beat's meter.
///     </para>
///     <para>
///         <b>The rule: the fix moves exactly one 32nd or not at all.</b> It is offered DISABLED, with
///         the reason, when more than one tone change sits on the note, the move would reach before time 0,
///         reach another tone change (never two
///         on one time, never swapped), cross a phrase or section start (a change sitting ON a start
///         may move before it — that note is what the 32nd protects), or land on another note's onset.
///         Each move is one undo step.
///     </para>
///     <para>
///         <b>Same time = I30's same time.</b> Matching the finding to its tone change and the landing
///         spot to a note both use <see cref="ValidationTimeMatch.Coincide" />, the comparison the
///         warning itself uses, so the fix covers every change the warning flags and never lands on a
///         note the warning would flag. Callers compute the nudge again when the fix is PRESSED, on the
///         live arrangement, not only when the finding was shown.
///     </para>
/// </summary>
public sealed class ToneChangeNudge
{
    private static readonly Fraction ThirtySecond = new(1, 32);

    private ToneChangeNudge(ToneChange change, FractionalPosition to, ToneChangeNudgeBlock block)
    {
        Change = change;
        To = to;
        Block = block;
    }

    /// <summary>The tone change on the note.</summary>
    public ToneChange Change { get; }

    /// <summary>Where the change goes: one 32nd earlier (meaningful when <see cref="CanApply" />).</summary>
    public FractionalPosition To { get; }

    /// <summary>What stops the move, or <see cref="ToneChangeNudgeBlock.None" />.</summary>
    public ToneChangeNudgeBlock Block { get; }

    public bool CanApply => Block == ToneChangeNudgeBlock.None;

    /// <summary>The one-step move, or null when blocked.</summary>
    public IChartAction? Action() => CanApply ? new MoveToneChangeAction(Change, To) : null;

    /// <summary>
    ///     The nudge for a tone change at <paramref name="timeMs" /> (the finding's time) in
    ///     <paramref name="arrangement" />, or null when no tone change sits there.
    /// </summary>
    public static ToneChangeNudge? For(Arrangement arrangement, IList<BeatMarker> beats, double timeMs)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        ArgumentNullException.ThrowIfNull(beats);
        if (beats.Count == 0) return null;

        var matches = arrangement.ToneChanges
            .Where(c => ValidationTimeMatch.Coincide(c.Position.GetPositionMs(beats), timeMs)).ToList();
        if (matches.Count == 0) return null;
        var change = matches[0];
        if (matches.Count > 1) return new ToneChangeNudge(change, change.Position, ToneChangeNudgeBlock.SeveralChanges);

        var to = ThirtySecondEarlier(change.Position, beats);
        return to.GetPositionMs(beats) < 0
            ? new ToneChangeNudge(change, change.Position, ToneChangeNudgeBlock.ChartStart)
            : new ToneChangeNudge(change, to, BlockFor(arrangement, beats, change, to));
    }

    /// <summary>
    ///     <paramref name="position" /> moved one 32nd note earlier on the beat grid. Before the first
    ///     beat it goes on into the lead-in (negative beats), measured in the first beat's meter as the
    ///     timeline extrapolates it; the caller decides whether that time is still at or after 0 ms.
    /// </summary>
    public static FractionalPosition ThirtySecondEarlier(FractionalPosition position, IList<BeatMarker> beats)
    {
        ArgumentNullException.ThrowIfNull(beats);
        var remaining = ThirtySecond;
        var beat = position.BeatId;
        var fraction = position.Fraction;
        while (true)
        {
            var denominator = Denominator(beats, beat);
            var available = fraction / new Fraction(denominator, 1);
            if (remaining <= available) return new FractionalPosition(beat, fraction - remaining * denominator);
            remaining -= available;
            beat--;
            fraction = Fraction.One;
        }
    }

    private static int Denominator(IList<BeatMarker> beats, int beat)
    {
        var marker = beats[Math.Clamp(beat, 0, beats.Count - 1)];
        return marker.NoteDenominator > 0 ? marker.NoteDenominator : 4;
    }

    private static ToneChangeNudgeBlock BlockFor(Arrangement arrangement, IList<BeatMarker> beats, ToneChange change,
        FractionalPosition to)
    {
        var from = change.Position;
        var toMs = to.GetPositionMs(beats);
        foreach (var other in arrangement.ToneChanges)
        {
            if (ReferenceEquals(other, change)) continue;
            if ((other.Position >= to && other.Position < from) || IsAt(other.Position, beats, toMs))
                return ToneChangeNudgeBlock.ToneChange;
        }

        foreach (var phrase in PhraseMarkers.All(arrangement))
        {
            if (phrase.Position > to && phrase.Position < from) return ToneChangeNudgeBlock.PhraseStart;
        }

        foreach (var point in arrangement.EventPoints)
        {
            if (!string.IsNullOrWhiteSpace(point.Section) && point.Position > to && point.Position < from)
                return ToneChangeNudgeBlock.PhraseStart;
        }

        foreach (var level in arrangement.Levels)
        {
            if (level.Notes.Any(n => IsAt(n.Position, beats, toMs)) || level.Chords.Any(c => IsAt(c.Position, beats, toMs)))
                return ToneChangeNudgeBlock.Note;
        }

        return ToneChangeNudgeBlock.None;
    }

    private static bool IsAt(FractionalPosition position, IList<BeatMarker> beats, double ms) =>
        ValidationTimeMatch.Coincide(position.GetPositionMs(beats), ms);
}

/// <summary>Moves one tone change to a new position; undo puts it back. The nudge never passes another
/// change, so the collection's order holds without a re-sort.</summary>
public sealed class MoveToneChangeAction(ToneChange change, FractionalPosition to) : IChartAction, ILinkPolicyAction
{
    private readonly FractionalPosition _from = change.Position;

    /// <summary>Tone changes are arrangement-wide, not linked-measure content.</summary>
    public LinkPolicy LinkPolicy => LinkPolicy.Retain;

    public bool HasEffect => _from != to;

    public void Execute() => change.Position = to;

    public void Undo() => change.Position = _from;
}
