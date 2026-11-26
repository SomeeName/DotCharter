namespace DotCharter.Services.Editing.Validation.Rules;

/// <summary>
///     Where a fret hand position sits and what it asks of the hand, the charting conventions experienced
///     charters follow (D50-D56):
///     <list type="bullet">
///         <item>D50 before the level's first note or chord (inside COUNT, say);</item>
///         <item>D51 between sounds, on no note, chord or phrase start; D52 inside a ringing sustain;</item>
///         <item>D53 a hand move ON a hammer-on target (a pull-off target is I42, <c>NoteRule</c>);</item>
///         <item>D54 an anchor on fret 22 to 24 (a warning), D55 above 24 (invalid; L03 still judges the
///         anchor's top fret against the neck);</item>
///         <item>D56 a bend that falls to the pinky: finger 4, or fret = FHP + 3 when no finger is stated.</item>
///     </list>
///     Placement is <see cref="FhpPlacement" />, shared with the D50-D52 fix. A phrase start or a mover
///     phrase is always a good spot for an FHP (E06 puts one there), so neither is reported.
/// </summary>
public sealed class FretHandPositionRule : IArrangementRule
{
    /// <summary>The highest anchor fret that draws no warning.</summary>
    public const int HighestComfortableAnchorFret = 21;

    private const int Pinky = 4;

    public void Validate(ArrangementValidationContext context, ICollection<ArrangementIssue> issues)
    {
        var beats = context.Beats;
        foreach (var level in context.Arrangement.Levels)
        {
            foreach (var anchor in level.Anchors)
            {
                var aMs = anchor.Position.GetPositionMs(beats);
                var spot = FhpPlacement.Classify(context, level, anchor);
                var placement = spot switch
                {
                    FhpPlacement.Spot.BeforeFirstSound => (ArrangementIssueType.AnchorBeforeFirstNote, "Validation_AnchorBeforeFirstNote"),
                    FhpPlacement.Spot.InsideSustain => (ArrangementIssueType.AnchorInsideSustain, "Validation_AnchorInsideSustain"),
                    FhpPlacement.Spot.BetweenSounds => (ArrangementIssueType.AnchorBetweenNotes, "Validation_AnchorBetweenNotes"),
                    _ => ((ArrangementIssueType, string)?)null
                };
                if (placement is var (type, key))
                {
                    issues.Add(ArrangementIssues.Make(type, key, aMs, anchor: anchor, args: [anchor.Fret]));
                }
                else if (FhpPlacement.IsShiftOnHammerOn(context, level, anchor))
                {
                    issues.Add(ArrangementIssues.Make(ArrangementIssueType.AnchorOnHammerOnTarget,
                        "Validation_AnchorOnHammerOnTarget", aMs, anchor: anchor, args: [anchor.Fret]));
                }

                if (anchor.Fret > FretLimits.MaxFret)
                {
                    issues.Add(ArrangementIssues.Make(ArrangementIssueType.AnchorFretAbove24,
                        "Validation_AnchorFretAbove24", aMs, anchor: anchor, args: [anchor.Fret, FretLimits.MaxFret]));
                }
                else if (anchor.Fret > HighestComfortableAnchorFret)
                {
                    issues.Add(ArrangementIssues.Make(ArrangementIssueType.AnchorFretAbove21,
                        "Validation_AnchorFretAbove21", aMs, anchor: anchor, args: [anchor.Fret]));
                }
            }

            CheckPinkyBends(context, level, issues);
        }
    }

    private static void CheckPinkyBends(ArrangementValidationContext context, ChartLevel level, ICollection<ArrangementIssue> issues)
    {
        var beats = context.Beats;
        foreach (var note in level.Notes)
        {
            if (note.Fret <= 0 || !HasBend(note)) continue;
            var anchor = context.ActiveAnchor(level, note.Position);
            var finger = note.LeftHandFinger is >= 1 and <= Pinky ? note.LeftHandFinger
                : anchor == null ? 0 : note.Fret - anchor.Fret + 1;
            if (finger != Pinky) continue;
            issues.Add(ArrangementIssues.Note(ArrangementIssueType.BendNeedsPinky, note, note.Position.GetPositionMs(beats),
                "Validation_BendNeedsPinky", args: [note.Fret]));
        }

        foreach (var chord in level.Chords)
        {
            var template = context.ChordTemplateFor(chord);
            var anchor = context.ActiveAnchor(level, chord.Position);
            foreach (var cn in chord.ChordNotes)
            {
                if (cn.Fret <= 0 || !HasBend(cn)) continue;
                var finger = template != null && template.Fingers.TryGetValue(cn.StringIndex, out var f) && f is >= 1 and <= Pinky ? f
                    : anchor == null ? 0 : cn.Fret - anchor.Fret + 1;
                if (finger != Pinky) continue;
                issues.Add(ArrangementIssues.Chord(ArrangementIssueType.BendNeedsPinky, chord, chord.Position.GetPositionMs(beats),
                    "Validation_BendNeedsPinky", cn.StringIndex, [cn.Fret]));
                break;
            }
        }
    }

    private static bool HasBend(IGuitarSound sound) => sound.BendValues.Any(b => Math.Abs(b.Step) > 0.0001);
}
