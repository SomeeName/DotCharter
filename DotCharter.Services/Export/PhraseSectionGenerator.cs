namespace DotCharter.Services.Export;

/// <summary>
///     Generates Rocksmith phrases + sections for an arrangement that ships
///     none, so the exported SNG (and the manifests derived from it) carry a
///     real COUNT / p0…pN / END structure instead of the degenerate one-riff
///     stub <see cref="Sng.SngFromDomainMapper"/> falls back to.
///     <para>
///         Ported from DLC Builder's
///         <c>Rocksmith2014.XML.Processing/PhraseGenerator.fs</c> (measure-count
///         phrase spacing, the "good phrase time" avoidance of sustains /
///         link-next chains / hand shapes, no-guitar gap sectioning) with three
///         deliberate deviations for the DotCharter save/export pipeline:
///     </para>
///     <list type="bullet">
///         <item>
///             <b>Trigger.</b> DLC Builder regenerates whenever a chart has ≤3
///             phrases and ≤3 sections. That would clobber a deliberately-authored
///             minimal structure (e.g. COUNT/verse/END). We instead generate ONLY
///             when the arrangement has no real authored structure at all: no
///             <see cref="EventPoint.Section"/> anywhere, and no phrase iteration
///             (or <see cref="EventPoint.Phrase"/> marker) that names anything
///             other than the structural COUNT/END. See
///             <see cref="HasAuthoredStructure"/>.
///         </item>
///         <item>
///             <b>Beat map is never mutated.</b> DLC Builder inserts a lead-in
///             beat (or throws) when there is no room for the COUNT phrase before
///             the content; this can run inside the save companion, so it must
///             never throw and must not touch the beat map. COUNT is placed at the
///             first beat and p0 is allowed to start at content start even when
///             that is tight against COUNT.
///         </item>
///         <item>
///             <b>Phrase length.</b> DLC Builder cuts every 9 measures; charters cut
///             about every 4, on a pattern change. Body phrases start on a bar line
///             about every <see cref="MeasuresPerPhrase"/> measures, moved a measure
///             either way onto a bar whose content differs from the bar before, and
///             never on a bar inside a sustain, link-next chain or hand shape (so the
///             generator makes no D17/D18 finding). The first section, at the first
///             note, is the intro.
///         </item>
///     </list>
///     Pure: no logging, no DI, no I/O. Mutates the passed arrangement in place —
///     callers hand it an already-cloned arrangement (export dialog + save
///     companion both build from a <c>ChartProjectCloner</c> snapshot).
/// </summary>
internal static class PhraseSectionGenerator
{
    /// <summary>Body phrases are cut about every this many measures, on a pattern
    /// change where one falls a measure either side.</summary>
    private const int MeasuresPerPhrase = 4;

    /// <summary>The longest phrase the generator leaves while a bar line is free to
    /// cut on; past it only a bar inside a held span keeps the phrase going.</summary>
    private const int MaxMeasuresPerPhrase = 8;

    /// <summary>No phrase is cut shorter than this many measures.</summary>
    private const int MinMeasuresPerPhrase = 2;

    /// <summary>The section at the first note: a chart's opening is its intro.</summary>
    private const string FirstSectionName = "intro";

    /// <summary>Minimum distance required between two body phrases, in ms.</summary>
    private const int MinimumPhraseSeparationMs = 2000;

    /// <summary>A gap between content pieces of at least this long, in ms, is
    /// sectioned off as "noguitar".</summary>
    private const int MinimumNoGuitarGapMs = 2500;

    /// <summary>
    ///     Generates COUNT / p0…pN / END phrases with riff/noguitar sections for
    ///     <paramref name="arrangement"/> when it carries no authored phrase or
    ///     section structure (see the type remarks for the trigger). Mutates the
    ///     arrangement in place: replaces <see cref="Arrangement.Phrases"/>,
    ///     <see cref="Arrangement.PhraseIterations"/>,
    ///     <see cref="Arrangement.PhraseInfos"/> and the section/phrase fields of
    ///     <see cref="Arrangement.EventPoints"/> (EventPoints carrying only
    ///     <see cref="EventPoint.Events"/> are preserved).
    /// </summary>
    /// <returns><c>true</c> when structure was generated; <c>false</c> when the
    /// arrangement already had authored structure, had no beat map, or had no
    /// note/chord content to build around.</returns>
    public static bool GenerateIfAbsent(Arrangement arrangement, IList<BeatMarker> beats, double songLengthMs)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        ArgumentNullException.ThrowIfNull(beats);

        if (beats.Count == 0) return false;
        if (HasAuthoredStructure(arrangement)) return false;

        var bounds = ContentBounds(arrangement, beats);
        if (bounds is null) return false; // no note/chord content: leave the stub to handle it
        var (contentStartMs, contentEndMs) = bounds.Value;

        var beatMs = new int[beats.Count];
        for (var i = 0; i < beats.Count; i++) beatMs[i] = Round(beats[i].Time);

        var countMs = beatMs[0];

        // p0 starts at the last beat <= content start; never coincide with COUNT.
        var p0BeatMs = LastBeatAtOrBefore(beatMs, contentStartMs);
        var p0Ms = p0BeatMs > countMs ? p0BeatMs : Math.Max(contentStartMs, countMs + 1);

        // END phrase after the content ends: the first beat past it, else +1ms.
        var endMs = FirstBeatAfter(beatMs, contentEndMs) ?? contentEndMs + 1;
        if (endMs <= contentEndMs) endMs = contentEndMs + 1;
        var songLenMs = Round(songLengthMs);
        if (songLenMs > 0 && endMs > songLenMs) endMs = Math.Max(contentEndMs + 1, songLenMs);
        if (endMs <= p0Ms) endMs = p0Ms + 1;

        var occupied = BuildOccupiedSpans(arrangement, beats);
        var cuts = PlanCuts(arrangement, beats, beatMs, p0Ms, endMs, occupied);

        // Replace whatever structure was there (all of it is unauthored).
        ClearStructure(arrangement);

        var countId = AddPhrase(arrangement, "COUNT");
        AddIteration(arrangement, beats, countMs, countId);
        arrangement.PhraseInfos["COUNT"] = new PhraseInfo(0, false);

        var p0Id = AddPhrase(arrangement, "p0");
        AddIteration(arrangement, beats, p0Ms, p0Id);
        AddSection(arrangement, beats, p0Ms, FirstSectionName);

        var bodyIndex = 0;
        foreach (var cut in cuts)
            AddBodyPhrase(arrangement, beats, cut.TimeMs, cut.IsNoGuitar ? "noguitar" : "riff", ref bodyIndex);

        var endId = AddPhrase(arrangement, "END");
        AddIteration(arrangement, beats, endMs, endId);
        AddSection(arrangement, beats, endMs, "noguitar");
        arrangement.PhraseInfos["END"] = new PhraseInfo(0, false);

        return true;
    }

    // ---- trigger ----------------------------------------------------------

    /// <summary>
    ///     True when the arrangement already expresses authored structure that
    ///     must be preserved: any non-empty <see cref="EventPoint.Section"/>, or
    ///     any phrase (referenced by an iteration OR an <see cref="EventPoint.Phrase"/>
    ///     marker) whose name is something other than the structural COUNT/END.
    ///     The <see cref="EventPoint.Phrase"/> check strengthens the spec's
    ///     PhraseIteration-only predicate so imported charts that carry their
    ///     phrase names on event points (SNG/EOF/rscp importers populate
    ///     <see cref="EventPoint.Phrase"/>) are never wiped.
    /// </summary>
    private static bool HasAuthoredStructure(Arrangement arrangement)
    {
        foreach (var ep in arrangement.EventPoints)
        {
            if (!string.IsNullOrEmpty(ep.Section)) return true;
            if (IsNonStructuralPhrase(ep.Phrase)) return true;
        }

        foreach (var iteration in arrangement.PhraseIterations)
        {
            var id = iteration.PhraseId;
            if (id >= 0 && id < arrangement.Phrases.Count && IsNonStructuralPhrase(arrangement.Phrases[id]))
                return true;
        }

        return false;
    }

    private static bool IsNonStructuralPhrase(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        return !name.Equals("COUNT", StringComparison.OrdinalIgnoreCase)
               && !name.Equals("END", StringComparison.OrdinalIgnoreCase);
    }

    // ---- structure mutation ----------------------------------------------

    private static void ClearStructure(Arrangement arrangement)
    {
        arrangement.Phrases.Clear();
        arrangement.PhraseIterations.Clear();
        arrangement.PhraseInfos.Clear();

        // Strip the phrase/section fields; keep event points that still carry an
        // Events string (time-signature markers etc.), drop the ones left empty.
        for (var i = arrangement.EventPoints.Count - 1; i >= 0; i--)
        {
            var ep = arrangement.EventPoints[i];
            ep.Section = "";
            ep.Phrase = "";
            ep.Status = SectionStatus.None;
            if (string.IsNullOrEmpty(ep.Events)) arrangement.EventPoints.RemoveAt(i);
        }
    }

    private static void AddBodyPhrase(
        Arrangement arrangement, IList<BeatMarker> beats, int timeMs, string section, ref int bodyIndex)
    {
        var id = AddPhrase(arrangement, $"p{++bodyIndex}");
        AddIteration(arrangement, beats, timeMs, id);
        AddSection(arrangement, beats, timeMs, section);
    }

    private static int AddPhrase(Arrangement arrangement, string name)
    {
        arrangement.Phrases.Add(name);
        return arrangement.Phrases.Count - 1;
    }

    private static void AddIteration(Arrangement arrangement, IList<BeatMarker> beats, int timeMs, int phraseId)
    {
        arrangement.PhraseIterations.Add(
            new PhraseIteration(TimelineMath.PositionFromMs(timeMs, beats), phraseId));
    }

    private static void AddSection(Arrangement arrangement, IList<BeatMarker> beats, int timeMs, string section)
    {
        arrangement.EventPoints.Add(new EventPoint
        {
            Position = TimelineMath.PositionFromMs(timeMs, beats),
            Section = section
        });
    }

    // ---- content geometry -------------------------------------------------

    /// <summary>First note/chord onset and last content end (sustains + hand
    /// shape ends included) across every level; null when there is no
    /// note/chord content.</summary>
    private static (int StartMs, int EndMs)? ContentBounds(Arrangement arrangement, IList<BeatMarker> beats)
    {
        var start = double.MaxValue;
        var end = double.MinValue;

        foreach (var level in arrangement.Levels)
        {
            foreach (var note in level.Notes)
            {
                start = Math.Min(start, note.Position.GetPositionMs(beats));
                end = Math.Max(end, note.EndPosition.GetPositionMs(beats));
            }

            foreach (var chord in level.Chords)
            {
                start = Math.Min(start, chord.Position.GetPositionMs(beats));
                end = Math.Max(end, chord.EndPosition.GetPositionMs(beats));
            }

            foreach (var handShape in level.HandShapes)
                end = Math.Max(end, handShape.EndPosition.GetPositionMs(beats));
        }

        if (start == double.MaxValue) return null;
        return (Round(start), Round(Math.Max(start, end)));
    }

    /// <summary>Spans a phrase cut must not land strictly inside: note sustains,
    /// chord sustains, hand shapes, and whole link-next chains (so a cut never
    /// lands on an interior linked note head). Mirrors the intent of
    /// <c>findGoodPhraseTime</c>.</summary>
    private static List<(int Start, int End)> BuildOccupiedSpans(Arrangement arrangement, IList<BeatMarker> beats)
    {
        var spans = new List<(int Start, int End)>();

        foreach (var level in arrangement.Levels)
        {
            foreach (var note in level.Notes)
                AddSpan(spans, Round(note.Position.GetPositionMs(beats)), Round(note.EndPosition.GetPositionMs(beats)));

            foreach (var chord in level.Chords)
            {
                AddSpan(spans, Round(chord.Position.GetPositionMs(beats)),
                    Round(chord.EndPosition.GetPositionMs(beats)));
            }

            foreach (var handShape in level.HandShapes)
            {
                AddSpan(spans, Round(handShape.Position.GetPositionMs(beats)),
                    Round(handShape.EndPosition.GetPositionMs(beats)));
            }

            AddLinkNextChainSpans(level, beats, spans);
        }

        return spans;
    }

    private static void AddLinkNextChainSpans(
        ChartLevel level, IList<BeatMarker> beats, List<(int Start, int End)> spans)
    {
        foreach (var group in level.Notes.GroupBy(n => n.StringIndex))
        {
            var ordered = group.OrderBy(n => n.Position).ToList();
            var i = 0;
            while (i < ordered.Count)
            {
                if (ordered[i].LinkNext && i + 1 < ordered.Count)
                {
                    var chainStart = Round(ordered[i].Position.GetPositionMs(beats));
                    var j = i;
                    while (j + 1 < ordered.Count && ordered[j].LinkNext) j++;
                    AddSpan(spans, chainStart, Round(ordered[j].EndPosition.GetPositionMs(beats)));
                    i = j + 1;
                }
                else
                {
                    i++;
                }
            }
        }
    }

    private static void AddSpan(List<(int Start, int End)> spans, int start, int end)
    {
        if (end > start) spans.Add((start, end));
    }

    /// <summary>Merged content coverage gaps of at least
    /// <see cref="MinimumNoGuitarGapMs"/>. Each is (end of content before the
    /// gap, start of content after the gap).</summary>
    private static List<(int Start, int End)> BuildNoGuitarGaps(Arrangement arrangement, IList<BeatMarker> beats)
    {
        var spans = new List<(int Start, int End)>();
        foreach (var level in arrangement.Levels)
        {
            foreach (var note in level.Notes)
            {
                var s = Round(note.Position.GetPositionMs(beats));
                spans.Add((s, Math.Max(s, Round(note.EndPosition.GetPositionMs(beats)))));
            }

            foreach (var chord in level.Chords)
            {
                var s = Round(chord.Position.GetPositionMs(beats));
                spans.Add((s, Math.Max(s, Round(chord.EndPosition.GetPositionMs(beats)))));
            }
        }

        if (spans.Count == 0) return [];
        spans.Sort((a, b) => a.Start.CompareTo(b.Start));

        var gaps = new List<(int Start, int End)>();
        var coverEnd = spans[0].End;
        for (var i = 1; i < spans.Count; i++)
        {
            if (spans[i].Start > coverEnd)
            {
                if (spans[i].Start - coverEnd >= MinimumNoGuitarGapMs)
                    gaps.Add((coverEnd, spans[i].Start));
                coverEnd = spans[i].End;
            }
            else
            {
                coverEnd = Math.Max(coverEnd, spans[i].End);
            }
        }

        return gaps;
    }

    private readonly record struct ForcedCut(int TimeMs, bool IsNoGuitar);

    /// <summary>The cuts after p0, in time order: a noguitar cut and a resume cut
    /// around each silent gap, and between them body cuts on bar lines
    /// (<see cref="AddBodyCuts"/>).</summary>
    private static List<ForcedCut> PlanCuts(
        Arrangement arrangement, IList<BeatMarker> beats, int[] beatMs, int p0Ms, int endMs,
        List<(int Start, int End)> occupied)
    {
        var bars = BarStarts(beats, beatMs);
        var signatures = BarSignatures(arrangement, beats, bars);
        var gapCuts = BuildForcedGapCuts(BuildNoGuitarGaps(arrangement, beats), beatMs, bars);
        var cuts = new List<ForcedCut>();
        var segmentStart = p0Ms;
        var inContent = true;
        foreach (var gapCut in gapCuts)
        {
            var lastMs = cuts.Count > 0 ? cuts[^1].TimeMs : p0Ms;
            if (gapCut.TimeMs <= lastMs || gapCut.TimeMs >= endMs) continue;
            if (gapCut.IsNoGuitar && inContent)
                AddBodyCuts(cuts, bars, signatures, occupied, segmentStart, gapCut.TimeMs);
            cuts.Add(gapCut);
            inContent = !gapCut.IsNoGuitar;
            segmentStart = gapCut.TimeMs;
        }

        if (inContent) AddBodyCuts(cuts, bars, signatures, occupied, segmentStart, endMs);
        return cuts;
    }

    /// <summary>Measures from the last cut at which a body cut is tried, best first:
    /// on a pattern change at four, three or five measures, else plainly at four,
    /// three, five and on up to <see cref="MaxMeasuresPerPhrase"/>.</summary>
    private static readonly (int Measures, bool NeedsChange)[] CutPreference =
    [
        (MeasuresPerPhrase, true), (MeasuresPerPhrase - 1, true), (MeasuresPerPhrase + 1, true),
        (MeasuresPerPhrase, false), (MeasuresPerPhrase - 1, false), (MeasuresPerPhrase + 1, false),
        (MeasuresPerPhrase + 2, false), (MeasuresPerPhrase + 3, false), (MaxMeasuresPerPhrase, false)
    ];

    /// <summary>Body cuts on bar lines in [<paramref name="startMs"/>,
    /// <paramref name="endMs"/>): never on a bar inside a sustain, link-next chain or
    /// hand shape, never closer than <see cref="MinimumPhraseSeparationMs"/>, and
    /// never leaving less than <see cref="MinMeasuresPerPhrase"/> measures before
    /// <paramref name="endMs"/>.</summary>
    private static void AddBodyCuts(
        List<ForcedCut> cuts, int[] bars, string[] signatures, List<(int Start, int End)> occupied,
        int startMs, int endMs)
    {
        var lastMs = startMs;
        while (NextBodyCut(bars, signatures, occupied, lastMs, endMs) is { } cut)
        {
            cuts.Add(new ForcedCut(cut, false));
            lastMs = cut;
        }
    }

    private static int? NextBodyCut(
        int[] bars, string[] signatures, List<(int Start, int End)> occupied, int lastMs, int endMs)
    {
        var first = 0;
        while (first < bars.Length && bars[first] <= lastMs) first++;

        bool Usable(int index) =>
            index < bars.Length && bars[index] - lastMs >= MinimumPhraseSeparationMs
                                && index + MinMeasuresPerPhrase < bars.Length
                                && bars[index + MinMeasuresPerPhrase] <= endMs
                                && !IsInsideAnySpan(occupied, bars[index]);

        foreach (var (measures, needsChange) in CutPreference)
        {
            var index = first + measures - 1;
            if (!Usable(index)) continue;
            if (needsChange && index > 0 && signatures[index] == signatures[index - 1]) continue;
            return bars[index];
        }

        // Every bar up to the longest phrase sits inside a held span: cut on the first free one after it.
        for (var index = first + MaxMeasuresPerPhrase; index < bars.Length && bars[index] < endMs; index++)
        {
            if (Usable(index)) return bars[index];
        }

        return null;
    }

    /// <summary>The ms of every bar line; every fourth beat when the beat map marks none.</summary>
    private static int[] BarStarts(IList<BeatMarker> beats, int[] beatMs)
    {
        var bars = new List<int>();
        for (var i = 0; i < beats.Count; i++)
        {
            if (beats[i].IsMeasure) bars.Add(beatMs[i]);
        }

        if (bars.Count > 0) return [.. bars];
        for (var i = 0; i < beatMs.Length; i += MeasuresPerPhrase) bars.Add(beatMs[i]);
        return [.. bars];
    }

    /// <summary>What each bar plays on the busiest level, (offset in the bar, string,
    /// fret) per note and (offset, template) per chord, so two bars with the same
    /// signature repeat one pattern and a different one is a pattern change.</summary>
    private static string[] BarSignatures(Arrangement arrangement, IList<BeatMarker> beats, int[] bars)
    {
        var parts = new List<string>[bars.Length];
        for (var i = 0; i < parts.Length; i++) parts[i] = [];
        var level = arrangement.Levels.MaxBy(l => l.Notes.Count + l.Chords.Count);
        if (level != null)
        {
            foreach (var note in level.Notes) AddToBar(parts, bars, beats, note.Position, $"{note.StringIndex}/{note.Fret}");
            foreach (var chord in level.Chords) AddToBar(parts, bars, beats, chord.Position, $"c{chord.TemplateId}");
        }

        return [.. parts.Select(p => string.Join(';', p.Order(StringComparer.Ordinal)))];
    }

    private static void AddToBar(
        List<string>[] parts, int[] bars, IList<BeatMarker> beats, FractionalPosition position, string what)
    {
        var ms = Round(position.GetPositionMs(beats));
        var bar = Array.BinarySearch(bars, ms);
        if (bar < 0) bar = ~bar - 1;
        if (bar >= 0) parts[bar].Add($"{(ms - bars[bar]) / 10}:{what}");
    }

    /// <summary>For each no-guitar gap: a "noguitar" cut on the first bar line in the
    /// silence (else its first beat), and a "riff" resume cut on the last bar line
    /// before the content after it (else that content's start).</summary>
    private static List<ForcedCut> BuildForcedGapCuts(List<(int Start, int End)> gaps, int[] beatMs, int[] bars)
    {
        var cuts = new List<ForcedCut>(gaps.Count * 2);
        foreach (var (gapStart, gapEnd) in gaps)
        {
            var noGuitarMs = FirstAtOrAfter(bars, gapStart) is { } bar && bar < gapEnd
                ? bar
                : FirstAtOrAfter(beatMs, gapStart) is { } beat && beat < gapEnd ? beat : gapStart;
            var resumeMs = LastAtOrBefore(bars, gapEnd) is { } resumeBar && resumeBar > noGuitarMs ? resumeBar : gapEnd;
            cuts.Add(new ForcedCut(noGuitarMs, true));
            cuts.Add(new ForcedCut(resumeMs, false));
        }

        cuts.Sort((a, b) => a.TimeMs.CompareTo(b.TimeMs));
        return cuts;
    }

    private static bool IsInsideAnySpan(List<(int Start, int End)> spans, int timeMs)
    {
        foreach (var (start, end) in spans)
        {
            if (start < timeMs && end > timeMs)
                return true;
        }

        return false;
    }

    // ---- beat lookups -----------------------------------------------------

    private static int LastBeatAtOrBefore(int[] beatMs, int timeMs)
    {
        var result = beatMs[0];
        foreach (var t in beatMs)
        {
            if (t > timeMs) break;
            result = t;
        }

        return result;
    }

    private static int? FirstAtOrAfter(int[] times, int timeMs)
    {
        foreach (var t in times)
        {
            if (t >= timeMs)
                return t;
        }

        return null;
    }

    private static int? LastAtOrBefore(int[] times, int timeMs)
    {
        int? result = null;
        foreach (var t in times)
        {
            if (t > timeMs) break;
            result = t;
        }

        return result;
    }

    private static int? FirstBeatAfter(int[] beatMs, int timeMs)
    {
        foreach (var t in beatMs)
        {
            if (t > timeMs)
                return t;
        }

        return null;
    }

    private static int Round(double ms) => (int)Math.Round(ms);
}
