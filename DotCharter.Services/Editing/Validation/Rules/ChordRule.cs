namespace DotCharter.Services.Editing.Validation.Rules;

/// <summary>
///     Per-chord checks: song-end boundary, noguitar, tone change, per-chord-note
///     fret/bass/harmonic/bend sanity, muted-mix chords, link-next chord
///     integrity, pull-off + anchor coincidence, chord template fingering /
///     barre-over-open-strings, and the D06 summary for the chords whose shape
///     cannot be resolved to a template at all.
/// </summary>
public sealed class ChordRule : IArrangementRule
{
    private const int TechniqueMinSustainMs = 5;

    /// <summary>
    ///     The two template-fingering verdicts, memoized per pass.
    ///     <para>
    ///         <b>A <c>ChordTemplate</c> is SHARED</b> — one instance backs every
    ///         chord with that shape — and both checks are pure functions of it,
    ///         so asking per CHORD re-answered the same question for every
    ///         occurrence of every shape. Each answer would cost six LINQ
    ///         allocations (five of them <c>OrderBy</c> buffers), on a rule that
    ///         re-runs after every history step.
    ///     </para>
    ///     <para>
    ///         Keyed by <c>TemplateId</c>, not by the template: <c>ChordTemplate</c>
    ///         has VALUE equality excluding <c>Id</c>, so a
    ///         <c>Dictionary&lt;ChordTemplate, …&gt;</c> would hash the shape
    ///         dictionaries on every lookup — see <c>DotCharter.Core/AGENTS.md</c>.
    ///         The cache lives for ONE pass, so a template edited between passes
    ///         is re-judged. The entry carries its own <see cref="Template" /> so
    ///         the hit can be reference-checked: <c>-1</c> is
    ///         <c>ChordTemplate.Id</c>'s default, so two UNNUMBERED templates
    ///         would otherwise share a cache slot and one would be judged as the
    ///         other.
    ///     </para>
    /// </summary>
    private readonly record struct FingeringVerdict(ChordTemplate Template, bool Strange, bool BarreOverOpen);

    public void Validate(ArrangementValidationContext context, ICollection<ArrangementIssue> issues)
    {
        var arr = context.Arrangement;
        var beats = context.Beats;
        var isBass = arr.ArrangementType == ArrangementType.Bass;
        // I45 needs it: an open string is one AT OR BELOW the capo, not fret 0.
        var capo = Math.Max(0, arr.Capo);
        Dictionary<int, FingeringVerdict>? verdicts = null;
        var untemplated = 0;

        foreach (var level in arr.Levels)
        {
            foreach (var chord in level.Chords)
            {
                var timeMs = chord.Position.GetPositionMs(beats);

                CheckPlacement(context, issues, chord, timeMs);
                var summary = CheckChordNotes(beats, issues, chord, timeMs, isBass);
                CheckLinkAndPullOff(context, issues, level, chord, timeMs, summary);
                if (!CheckFingering(context, issues, chord, timeMs, capo, ref verdicts) && chord.ChordNotes.Count > 0)
                    untemplated++;
            }
        }

        // D06: the honest residue. One summary for the whole arrangement — a
        // per-chord finding would flood a DD chart with the same fact once per
        // difficulty level, which is exactly how I29 buried every real finding.
        if (untemplated > 0)
        {
            var counted = untemplated == 1 ? "1 chord names" : $"{untemplated} chords name";
            issues.Add(new ArrangementIssue(
                ArrangementIssueType.ChordWithoutTemplate,
                $"{counted} no chord template, and no template in this arrangement matches the notes they " +
                "play, so the chord-fingering checks (I44/I45, L06–L08) cannot judge them. Saving mints a " +
                "template with GUESSED fingering for each."));
        }
    }

    private static void CheckPlacement(ArrangementValidationContext context, ICollection<ArrangementIssue> issues, Chord chord,
        double timeMs)
    {
        if (context.TotalAudioLengthMs is { } endMs && timeMs >= endMs)
        {
            issues.Add(ArrangementIssues.Chord(ArrangementIssueType.NoteAfterSongEnd, chord, timeMs,
                "Chord is placed after the end of the song audio."));
        }

        if (context.IsInsideNoguitar(timeMs))
        {
            issues.Add(ArrangementIssues.Chord(ArrangementIssueType.NoteInsideNoguitarSection, chord, timeMs,
                "Chord is placed inside a no-guitar section."));
        }

        if (context.IsOnToneChange(timeMs))
        {
            issues.Add(ArrangementIssues.Chord(ArrangementIssueType.ToneChangeOnNote, chord, timeMs,
                "A tone change happens at the same time as this chord."));
        }
    }

    /// <summary>
    ///     What the chord-level checks need to know about the chord's notes, gathered
    ///     in the per-note loop rather than by three more LINQ passes: every
    ///     Count/All/Any over an ObservableCollection boxes an enumerator, and this
    ///     rule re-runs after every history step on every level.
    /// </summary>
    private readonly record struct ChordNoteSummary(bool AnyLinks, bool AnyFrettedPullOff);

    private static ChordNoteSummary CheckChordNotes(IList<BeatMarker> beats, ICollection<ArrangementIssue> issues, Chord chord,
        double timeMs, bool isBass)
    {
        var mutedCount = 0;
        var anyLinks = false;
        var anyFrettedPullOff = false;

        // Indexed, not `foreach`: ObservableCollection<T> derives from
        // Collection<T>, which exposes no struct enumerator, so every
        // `foreach` over one BOXES an enumerator — measured at 40 bytes
        // per chord, on a rule that re-runs after every history step.
        var chordNotes = chord.ChordNotes;
        if (chordNotes.Count == 0) return default;

        for (var ci = 0; ci < chordNotes.Count; ci++)
        {
            var cn = chordNotes[ci];
            if (cn.Mute == MuteType.Full) mutedCount++;
            if (cn.LinkNext) anyLinks = true;
            if (cn.Hopo == HopoType.PullOff && cn.Fret > 0) anyFrettedPullOff = true;

            CheckChordNote(beats, issues, chord, cn, timeMs, isBass);
        }

        if (mutedCount > 0 && mutedCount < chordNotes.Count)
        {
            issues.Add(ArrangementIssues.Chord(ArrangementIssueType.MutedStringInNonMutedChord, chord, timeMs,
                "Chord has some — but not all — strings fret-hand muted."));
        }

        return new ChordNoteSummary(anyLinks, anyFrettedPullOff);
    }

    private static void CheckChordNote(IList<BeatMarker> beats, ICollection<ArrangementIssue> issues, Chord chord, ChordNote cn,
        double timeMs, bool isBass)
    {
        if (cn.Fret > 24)
        {
            issues.Add(ArrangementIssues.Chord(ArrangementIssueType.FretNumberMoreThan24, chord, timeMs,
                $"Chord note fret {cn.Fret} exceeds the maximum of 24.", stringIndex: cn.StringIndex));
        }

        if (isBass && cn.StringIndex >= 4)
        {
            issues.Add(ArrangementIssues.Chord(ArrangementIssueType.InvalidBassArrangementString, chord, timeMs,
                $"Bass arrangements only have 4 strings (chord uses string {cn.StringIndex}).",
                stringIndex: cn.StringIndex));
        }

        var sustainMs = cn.Sustain == FractionalPosition.Zero
            ? 0.0
            : (chord.Position + cn.Sustain).GetPositionMs(beats) - timeMs;

        if (cn.Harmonic == HarmonicType.Normal && cn.BendValues.Count > 0)
        {
            issues.Add(ArrangementIssues.Chord(ArrangementIssueType.NaturalHarmonicWithBend, chord, timeMs,
                "Chord note has natural harmonic + bend.", stringIndex: cn.StringIndex));
        }

        if (cn.Harmonic == HarmonicType.Normal && cn.Fret == 7 && sustainMs > 0)
        {
            issues.Add(ArrangementIssues.Chord(ArrangementIssueType.SeventhFretHarmonicWithSustain, chord, timeMs,
                "Chord 7th-fret natural harmonic with sustain.", stringIndex: cn.StringIndex));
        }

        if (cn.BendValues.Count > 0 && !cn.BendValues.Any(bv => bv.Step != 0.0))
        {
            issues.Add(ArrangementIssues.Chord(ArrangementIssueType.MissingBendValue, chord, timeMs,
                "Chord note marked as bend but all steps are 0.", stringIndex: cn.StringIndex));
        }

        var requiresSustain = cn.Tremolo || cn.Vibrato || cn.SlideTo.HasValue || cn.UnpitchedSlide;
        if (requiresSustain && sustainMs < TechniqueMinSustainMs)
        {
            issues.Add(ArrangementIssues.Chord(ArrangementIssueType.TechniqueNoteWithoutSustain, chord, timeMs,
                "Chord technique note needs sustain.", stringIndex: cn.StringIndex));
        }

        if (cn.LinkNext && cn.UnpitchedSlide)
        {
            issues.Add(ArrangementIssues.Chord(ArrangementIssueType.UnpitchedSlideWithLinkNext, chord, timeMs,
                "Chord note: unpitched slide + LinkNext.", stringIndex: cn.StringIndex));
        }
    }

    private static void CheckLinkAndPullOff(ArrangementValidationContext context, ICollection<ArrangementIssue> issues,
        ChartLevel level, Chord chord, double timeMs, ChordNoteSummary summary)
    {
        // I33. This read `chord.HasLinkNext() && chord.ChordNotes.All(cn
        // => !cn.LinkNext)` — and HasLinkNext() IS "any chord note
        // links", so the check was `A && !A` and could not fire on any
        // chart ever authored. A dead check is worse than none: the code
        // reads as covered.
        //
        // The condition that IS representable is the one the SNG mapper
        // uses. ForceNoNotes suppresses the <chordNotes> block
        // (SngFromDomainMapper: `hasChordNotes = ChordNotes.Count > 0 &&
        // !ForceNoNotes`), while CreateMaskForChord still stamps
        // RsNoteMask.Parent from HasLinkNext() — so the chord ships
        // claiming to be a link parent with nothing to carry the link.
        if (summary.AnyLinks && chord.ForceNoNotes)
        {
            issues.Add(ArrangementIssues.Chord(ArrangementIssueType.MissingLinkNextChordNotes, chord, timeMs,
                "Chord links into the next sound but is set to force no chord notes, so the export " +
                "has nothing to attach the link to."));
        }

        if (!summary.AnyFrettedPullOff) return;

        var here = context.AnchorAt(level, chord.Position);
        if (here == null) return;

        var prevAnchor = context.PreviousAnchor(level, chord.Position);
        if (prevAnchor != null && prevAnchor.Fret != here.Fret)
        {
            issues.Add(ArrangementIssues.Chord(ArrangementIssueType.PositionShiftIntoPullOff, chord, timeMs,
                "Pull-off chord coincides with an anchor change."));
        }
    }

    /// <summary>
    ///     I44/I45. Returns false when the chord resolves to no template at all, so
    ///     nothing here can be judged — still not "no issue": the caller states it
    ///     ONCE as D06 rather than once per chord.
    ///     <para>
    ///         This was gated on <c>chord.TemplateId &gt;= 0</c>, which turns a missing
    ///         input into a clean bill of health: a chord still on the -1 sentinel —
    ///         the tab importers' idempotency skips leave some, and hand-authored or
    ///         partial states exist — was reported as having no fingering problem
    ///         rather than as unjudged. The context answers the question the guard was
    ///         standing in for: the template this chord's shape resolves to, which is
    ///         the one ChartSpacingEnforcer will repoint it at on the next save.
    ///     </para>
    /// </summary>
    private static bool CheckFingering(ArrangementValidationContext context, ICollection<ArrangementIssue> issues, Chord chord,
        double timeMs, int capo, ref Dictionary<int, FingeringVerdict>? verdicts)
    {
        var template = context.ChordTemplateFor(chord);
        if (template == null) return false;

        verdicts ??= [];
        if (!verdicts.TryGetValue(template.Id, out var verdict)
            || !ReferenceEquals(verdict.Template, template))
        {
            verdict = new FingeringVerdict(template,
                HasStrangeFingering(template), HasBarreOverOpenStrings(template, capo));
            verdicts[template.Id] = verdict;
        }

        if (verdict.Strange)
        {
            issues.Add(ArrangementIssues.Chord(ArrangementIssueType.PossiblyWrongChordFingering, chord, timeMs,
                "Chord fingering looks wrong: a fretted string is below the lowest non-thumb finger."));
        }

        if (verdict.BarreOverOpen)
        {
            issues.Add(ArrangementIssues.Chord(ArrangementIssueType.BarreOverOpenStrings, chord, timeMs,
                "Chord has a barre that crosses an open string."));
        }

        return true;
    }

    private static bool HasStrangeFingering(ChordTemplate template)
    {
        // The lowest non-thumb finger, and the string it sits on. Ties resolve to
        // the first entry the dictionary yields, exactly as the `First(kv =>
        // kv.Value == lowestFinger)` this replaces did.
        var lowestFinger = int.MaxValue;
        var lowestFingerString = 0;
        foreach (var (s, finger) in template.Fingers)
        {
            if (finger <= 0) continue;
            if (finger >= lowestFinger) continue;
            lowestFinger = finger;
            lowestFingerString = s;
        }

        if (lowestFinger == int.MaxValue) return false;
        if (!template.Frets.TryGetValue(lowestFingerString, out var lowestFingerFret)) return false;

        foreach (var (s, finger) in template.Fingers)
        {
            if (finger <= 0) continue;
            if (!template.Frets.TryGetValue(s, out var fret)) continue;
            if (fret <= 0) continue;
            if (fret < lowestFingerFret) return true;
        }
        return false;
    }

    /// <summary>
    ///     A finger spanning two strings is a barre; an OPEN string strictly
    ///     between its ends cannot be sounding under it — the barring finger
    ///     lies across that string too. <b>"Open" is AT OR BELOW THE CAPO</b>,
    ///     not fret 0; see the loop.
    ///     <para>
    ///         Only the lowest and highest string per finger were ever used, so
    ///         a sorted list (four <c>OrderBy</c>
    ///         buffers per chord) is unnecessary: it is just a min/max scan.
    ///     </para>
    /// </summary>
    private static bool HasBarreOverOpenStrings(ChordTemplate template, int capo)
    {
        for (var finger = 1; finger <= 4; finger++)
        {
            int low = int.MaxValue, high = int.MinValue, count = 0;
            foreach (var (s, f) in template.Fingers)
            {
                if (f != finger) continue;
                count++;
                if (s < low) low = s;
                if (s > high) high = s;
            }

            if (count < 2) continue;
            // `<= capo`, not `== 0`. Under a capo an OPEN string is written as
            // the capo fret — that is what TemplateAndOverlapRule means by
            // `isOpen = fret <= capo`, and the value ArrangementAutoFixService
            // .FixCapoFrets normalizes a sub-capo template fret to. Testing
            // fret 0 alone made this whole check silent on every capo'd chart,
            // so the unplayable shape validated clean. A string absent from
            // Frets is UNPLAYED and is skipped either way (an unused string is
            // ABSENT, never -1 — DotCharter.Core/AGENTS.md).
            for (var s = low + 1; s < high; s++)
            {
                if (template.Frets.TryGetValue(s, out var fret) && fret <= capo)
                    return true;
            }
        }
        return false;
    }
}
