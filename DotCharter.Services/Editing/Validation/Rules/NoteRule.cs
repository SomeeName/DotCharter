namespace DotCharter.Services.Editing.Validation.Rules;

/// <summary>
///     Per-note checks: fret range, bass string range, song-end boundary,
///     noguitar section, tone-change collision, harmonic quirks, bend sanity,
///     technique-needs-sustain, HOPO-same-fret, position shift into pull-off,
///     finger change during slide. Link-Next specifics live in
///     <see cref="LinkNextRule"/>; this rule only flags the unpitched-slide +
///     Link-Next illegal combination and the phrase-change-on-sustain case
///     because they attach to the source note, not the link chain.
/// </summary>
public sealed class NoteRule : IArrangementRule
{
    private const int TechniqueMinSustainMs = 5;

    public void Validate(ArrangementValidationContext context, ICollection<ArrangementIssue> issues)
    {
        var beats = context.Beats;
        var isBass = context.Arrangement.ArrangementType == ArrangementType.Bass;

        foreach (var level in context.Arrangement.Levels)
        {
            var notes = level.Notes;
            for (var i = 0; i < notes.Count; i++)
            {
                var note = notes[i];
                var timeMs = note.Position.GetPositionMs(beats);
                var sustainMs = note.Sustain == FractionalPosition.Zero
                    ? 0.0
                    : note.EndPosition.GetPositionMs(beats) - timeMs;

                CheckPlacement(context, issues, note, timeMs, isBass);
                CheckHarmonicAndBend(beats, issues, note, timeMs, sustainMs);
                CheckTechniques(context, issues, note, timeMs, sustainMs);
                CheckHopo(context, issues, level, i, timeMs);
                CheckSlideFingering(context, issues, level, note, timeMs);
            }
        }
    }

    private static void CheckPlacement(ArrangementValidationContext context, ICollection<ArrangementIssue> issues, GuitarNote note,
        double timeMs, bool isBass)
    {
        if (note.Fret > 24)
        {
            issues.Add(ArrangementIssues.Note(ArrangementIssueType.FretNumberMoreThan24, note, timeMs,
                $"Fret number {note.Fret} exceeds the in-game maximum of 24."));
        }

        if (isBass && note.StringIndex >= 4)
        {
            issues.Add(ArrangementIssues.Note(ArrangementIssueType.InvalidBassArrangementString, note, timeMs,
                $"Bass arrangements only have 4 strings (got string index {note.StringIndex})."));
        }

        if (context.TotalAudioLengthMs is { } endMs && timeMs >= endMs)
        {
            issues.Add(ArrangementIssues.Note(ArrangementIssueType.NoteAfterSongEnd, note, timeMs,
                "Note is placed after the end of the song audio."));
        }

        if (context.IsInsideNoguitar(timeMs))
        {
            issues.Add(ArrangementIssues.Note(ArrangementIssueType.NoteInsideNoguitarSection, note, timeMs,
                "Note is placed inside a no-guitar section."));
        }

        if (context.IsOnToneChange(timeMs))
        {
            issues.Add(ArrangementIssues.Note(ArrangementIssueType.ToneChangeOnNote, note, timeMs,
                "A tone change happens at the same time as this note. Move it just before or after."));
        }
    }

    private static void CheckHarmonicAndBend(IList<BeatMarker> beats, ICollection<ArrangementIssue> issues, GuitarNote note,
        double timeMs, double sustainMs)
    {
        var hasBend = note.BendValues.Count > 0;
        if (note.Harmonic == HarmonicType.Normal && hasBend)
        {
            issues.Add(ArrangementIssues.Note(ArrangementIssueType.NaturalHarmonicWithBend, note, timeMs,
                "Natural harmonics cannot be bent."));
        }

        if (note.Harmonic == HarmonicType.Normal && note.Fret == 7 && sustainMs > 0 && !note.Ignore)
        {
            issues.Add(ArrangementIssues.Note(ArrangementIssueType.SeventhFretHarmonicWithSustain, note, timeMs,
                "7th-fret natural harmonic with sustain will not ring out in-game."));
        }

        if (!hasBend) return;

        if (!note.BendValues.Any(bv => bv.Step != 0.0))
        {
            issues.Add(ArrangementIssues.Note(ArrangementIssueType.MissingBendValue, note, timeMs,
                "Note is marked as a bend but no bend value reaches a non-zero step."));
        }

        if (FindOverlappingBendValue(note.BendValues, beats) is { } overlapMs)
        {
            issues.Add(ArrangementIssues.Note(ArrangementIssueType.OverlappingBendValues, note, overlapMs,
                "Two bend values share the same time."));
        }
    }

    private static void CheckTechniques(ArrangementValidationContext context, ICollection<ArrangementIssue> issues, GuitarNote note,
        double timeMs, double sustainMs)
    {
        var requiresSustain = note.Tremolo || note.Vibrato || note.SlideTo.HasValue || note.UnpitchedSlide;
        if (requiresSustain && sustainMs < TechniqueMinSustainMs)
        {
            issues.Add(ArrangementIssues.Note(ArrangementIssueType.TechniqueNoteWithoutSustain, note, timeMs,
                "Tremolo/vibrato/slide notes need a sustain to be audible."));
        }

        if (note.LinkNext && note.UnpitchedSlide)
        {
            issues.Add(ArrangementIssues.Note(ArrangementIssueType.UnpitchedSlideWithLinkNext, note, timeMs,
                "Unpitched slide notes cannot use Link-Next."));
        }

        if (note.LinkNext && context.HasPhraseChangeOnSustain(note))
        {
            issues.Add(ArrangementIssues.Note(ArrangementIssueType.PhraseChangeOnLinkNextNote, note, timeMs,
                "A non-mover phrase change occurs while this Link-Next note is still ringing."));
        }
    }

    private static void CheckHopo(ArrangementValidationContext context, ICollection<ArrangementIssue> issues, ChartLevel level,
        int index, double timeMs)
    {
        var note = level.Notes[index];

        // A tap is not legato from the previous note, and a hammer-on with other sounds played
        // since the previous note on its string is a hammer-on from nowhere (a fretting-hand
        // tap, how Rocksmith charts one): repeating the fret is the point of both. Measured
        // on a tapping CDLC, every one of its 193 findings was one of these two.
        if (note.Hopo is HopoType.HammerOn or HopoType.PullOff)
        {
            var prev = FindPreviousNoteOnSameString(level.Notes, index);
            if (prev != null && prev.Fret == note.Fret
                && (note.Hopo == HopoType.PullOff || !SoundsBetween(level, prev.Position, note.Position)))
            {
                issues.Add(ArrangementIssues.Note(ArrangementIssueType.HopoIntoSameNote, note, timeMs,
                    "HOPO into a note on the same fret as the previous note."));
            }
        }

        if (note.Hopo != HopoType.PullOff || note.Fret <= 0) return;

        var here = context.AnchorAt(level, note.Position);
        if (here == null) return;

        var prevAnchor = context.PreviousAnchor(level, note.Position);
        if (prevAnchor != null && prevAnchor.Fret != here.Fret)
        {
            issues.Add(ArrangementIssues.Note(ArrangementIssueType.PositionShiftIntoPullOff, note, timeMs,
                "Pull-off note coincides with an anchor change — finger position would need to shift."));
        }
    }

    private static void CheckSlideFingering(ArrangementValidationContext context, ICollection<ArrangementIssue> issues,
        ChartLevel level, GuitarNote note, double timeMs)
    {
        if (note.SlideTo is not { } slideEnd || note.Sustain == FractionalPosition.Zero) return;

        var startAnchor = context.ActiveAnchor(level, note.Position);
        var endAnchor = context.AnchorAt(level, note.EndPosition);
        if (startAnchor == null || endAnchor == null || startAnchor == endAnchor) return;

        var startFinger = ToFinger(note.Fret, startAnchor);
        var endFinger = ToFinger(slideEnd, endAnchor);
        var fingerCannotBeDetermined =
            startAnchor.Width != endAnchor.Width
            && endAnchor.Width > 4
            && slideEnd != endAnchor.Fret + endAnchor.Width;
        var slideFromLowPosition = startAnchor.Fret == 1 && startAnchor.Fret - endFinger < 0;
        if (!fingerCannotBeDetermined && !slideFromLowPosition && startFinger != endFinger)
        {
            issues.Add(ArrangementIssues.Note(ArrangementIssueType.FingerChangeDuringSlide, note, timeMs,
                $"Slide requires a finger change ({startFinger} → {endFinger}) — likely an anchor mistake."));
        }
    }

    private static int ToFinger(int fret, FretAnchor anchor)
    {
        var raw = 1 + (fret - anchor.Fret) - (anchor.Width - 4);
        if (raw < 1) raw = 1;
        if (raw > 4) raw = 4;
        return raw;
    }

    /// <summary>Whether any note or chord starts strictly between <paramref name="from" /> and <paramref name="to" />.</summary>
    private static bool SoundsBetween(ChartLevel level, FractionalPosition from, FractionalPosition to)
    {
        foreach (var other in level.Notes)
        {
            if (other.Position > from && other.Position < to) return true;
        }

        foreach (var chord in level.Chords)
        {
            if (chord.Position > from && chord.Position < to) return true;
        }

        return false;
    }

    private static GuitarNote? FindPreviousNoteOnSameString(IList<GuitarNote> notes, int currentIndex)
    {
        var current = notes[currentIndex];
        for (var i = currentIndex - 1; i >= 0; i--)
        {
            if (notes[i].StringIndex == current.StringIndex)
                return notes[i];
        }

        return null;
    }

    private static double? FindOverlappingBendValue(IList<BendValue> bendValues, IList<BeatMarker> beats)
    {
        for (var i = 0; i + 1 < bendValues.Count; i++)
        {
            if (bendValues[i].Position == bendValues[i + 1].Position)
                return bendValues[i + 1].Position.GetPositionMs(beats);
        }

        return null;
    }
}
