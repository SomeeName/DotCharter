namespace DotCharter.Services.Editing.Validation.Rules;

/// <summary>
///     Link-Next chain integrity: linked-to-chord, missing target, timing
///     misalignment, fret/slide mismatch, bend value mismatch between the
///     source note's terminal bend step and the target note's initial bend
///     step.
///     <para>
///         **It walks CHORD NOTES as well as single notes, and must keep doing
///         so.** Rocksmith2014.NET / DLC Builder run the identical chain check
///         over every chord note carrying <c>linkNext</c>
///         (<c>InstrumentalChecker.checkChords</c>, "EOF does not set LinkNext
///         on chords correctly, so check all chords regardless of LinkNext
///         status"), and a rule that looked only at
///         <see cref="ChartLevel.Notes" /> would have a blind spot that is total, not
///         partial: on the owner's chart DLC Builder reported 27 I03
///         "note is linked to a chord" findings and **every one of them sat on
///         a chord note**, so our inspector showed a clean chart while the
///         export was rejected. Chord notes are exactly where the finding comes
///         from, too — a linked CHORD is how the importers and the paste path
///         spell a strum that rings into the next one.
///     </para>
///     <para>
///         A chord note has no position of its own; it is anchored at its
///         parent chord's, which is why the checks take an explicit
///         <see cref="LinkSource" /> rather than a <c>GuitarNote</c>.
///     </para>
/// </summary>
public sealed class LinkNextRule : IArrangementRule
{
    private const int LinkNextToleranceMs = 10;

    public void Validate(ArrangementValidationContext context, ICollection<ArrangementIssue> issues)
    {
        var beats = context.Beats;

        foreach (var level in context.Arrangement.Levels)
        {
            var notes = level.Notes;
            for (var i = 0; i < notes.Count; i++)
            {
                var note = notes[i];
                if (!note.LinkNext) continue;
                CheckOne(context, level, LinkSource.From(note), FindNextNoteOnSameString(notes, i, note.StringIndex),
                    beats, issues);
            }

            foreach (var chord in level.Chords)
            {
                foreach (var chordNote in chord.ChordNotes)
                {
                    if (!chordNote.LinkNext) continue;
                    // The chord note's target is the next SINGLE note on that string
                    // anywhere after the chord — the chord itself is not in
                    // level.Notes, so there is no index to scan from; the context's
                    // per-string index answers it (a scan of the note list from index 0
                    // per chord note is quadratic).
                    CheckOne(context, level, LinkSource.From(chord, chordNote),
                        context.NextNoteOnStringAfter(level, chord.Position, chordNote.StringIndex),
                        beats, issues);
                }
            }
        }
    }

    private static void CheckOne(
        ArrangementValidationContext context,
        ChartLevel level,
        LinkSource source,
        GuitarNote? next,
        IList<BeatMarker> beats,
        ICollection<ArrangementIssue> issues)
    {
        var timeMs = source.Position.GetPositionMs(beats);

        // LANDING ON A CHORD IS NOT AN ISSUE HERE, and reporting it was wrong.
        // Tying or sliding one chord into the next is ordinary guitar and the
        // editor lets you author it; what RS cannot spell is the <chord> at the
        // landing, because a chord means a STRUM and a shape you slid into was
        // never re-picked. EOF keeps the linked chord in the project too and
        // splits it while WRITING the XML (EOF_SRC/rs.c:2010, "chords that
        // follow a note with linkNext ... need to be broken into single notes so
        // that they display correctly in-game"), which is exactly what
        // LinkLandingBreakup does on our export path. Flagging it would nag
        // about something already handled — 27 findings on one real chart.
        // The landing chord's note on this string IS the link target, so the
        // fret/timing/bend checks below still run against it.
        var landingChord = context.ChordLinkedTo(level, source.EndPosition, source.StringIndex);
        if (landingChord != null)
        {
            next = LandingNote(landingChord, source.StringIndex);
            if (next == null) return;
        }

        if (next == null)
        {
            issues.Add(source.Issue(ArrangementIssueType.LinkNextMissingTargetNote, timeMs,
                "Sound has Link-Next but there's no following note on the same string."));
            return;
        }

        var endMs = source.EndPosition.GetPositionMs(beats);
        var nextTimeMs = next.Position.GetPositionMs(beats);
        if (Math.Abs(nextTimeMs - endMs) > LinkNextToleranceMs)
        {
            issues.Add(source.Issue(ArrangementIssueType.IncorrectLinkNext, timeMs,
                "Link-Next target note doesn't start where this sound's sustain ends."));
            return;
        }

        if (source.Fret != next.Fret)
        {
            if (source.SlideTo is { } slideTo)
            {
                if (slideTo != next.Fret)
                {
                    issues.Add(source.Issue(ArrangementIssueType.LinkNextSlideMismatch, timeMs,
                        $"Link-Next slide ends on fret {slideTo} but next note is on fret {next.Fret}."));
                }
            }
            else
            {
                issues.Add(source.Issue(ArrangementIssueType.LinkNextFretMismatch, nextTimeMs,
                    $"Link-Next target fret ({next.Fret}) doesn't match the source's fret ({source.Fret})."));
            }
        }

        if (source.BendValues.Count > 0)
        {
            var lastStep = source.BendValues[^1].Step;
            var nextFirstStep = 0.0;
            if (next.BendValues.Count > 0 && next.BendValues[0].Position == next.Position)
                nextFirstStep = next.BendValues[0].Step;

            if (Math.Abs(lastStep - nextFirstStep) > 0.0001)
            {
                issues.Add(source.Issue(ArrangementIssueType.LinkNextBendMismatch, nextTimeMs,
                    $"Link-Next bend mismatch: source ends at {lastStep} steps, target starts at {nextFirstStep}."));
            }
        }
    }

    private static GuitarNote? FindNextNoteOnSameString(IList<GuitarNote> notes, int currentIndex, int stringIndex)
    {
        for (var i = currentIndex + 1; i < notes.Count; i++)
        {
            if (notes[i].StringIndex == stringIndex)
                return notes[i];
        }

        return null;
    }

    /// <summary>
    ///     The landing chord's own note on this string, as the loose note the
    ///     export will split it into — so the fret / timing / bend checks run
    ///     against what actually ships.
    /// </summary>
    private static GuitarNote? LandingNote(Chord chord, int stringIndex)
    {
        foreach (var chordNote in chord.ChordNotes)
        {
            if (chordNote.StringIndex == stringIndex)
            {
                return new GuitarNote
                {
                    Position = chord.Position,
                    StringIndex = chordNote.StringIndex,
                    Fret = chordNote.Fret,
                    Sustain = chordNote.Sustain
                };
            }
        }

        return null;
    }

    /// <summary>
    ///     The one thing a Link-Next check needs from its source, so a
    ///     <see cref="GuitarNote" /> and a <see cref="ChordNote" /> can go
    ///     through the identical checks. A chord note borrows its parent
    ///     chord's position and reports against the chord.
    /// </summary>
    private readonly record struct LinkSource(
        FractionalPosition Position,
        FractionalPosition Sustain,
        int StringIndex,
        int Fret,
        int? SlideTo,
        IList<BendValue> BendValues,
        GuitarNote? Note,
        Chord? Chord)
    {
        public FractionalPosition EndPosition => Position + Sustain;

        public static LinkSource From(GuitarNote note) => new(
            note.Position, note.Sustain, note.StringIndex, note.Fret, note.SlideTo, note.BendValues, note, null);

        public static LinkSource From(Chord chord, ChordNote chordNote) => new(
            chord.Position, chordNote.Sustain, chordNote.StringIndex, chordNote.Fret, chordNote.SlideTo,
            chordNote.BendValues, null, chord);

        public ArrangementIssue Issue(ArrangementIssueType type, double timeMs, string message) =>
            Note != null
                ? ArrangementIssues.Note(type, Note, timeMs, message)
                : ArrangementIssues.Chord(type, Chord!, timeMs, message, StringIndex);
    }
}
