namespace DotCharter.Services.Editing.Validation.Rules;

/// <summary>
///     Capo and fret-hand-position (anchor) coverage checks, ported from
///     Log Charter's FHPsValidator / GuitarSoundsValidator and EOF's
///     fret-hand-position gate: fret-below-capo, anchor-below-capo,
///     anchor range exceeding the neck, notes with no anchor coverage and
///     notes falling outside their anchor's highlighted fret range.
/// </summary>
public sealed class CapoAnchorRangeRule : IArrangementRule
{
    private const int MaxFret = FretLimits.MaxFret;
    private const int DefaultAnchorWidth = 4;

    public void Validate(ArrangementValidationContext context, ICollection<ArrangementIssue> issues)
    {
        var beats = context.Beats;
        var capo = context.Arrangement.Capo;

        foreach (var level in context.Arrangement.Levels)
        {
            foreach (var anchor in level.Anchors)
            {
                var aMs = anchor.Position.GetPositionMs(beats);
                if (anchor.Fret <= capo)
                {
                    issues.Add(ArrangementIssues.Make(ArrangementIssueType.AnchorOnOrBelowCapo,
                        "Validation_AnchorOnOrBelowCapo", aMs, anchor: anchor, args: [anchor.Fret, capo]));
                }

                if (TopFret(anchor.Fret, anchor.Width) > MaxFret)
                {
                    issues.Add(ArrangementIssues.Make(ArrangementIssueType.AnchorTopFretTooHigh,
                        "Validation_AnchorTopFretTooHigh", aMs, anchor: anchor,
                        args: [TopFret(anchor.Fret, anchor.Width), MaxFret]));
                }
            }

            foreach (var note in level.Notes)
            {
                var timeMs = note.Position.GetPositionMs(beats);
                if (note.Fret > 0 && note.Fret < capo)
                {
                    issues.Add(ArrangementIssues.Note(ArrangementIssueType.NoteFretBelowCapo, note, timeMs,
                        "Validation_NoteFretBelowCapo_Note", args: [note.Fret, capo]));
                }

                // A slide TARGET is a fret too: ArrangementAutoFixService
                // .FixCapoFrets raises one below the capo, so the inspector has to
                // show it first, or Fix changes something nobody was told about.
                if (note.SlideTo is { } noteTarget && noteTarget > 0 && noteTarget < capo)
                {
                    issues.Add(ArrangementIssues.Note(ArrangementIssueType.NoteFretBelowCapo, note, timeMs,
                        "Validation_NoteFretBelowCapo_SlideTarget", args: [noteTarget, capo]));
                }

                CheckAnchorCoverage(context, level, note.Position, note.Fret, note.Hopo == HopoType.Tap,
                    timeMs, capo, issues, note: note);
            }

            foreach (var chord in level.Chords)
            {
                var timeMs = chord.Position.GetPositionMs(beats);
                foreach (var cn in chord.ChordNotes)
                {
                    if (cn.Fret > 0 && cn.Fret < capo)
                    {
                        issues.Add(ArrangementIssues.Chord(ArrangementIssueType.NoteFretBelowCapo, chord, timeMs,
                            "Validation_NoteFretBelowCapo_ChordNote", cn.StringIndex,
                            [cn.Fret, cn.StringIndex + 1, capo]));
                    }

                    if (cn.SlideTo is { } chordTarget && chordTarget > 0 && chordTarget < capo)
                    {
                        issues.Add(ArrangementIssues.Chord(ArrangementIssueType.NoteFretBelowCapo, chord, timeMs,
                            "Validation_NoteFretBelowCapo_ChordSlideTarget", cn.StringIndex,
                            [chordTarget, cn.StringIndex + 1, capo]));
                    }

                    CheckAnchorCoverage(context, level, chord.Position, cn.Fret, isTap: false,
                        timeMs, capo, issues, chord: chord, stringIndex: cn.StringIndex);
                }
            }
        }
    }

    private static void CheckAnchorCoverage(ArrangementValidationContext context,
        ChartLevel level, FractionalPosition position, int fret, bool isTap,
        double timeMs, int capo, ICollection<ArrangementIssue> issues,
        GuitarNote? note = null, Chord? chord = null, int? stringIndex = null)
    {
        // Open strings and taps are anchor-independent (EOF: tap notes never
        // require an anchor; Log Charter only ranges frets above the capo).
        if (fret <= Math.Max(capo, 0) || isTap) return;

        var anchor = context.ActiveAnchor(level, position);
        if (anchor == null)
        {
            issues.Add(ArrangementIssues.Make(ArrangementIssueType.NoteWithoutAnchor,
                "Validation_NoteWithoutAnchor",
                timeMs, note: note, chord: chord, stringIndex: stringIndex ?? note?.StringIndex));
            return;
        }

        if (fret < anchor.Fret || fret > TopFret(anchor.Fret, anchor.Width))
        {
            issues.Add(ArrangementIssues.Make(ArrangementIssueType.NoteOutsideAnchorRange,
                "Validation_NoteOutsideAnchorRange",
                timeMs, note: note, chord: chord, anchor: anchor,
                stringIndex: stringIndex ?? note?.StringIndex,
                args: [fret, anchor.Fret, TopFret(anchor.Fret, anchor.Width)]));
        }
    }

    private static int TopFret(int fret, int width) =>
        fret + Math.Max(width, DefaultAnchorWidth) - 1;
}
