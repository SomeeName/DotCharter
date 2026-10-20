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
///         link-next chains / hand shapes, no-guitar gap sectioning) with two
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
///             that is tight against COUNT. Body phrases land on beats (nudged
///             forward past sustains) rather than on arbitrary note times.
///         </item>
///     </list>
///     Pure: no logging, no DI, no I/O. Mutates the passed arrangement in place —
///     callers hand it an already-cloned arrangement (export dialog + save
///     companion both build from a <c>ChartProjectCloner</c> snapshot).
/// </summary>
internal static class PhraseSectionGenerator
{
    /// <summary>Body phrases are cut roughly every this many measures.</summary>
    private const int MeasuresPerPhrase = 9;

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
        var gaps = BuildNoGuitarGaps(arrangement, beats);
        var forced = BuildForcedGapCuts(gaps, beatMs);

        // Replace whatever structure was there (all of it is unauthored).
        ClearStructure(arrangement);

        var countId = AddPhrase(arrangement, "COUNT");
        AddIteration(arrangement, beats, countMs, countId);
        arrangement.PhraseInfos["COUNT"] = new PhraseInfo(0, false);

        var bodyIndex = 0;
        var p0Id = AddPhrase(arrangement, "p0");
        AddIteration(arrangement, beats, p0Ms, p0Id);
        AddSection(arrangement, beats, p0Ms, "riff");
        var lastCutMs = p0Ms;

        var fi = 0;
        var measureCounter = 0;

        for (var i = 0; i < beats.Count; i++)
        {
            var t = beatMs[i];
            if (t < contentStartMs) continue;
            if (t >= endMs) break;

            // Emit any content-driven gap cuts due at/before this beat. These
            // bypass the measure counter and the minimum-separation rule — they
            // follow the music, not the 9-measure grid.
            while (fi < forced.Count && forced[fi].TimeMs <= t)
            {
                var fc = forced[fi];
                fi++;
                if (fc.TimeMs <= lastCutMs || fc.TimeMs >= endMs) continue;
                AddBodyPhrase(arrangement, beats, fc.TimeMs, fc.IsNoGuitar ? "noguitar" : "riff", ref bodyIndex);
                lastCutMs = fc.TimeMs;
                measureCounter = 1;
            }

            // No measure counting / body cuts inside a silent gap.
            if (IsInsideGap(gaps, t)) continue;

            if (beats[i].IsMeasure) measureCounter++;
            if (measureCounter < MeasuresPerPhrase) continue;

            measureCounter = 1;
            var good = FindGoodBeatTime(beatMs, i, occupied);
            if (good >= endMs || IsInsideGap(gaps, good)) continue;
            if (good - lastCutMs < MinimumPhraseSeparationMs) continue; // skip a cut too close to the previous

            AddBodyPhrase(arrangement, beats, good, "riff", ref bodyIndex);
            lastCutMs = good;
        }

        // Flush any gap cuts that fell past the last iterated beat but before END.
        while (fi < forced.Count)
        {
            var fc = forced[fi];
            fi++;
            if (fc.TimeMs <= lastCutMs || fc.TimeMs >= endMs) continue;
            AddBodyPhrase(arrangement, beats, fc.TimeMs, fc.IsNoGuitar ? "noguitar" : "riff", ref bodyIndex);
            lastCutMs = fc.TimeMs;
        }

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

    /// <summary>For each no-guitar gap: a "noguitar" cut at the first beat where
    /// the silence begins, and a "riff" resume cut at the content after it.</summary>
    private static List<ForcedCut> BuildForcedGapCuts(List<(int Start, int End)> gaps, int[] beatMs)
    {
        var cuts = new List<ForcedCut>(gaps.Count * 2);
        foreach (var (gapStart, gapEnd) in gaps)
        {
            var noGuitarMs = FirstBeatAtOrAfter(beatMs, gapStart);
            if (noGuitarMs is null || noGuitarMs.Value >= gapEnd) noGuitarMs = gapStart;
            cuts.Add(new ForcedCut(noGuitarMs.Value, true));
            cuts.Add(new ForcedCut(gapEnd, false));
        }

        cuts.Sort((a, b) => a.TimeMs.CompareTo(b.TimeMs));
        return cuts;
    }

    private static bool IsInsideGap(List<(int Start, int End)> gaps, int timeMs)
    {
        foreach (var (start, end) in gaps)
        {
            if (start <= timeMs && timeMs < end)
                return true;
        }

        return false;
    }

    /// <summary>Walks beats forward from <paramref name="startIndex"/> past any
    /// beat that lands strictly inside an occupied span, returning the ms of the
    /// first "good" beat (or the last beat as an infinite-loop safeguard).</summary>
    private static int FindGoodBeatTime(int[] beatMs, int startIndex, List<(int Start, int End)> occupied)
    {
        var i = startIndex;
        while (i < beatMs.Length - 1 && IsInsideAnySpan(occupied, beatMs[i])) i++;
        return beatMs[i];
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

    private static int? FirstBeatAtOrAfter(int[] beatMs, int timeMs)
    {
        foreach (var t in beatMs)
        {
            if (t >= timeMs)
                return t;
        }

        return null;
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
