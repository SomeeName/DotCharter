namespace DotCharter.Services.Editing.Structure;

/// <summary>What a structure pass proposes. Positions only — the caller turns
///     these into history-backed actions.</summary>
/// <param name="CountPhrase">Position for a new COUNT phrase marker, or null when
///     one already exists.</param>
/// <param name="EndPhrase">Position for a new END phrase marker, or null.</param>
/// <param name="NoGuitarSections">Positions where a silent stretch should be
///     marked <c>noguitar</c>.</param>
public readonly record struct StructurePlan(
    FractionalPosition? CountPhrase,
    FractionalPosition? EndPhrase,
    IReadOnlyList<FractionalPosition> NoGuitarSections)
{
    public static StructurePlan Empty => new(null, null, []);

    public int Count => (CountPhrase.HasValue ? 1 : 0) + (EndPhrase.HasValue ? 1 : 0) + NoGuitarSections.Count;
}

/// <summary>
///     Derives the parts of a chart's structure that are NOT a judgement call.
///     <para>
///         Measured against the 89 hand-finished arrangements in
///         <c>TestData/EOF</c> before any of it was written:
///     </para>
///     <list type="bullet">
///         <item>
///             <b>COUNT and END: 89 of 89 arrangements carry exactly one of
///             each.</b> Every single hand-finished chart. Nothing in the editor
///             created them, so the inspector's <c>NoEndPhrase</c> was permanent
///             furniture on a chart that would in fact export correctly — the
///             RS-XML writer synthesises both. Materialising them makes the
///             editor SHOW what already ships.
///         </item>
///         <item>
///             <b>noguitar: 171 of 171 such sections contain literally no
///             sound.</b> Not "mostly" — all of them. So a silent stretch is the
///             one section a machine can name with certainty, and it is 6.2% of
///             every section in the corpus.
///         </item>
///     </list>
///     <para>
///         <b>What it deliberately does NOT do is invent section boundaries.</b>
///         Not one of the 24 GuitarPro sources in the corpus carries a
///         <c>&lt;Section&gt;</c> marker, and silence does not predict a boundary
///         either — only 14.8% of sections follow a rest longer than 1.5 s. Where
///         a verse becomes a chorus is the charter's call and there is no signal
///         to take it from.
///     </para>
///     <para>
///         <b>The two obvious ways round that were measured and both
///         FAILED</b> (<c>LocalOnly_SectionStructureProbe</c>; do not re-derive
///         them). <i>Labelling by example</i> — "sections repeat, so propagate the
///         label rather than invent it" — nearest-EARLIER-neighbour by note
///         content (Jaccard over (1/12-beat offset, string, fret) sets) names a
///         repeat correctly 56.2% of the time over 2,511 predictable sections, and
///         69.5% only after discarding 58% of the recall. The content-FREE rule
///         "copy the PREVIOUS section's name" scores <b>68.9% at full coverage</b>
///         on the same rows and 76.2% on the very rows where the content matcher
///         is most confident; a hybrid that defers to content when confident is
///         worse than copy-previous at EVERY threshold (59.9%–66.3%). Three other
///         encodings (timing-free, string-invariant, coarse-timing) and four
///         minimum-size gates all land within 1.4 pp. Per-folder accuracy runs
///         4.8%–100%. The ceiling — the true name appearing in ANY earlier
///         section — is 81.6%.
///         <i>Boundary detection</i> — over 12,778 candidate measure starts at an
///         18.3% base rate, scored top-k per arrangement so precision equals
///         recall: repeat-start 39.8%, checkerboard novelty 35.7%, chord-template
///         change 23.4%, note-density change 20.9%, rest/sustain-before 23.0%, and
///         the charter's own FHP anchors 38.3% — against 20.9% for the trivial
///         "fire every N measures". An IN-SAMPLE weight search over all of them
///         (fitted on the corpus it is scored on, so an upper bound) reaches 48.9%,
///         and 22 of 83 charts sit at or below the trivial baseline. A section
///         marker tells Rocksmith where a phrase begins, so half-right is not a
///         draft — it is damage the charter has to find and undo.
///     </para>
///     <para>
///         Pure: every input is a parameter, so the whole thing is provable in the
///         default gate.
///     </para>
/// </summary>
public static class StructureGenerator
{
    /// <summary>
    ///     A silent stretch shorter than this is a rest, not a section. The corpus
    ///     puts 118 of its 132 measurable <c>noguitar</c> sections at 2 s or
    ///     longer (median 9.2 s, p10 1.9 s), and the resume-marker requirement
    ///     below does most of the filtering anyway.
    /// </summary>
    public const double DefaultMinSilenceSeconds = 2.0;

    /// <summary>How close a section marker has to sit to the resume point to
    ///     count as marking it — one beat at 220 BPM is ~273 ms.</summary>
    private const double ResumeToleranceMs = 350.0;

    public static StructurePlan Plan(
        Arrangement arrangement,
        IList<BeatMarker> beats,
        double minSilenceSeconds = DefaultMinSilenceSeconds)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        ArgumentNullException.ThrowIfNull(beats);
        if (beats.Count == 0) return StructurePlan.Empty;

        var count = PlanCountPhrase(arrangement, beats);
        var end = PlanEndPhrase(arrangement, beats);
        var noGuitar = PlanNoGuitarSections(arrangement, beats, minSilenceSeconds);

        return new StructurePlan(count, end, noGuitar);
    }

    /// <summary>
    ///     The count-in phrase: an empty phrase before the first sound, so the
    ///     song fades in cleanly. It is also what the inspector's
    ///     <c>FirstPhraseNotEmpty</c> rule is asking for.
    /// </summary>
    private static FractionalPosition? PlanCountPhrase(Arrangement arrangement, IList<BeatMarker> beats)
    {
        if (HasPhrase(arrangement, "COUNT")) return null;

        // Beat 0 — the same place the RS-XML writer puts it when it synthesises
        // one, so materialising cannot move what already exports.
        return FractionalPosition.Zero;
    }

    /// <summary>
    ///     The END phrase: the first beat AFTER everything stops sounding. Same
    ///     rule as <c>RsXmlSongWriter.FindEndPhrasePosition</c> — deliberately, so
    ///     generating one does not relocate the phrase the export was already
    ///     writing.
    /// </summary>
    private static FractionalPosition? PlanEndPhrase(Arrangement arrangement, IList<BeatMarker> beats)
    {
        if (HasPhrase(arrangement, "END")) return null;

        var lastContentMs = beats[0].Time;
        var hasContent = false;

        foreach (var level in arrangement.Levels)
        {
            foreach (var note in level.Notes)
            {
                lastContentMs = Math.Max(lastContentMs, note.EndPosition.GetPositionMs(beats));
                hasContent = true;
            }

            foreach (var chord in level.Chords)
            {
                lastContentMs = Math.Max(lastContentMs, chord.EndPosition.GetPositionMs(beats));
                hasContent = true;
            }
        }

        if (!hasContent) return new FractionalPosition(Math.Max(0, beats.Count - 1));

        for (var i = 0; i < beats.Count; i++)
        {
            if (beats[i].Time > lastContentMs)
                return new FractionalPosition(i);
        }

        return new FractionalPosition(beats.Count - 1);
    }

    /// <summary>
    ///     A <c>noguitar</c> section at the start of every silent stretch that a
    ///     later section marker already ends.
    ///     <para>
    ///         <b>The resume-marker requirement is what makes this safe.</b>
    ///         A section runs until the next section, so a <c>noguitar</c> dropped
    ///         into a gap whose resume point is unmarked would swallow the music
    ///         that follows it and label it "no guitar" — a chart-wide error
    ///         produced by a convenience. Requiring a marker where the sound comes
    ///         back means the pass only ever fills a hole the charter has already
    ///         bracketed, which is exactly the state the owner's own session
    ///         reached: all sections placed in one sweep, silences left over.
    ///     </para>
    /// </summary>
    private static IReadOnlyList<FractionalPosition> PlanNoGuitarSections(
        Arrangement arrangement, IList<BeatMarker> beats, double minSilenceSeconds)
    {
        var sections = arrangement.EventPoints
            .Where(ep => !string.IsNullOrWhiteSpace(ep.Section))
            .OrderBy(ep => ep.Position)
            .ToList();
        if (sections.Count == 0) return [];

        var sounds = new List<(double StartMs, double EndMs)>();
        foreach (var level in arrangement.Levels)
        {
            foreach (var note in level.Notes)
                sounds.Add((note.Position.GetPositionMs(beats), note.EndPosition.GetPositionMs(beats)));
            foreach (var chord in level.Chords)
                sounds.Add((chord.Position.GetPositionMs(beats), chord.EndPosition.GetPositionMs(beats)));
        }

        if (sounds.Count == 0) return [];
        sounds.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));

        var minSilenceMs = Math.Max(0.0, minSilenceSeconds) * 1000.0;
        var result = new List<FractionalPosition>();

        var soundedUntil = sounds[0].EndMs;
        for (var i = 1; i < sounds.Count; i++)
        {
            var (startMs, endMs) = sounds[i];
            var gapMs = startMs - soundedUntil;
            soundedUntil = Math.Max(soundedUntil, endMs);
            if (gapMs < minSilenceMs) continue;

            // The sound comes back HERE, so a section marker has to be here too.
            var resume = sections.FirstOrDefault(s =>
                Math.Abs(s.Position.GetPositionMs(beats) - startMs) <= ResumeToleranceMs);
            if (resume == null) continue;

            // Start the noguitar on the first beat after the silence begins: the
            // corpus puts 87.1% of sections on a measure and 100% on some beat,
            // and an off-grid section is a section the game renders at the wrong
            // place.
            var startPosition = FirstBeatAfter(beats, soundedUntilAt: startMs - gapMs);
            if (startPosition == null) continue;
            if (startPosition.Value >= resume.Position) continue;

            // Nothing may already claim this stretch — a section the charter put
            // inside the silence is theirs.
            var alreadyMarked = sections.Any(s =>
                s.Position >= startPosition.Value && s.Position < resume.Position);
            if (alreadyMarked) continue;

            if (!result.Contains(startPosition.Value)) result.Add(startPosition.Value);
        }

        return result;
    }

    private static FractionalPosition? FirstBeatAfter(IList<BeatMarker> beats, double soundedUntilAt)
    {
        for (var i = 0; i < beats.Count; i++)
        {
            if (beats[i].Time > soundedUntilAt)
                return new FractionalPosition(i);
        }

        return null;
    }

    private static bool HasPhrase(Arrangement arrangement, string name) =>
        arrangement.EventPoints.Any(ep =>
            string.Equals(ep.Phrase, name, StringComparison.OrdinalIgnoreCase)) ||
        arrangement.Phrases.Any(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase));
}
