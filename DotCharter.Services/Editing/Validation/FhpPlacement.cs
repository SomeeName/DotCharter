using DotCharter.Services.State;

namespace DotCharter.Services.Editing.Validation;

/// <summary>
///     Where a fret hand position sits against the sounds of its level, for D50-D53
///     (<c>Rules/FretHandPositionRule</c>), and the one fix that is unambiguous: an FHP before the first
///     sound, between sounds or inside a sustain moves onto the NEXT sound, where the hand actually moves.
///     <para>
///         <b>A phrase start is always a good spot.</b> E06 (<c>PhraseAnchorRule</c>) wants every phrase to
///         own an anchor between its start and its first sound, and its fix restates the anchor ON the phrase
///         start, so an FHP on a playable phrase start (or a mover phrase) is never reported here. COUNT and
///         the other structural phrases do not count: an FHP inside COUNT is "before the first note".
///     </para>
///     <para>
///         <b>So is a hand-shape start.</b> The hand forms the grip where its shape begins, and the generator
///         anchors every shape start on purpose (a shape that opens on an empty beat before its first strum would
///         otherwise seed no anchor), so an FHP there is the hand moving, not a stray marker.
///     </para>
///     <para>
///         Times compare through <see cref="ValidationTimeMatch" />, so an imported anchor a rounding step off
///         its note still sits ON it.
///     </para>
/// </summary>
public static class FhpPlacement
{
    /// <summary>Where an anchor sits.</summary>
    public enum Spot
    {
        /// <summary>On a sound, a phrase start or a mover phrase: fine (D53 may still apply).</summary>
        Fine,

        /// <summary>Before the level's first sound (D50).</summary>
        BeforeFirstSound,

        /// <summary>Inside a ringing sustain, on no new sound (D52).</summary>
        InsideSustain,

        /// <summary>Between two sounds, or after the last one (D51).</summary>
        BetweenSounds
    }

    /// <summary>Where <paramref name="anchor" /> sits on <paramref name="level" />; <see cref="Spot.Fine" />
    /// when the level plays nothing at all.</summary>
    public static Spot Classify(ArrangementValidationContext context, ChartLevel level, FretAnchor anchor)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(anchor);
        var beats = context.Beats;
        var aMs = anchor.Position.GetPositionMs(beats);
        if (context.IsOnMoverPhraseBoundary(aMs)) return Spot.Fine;
        if (context.PlayablePhrases.Any(p => ValidationTimeMatch.Coincide(p.Position.GetPositionMs(beats), aMs))) return Spot.Fine;
        if (level.HandShapes.Any(h => ValidationTimeMatch.Coincide(h.Position.GetPositionMs(beats), aMs))) return Spot.Fine;

        double? firstMs = null;
        var inside = false;
        foreach (var (start, end) in Sounds(level))
        {
            var sMs = start.GetPositionMs(beats);
            if (ValidationTimeMatch.Coincide(sMs, aMs)) return Spot.Fine;
            if (IsPitchedSlideLanding(level, start, end, beats, aMs)) return Spot.Fine;
            if (firstMs == null || sMs < firstMs) firstMs = sMs;
            if (sMs < aMs && end.GetPositionMs(beats) > aMs && !ValidationTimeMatch.Coincide(end.GetPositionMs(beats), aMs))
                inside = true;
        }

        if (firstMs == null) return Spot.Fine;
        if (aMs < firstMs) return Spot.BeforeFirstSound;
        return inside ? Spot.InsideSustain : Spot.BetweenSounds;
    }

    /// <summary>
    ///     True when <paramref name="anchor" /> sits on a sound whose every note is a hammer-on, moves the
    ///     hand (the anchor before it has another fret), and that move could have come one note earlier: the
    ///     note each hammer-on is struck from, on its string, is open or already under the new anchor. When
    ///     it is not, the stretch itself needs the move, and a chart has no better spot for it.
    /// </summary>
    public static bool IsShiftOnHammerOn(ArrangementValidationContext context, ChartLevel level, FretAnchor anchor)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(anchor);
        var beats = context.Beats;
        var aMs = anchor.Position.GetPositionMs(beats);
        var notes = level.Notes.Where(n => ValidationTimeMatch.Coincide(n.Position.GetPositionMs(beats), aMs)).ToList();
        if (notes.Count == 0 || notes.Any(n => n.Hopo != HopoType.HammerOn)) return false;
        if (level.Chords.Any(c => ValidationTimeMatch.Coincide(c.Position.GetPositionMs(beats), aMs))) return false;
        if (context.PreviousAnchor(level, anchor.Position) is not { } previous || previous.Fret == anchor.Fret) return false;

        var top = anchor.Fret + Math.Max(4, anchor.Width) - 1;
        foreach (var target in notes)
        {
            GuitarNote? from = null;
            foreach (var n in level.Notes)
            {
                if (n.StringIndex == target.StringIndex && n.Position < target.Position && (from == null || n.Position > from.Position))
                    from = n;
            }

            if (from == null || (from.Fret > 0 && (from.Fret < anchor.Fret || from.Fret > top))) return false;
        }

        return true;
    }

    // An FHP on the landing of a pitched slide moves the hand with the slide: where the generator and
    // experienced charters put it.
    private static bool IsPitchedSlideLanding(ChartLevel level, FractionalPosition start, FractionalPosition end,
        IList<BeatMarker> beats, double aMs)
    {
        if (end <= start || !ValidationTimeMatch.Coincide(end.GetPositionMs(beats), aMs)) return false;
        return level.Notes.Any(n => n.Position == start && n.SlideTo.HasValue && !n.UnpitchedSlide)
               || level.Chords.Any(c => c.Position == start && c.ChordNotes.Any(cn => cn.SlideTo.HasValue && !cn.UnpitchedSlide));
    }

    /// <summary>
    ///     One undo step that moves the FHP of a D50/D51/D52 finding onto the next sound, or removes it when
    ///     that sound already has its own (or no sound follows, so it governs nothing). Null when the finding
    ///     is another rule's, the anchor is gone, or the move is not clean: the next sound is a hammer-on or
    ///     pull-off target, or sits inside a hand shape.
    /// </summary>
    public static IChartAction? MoveToNextSound(ChartLevel level, ArrangementIssue issue)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(issue);
        if (issue.Type is not (ArrangementIssueType.AnchorBeforeFirstNote or ArrangementIssueType.AnchorBetweenNotes
                or ArrangementIssueType.AnchorInsideSustain))
        {
            return null;
        }

        if (issue.Anchor is not { } anchor || !level.Anchors.Contains(anchor)) return null;

        FractionalPosition? next = null;
        foreach (var (start, _) in Sounds(level))
        {
            if (start > anchor.Position && (next == null || start < next.Value)) next = start;
        }

        if (next is not { } target || level.Anchors.Any(a => a != anchor && a.Position > anchor.Position && a.Position <= target))
            return new RemoveAnchorsAction(level.Anchors, [anchor]);

        if (level.Notes.Any(n => n.Position == target && n.Hopo is HopoType.HammerOn or HopoType.PullOff)) return null;
        if (level.HandShapes.Any(h => h.Position < target && h.EndPosition > target)) return null;

        var moved = anchor.Clone();
        moved.Position = target;
        return new MutateAnchorsAction(level.Anchors, [anchor], [anchor.Clone()], [moved]);
    }

    private static IEnumerable<(FractionalPosition Start, FractionalPosition End)> Sounds(ChartLevel level)
    {
        foreach (var n in level.Notes) yield return (n.Position, n.EndPosition);
        foreach (var c in level.Chords) yield return (c.Position, c.EndPosition);
    }
}
