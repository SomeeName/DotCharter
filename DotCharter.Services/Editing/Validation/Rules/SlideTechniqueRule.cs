namespace DotCharter.Services.Editing.Validation.Rules;

/// <summary>
///     Slide / HOPO / bend-limit checks ported from Log Charter's
///     GuitarSoundsValidator and EOF's pre-save gate: slides from or onto
///     invalid frets, unlinked pitched slides, HOPO techniques on open
///     strings, pull-offs without a lower predecessor, bends past what
///     Rocksmith supports and the 32-bend-point crash guard.
/// </summary>
public sealed class SlideTechniqueRule : IArrangementRule
{
    /// <summary>Rocksmith 2014 renders bends up to 3 whole steps (Step is in half steps).</summary>
    private const double MaxBendHalfSteps = 6.0;

    /// <summary>RS2014 crashes on 33+ bend points per note (EOF guard).</summary>
    private const int MaxBendPoints = 32;

    public void Validate(ArrangementValidationContext context, ICollection<ArrangementIssue> issues)
    {
        var beats = context.Beats;

        // "Open" means AT OR BELOW THE CAPO, not fret 0. A capo stops every
        // string at its own fret, so on a capo'd chart nothing is fretting the
        // capo fret and there is nothing to slide off, hammer onto or tap — the
        // three gates below exist for exactly that state and tested `Fret == 0`,
        // which only finds it on an uncapoed neck. Every other rule that asks
        // the question already spells it this way (OpenNoteBendRule's E07,
        // CapoAnchorRangeRule's anchor-coverage skip, TemplateAndOverlapRule's
        // `fret <= capo`), and DotCharter.Core/AGENTS.md states the sounding
        // offset as max(fret, capo). A strict no-op at capo 0.
        var capo = Math.Max(0, context.Arrangement.Capo);

        foreach (var level in context.Arrangement.Levels)
        {
            for (var i = 0; i < level.Notes.Count; i++)
            {
                var note = level.Notes[i];
                var timeMs = note.Position.GetPositionMs(beats);

                if (note.SlideTo is { } slideTo)
                {
                    if (note.Fret <= capo)
                    {
                        issues.Add(ArrangementIssues.Note(ArrangementIssueType.SlideFromOpenString, note, timeMs,
                            OpenStringMessage(capo,
                                "Slide starts on an open string — slides need a fretted start.",
                                "the slide has no stopped fret to start from")));
                    }
                    else if (slideTo == note.Fret)
                    {
                        issues.Add(ArrangementIssues.Note(ArrangementIssueType.SlideEndsOnStartFret, note, timeMs,
                            $"Slide ends on its own start fret ({note.Fret}) — it will not export as a slide."));
                    }

                    if (!note.LinkNext && !note.UnpitchedSlide)
                    {
                        issues.Add(ArrangementIssues.Note(ArrangementIssueType.PitchedSlideNotLinked, note, timeMs,
                            "Pitched slide is not linked to a following note (link-next is off)."));
                    }
                }

                switch (note.Hopo)
                {
                    case HopoType.HammerOn when note.Fret <= capo:
                        issues.Add(ArrangementIssues.Note(ArrangementIssueType.HammerOnOnOpenString, note, timeMs,
                            OpenStringMessage(capo,
                                "Hammer-on onto an open string is not playable.",
                                "there is no fret to hammer onto")));
                        break;
                    case HopoType.Tap when note.Fret <= capo:
                        issues.Add(ArrangementIssues.Note(ArrangementIssueType.TapOnOpenString, note, timeMs,
                            OpenStringMessage(capo,
                                "Tap on an open string is not playable.",
                                "there is no fret to tap")));
                        break;
                    case HopoType.PullOff:
                        CheckPullOff(context, level, i, note, timeMs, issues);
                        break;
                }

                CheckBends(note.BendValues, timeMs, issues, note: note);
            }

            foreach (var chord in level.Chords)
            {
                var timeMs = chord.Position.GetPositionMs(beats);
                foreach (var cn in chord.ChordNotes)
                {
                    // `> capo`, not `> 0`: the guard exists to keep the
                    // open-string case out of E01, and under a capo the open
                    // string is the capo fret.
                    if (cn.SlideTo is { } slideTo && slideTo == cn.Fret && cn.Fret > capo)
                    {
                        issues.Add(ArrangementIssues.Chord(ArrangementIssueType.SlideEndsOnStartFret, chord, timeMs,
                            $"Chord slide ends on its own start fret ({cn.Fret}) on string {cn.StringIndex + 1}.",
                            cn.StringIndex));
                    }

                    CheckBends(cn.BendValues, timeMs, issues, chord: chord, stringIndex: cn.StringIndex);
                }
            }
        }
    }

    /// <summary>The uncapoed sentence verbatim, or the capo'd one that says WHY
    /// a fretted-looking number is an open string — a charter reading
    /// "open string" against a note on fret 3 needs the capo named.</summary>
    private static string OpenStringMessage(int capo, string plain, string because) =>
        capo > 0
            ? $"This string is played open (stopped by the capo at fret {capo}), so {because}."
            : plain;

    private static void CheckPullOff(
        ArrangementValidationContext context, ChartLevel level, int noteIndex, GuitarNote note, double timeMs,
        ICollection<ArrangementIssue> issues)
    {
        // Nearest previous sound on the same string — note or chord note.
        int? previousFret = null;
        var previousPosition = FractionalPosition.Zero;

        for (var j = noteIndex - 1; j >= 0; j--)
        {
            var candidate = level.Notes[j];
            if (candidate.StringIndex != note.StringIndex) continue;
            previousFret = candidate.Fret;
            previousPosition = candidate.Position;
            break;
        }

        // Indexed: walking EVERY chord in the level, per pull-off
        // note, with no early exit is the O(pullOffs × chords) pass that would make a
        // chart load's two validation runs the worst thing on the UI thread.
        // A chord only wins on a position STRICTLY greater than the note
        // candidate's, which is the `<= previousPosition` skip a scan would use.
        var previousChord = context.PreviousChordOnStringBefore(level, note.Position, note.StringIndex);
        if (previousChord is { } chord && (previousFret == null || chord.Position > previousPosition))
            previousFret = chord.Fret;

        if (previousFret == null)
        {
            issues.Add(ArrangementIssues.Note(ArrangementIssueType.PullOffWithoutPrecedingNote, note, timeMs,
                "Pull-off has no preceding note on the same string."));
        }
        else if (previousFret <= note.Fret)
        {
            issues.Add(ArrangementIssues.Note(ArrangementIssueType.PullOffOntoHigherOrEqualFret, note, timeMs,
                $"Pull-off from fret {previousFret} onto fret {note.Fret} — the target must be lower."));
        }
    }

    private static void CheckBends(IReadOnlyCollection<BendValue> bends, double timeMs,
        ICollection<ArrangementIssue> issues,
        GuitarNote? note = null, Chord? chord = null, int? stringIndex = null)
    {
        if (bends.Count == 0) return;

        if (bends.Count > MaxBendPoints)
        {
            issues.Add(new ArrangementIssue(ArrangementIssueType.TooManyBendPoints,
                $"{bends.Count} bend points on one note — Rocksmith 2014 crashes above {MaxBendPoints}.",
                timeMs, Note: note, Chord: chord, StringIndex: stringIndex ?? note?.StringIndex));
        }

        foreach (var bend in bends)
        {
            if (bend.Step > MaxBendHalfSteps)
            {
                issues.Add(new ArrangementIssue(ArrangementIssueType.BendStrengthTooHigh,
                    $"Bend of {bend.Step / 2.0:0.#} steps exceeds Rocksmith's 3-step maximum.",
                    timeMs, Note: note, Chord: chord, StringIndex: stringIndex ?? note?.StringIndex));
                break;
            }
        }
    }
}
