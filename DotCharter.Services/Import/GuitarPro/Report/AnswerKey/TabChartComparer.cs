using System.Globalization;
using DotCharter.Core.Linking;

namespace DotCharter.Services.Import.GuitarPro.Report.AnswerKey;

/// <summary>
///     Compares a Guitar Pro import with a finished chart of the same song, measure by measure
///     (<see cref="TabChartAligner" />): onsets, string + fret, pitch, techniques, chords and
///     chord names, FHP, chord fingering, and the linked-measure groups (LinkCandidateDetector) against the chart's
///     real repeats. Deterministic: no audio, no model. The engine of <c>tools/tab-vs-chart</c>.
///     <para>
///         FHP and fingering use <c>tools/chart-eval</c>'s definitions: the anchor that governs a
///         fretted sound is the last one at or before it; a chord counts for fingering only when
///         the chart's template fingers every fretted string, and is exact when every finger
///         agrees.
///     </para>
/// </summary>
public static class TabChartComparer
{
    /// <summary>Two onsets closer than this (in the chart's beats) are one onset.</summary>
    public const double OnsetToleranceBeats = 0.06;

    /// <summary>A missing and an extra onset with the same sounds this close are one shifted onset.</summary>
    public const double ShiftWindowBeats = 0.5;

    /// <summary>The techniques that count; a legato slide is recorded on the sound but never scored.</summary>
    private static readonly SoundTechnique[] Scored =
        [.. Enum.GetValues<SoundTechnique>().Where(t => t is not (SoundTechnique.None or SoundTechnique.LegatoSlide))];

    /// <summary>For each chart arrangement, the tab track of the same instrument that agrees best.</summary>
    public static List<ArrangementComparison> CompareSong(IReadOnlyList<Arrangement> tabParts, IList<BeatMarker> tabBeats,
        IReadOnlyList<(string File, Arrangement Part, IList<BeatMarker> Beats)> charts)
    {
        var results = new List<ArrangementComparison>();
        foreach (var (file, part, beats) in charts)
        {
            var bass = ArrangementInstrument.IsBassPart(part);
            var candidates = Enumerable.Range(0, tabParts.Count).Where(i => ArrangementInstrument.IsBassPart(tabParts[i]) == bass).ToList();
            if (candidates.Count == 0) candidates = [.. Enumerable.Range(0, tabParts.Count)];

            ArrangementComparison? best = null;
            foreach (var i in candidates)
            {
                var c = Compare(tabParts[i], tabBeats, part, beats);
                c.TabTrack = i;
                c.ChartFile = file;
                if (best == null || (c.Counts.ExactF1, c.Counts.PitchF1).CompareTo((best.Counts.ExactF1, best.Counts.PitchF1)) > 0) best = c;
            }

            if (best != null) results.Add(best);
        }

        return results;
    }

    public static ArrangementComparison Compare(Arrangement tab, IList<BeatMarker> tabBeats, Arrangement chart, IList<BeatMarker> chartBeats)
    {
        var tabMeasures = TabChartStrings.OnChartStrings(TabChartSounds.ByMeasure(tab, tabBeats), tab, chart);
        var chartMeasures = TabChartSounds.ByMeasure(chart, chartBeats);
        var alignment = TabChartAligner.Align(tabMeasures, chartMeasures);
        var result = new ArrangementComparison
        {
            Role = RoleOf(chart),
            TabTrackName = tab.Name,
            TabTuning = Tuning(tab),
            ChartTuning = Tuning(chart),
            TabCapo = tab.Capo,
            ChartCapo = chart.Capo,
            TabMeasures = tabMeasures.Count,
            ChartMeasures = chartMeasures.Count,
            AlignedMeasures = alignment.Pairs.Count,
            AlignmentMethod = alignment.Method,
            FirstNoteOffset = alignment.FirstNoteOffset,
            AtFirstNoteOffset = alignment.AtFirstNoteOffset,
            MeanSimilarity = alignment.MeanSimilarity
        };

        var context = new Context(Anchors(tab), Anchors(chart), Templates(tab), Templates(chart));
        var groups = TabChartBarring.Group(alignment.Pairs, tabMeasures, chartMeasures);
        foreach (var group in groups)
        {
            var (tabSide, chartSide) = TabChartBarring.Merge(group, tabMeasures, chartMeasures);
            var counts = new ComparisonCounts();
            var before = result.Differences.Count;
            CompareMeasure(tabSide, chartSide, counts, result.Differences, context);
            result.Counts.Add(counts);
            result.Measures.Add(new MeasureComparison(tabSide.Index, chartSide.Index, TabChartAligner.Similarity(tabSide, chartSide),
                counts.TabSounds, counts.ChartSounds, counts.ExactSounds, counts.TabOnsets, counts.ChartOnsets, counts.MatchedOnsets,
                result.Differences.Count - before));
        }

        Unaligned(tabMeasures, groups.SelectMany(g => g.Tab), true, result);
        Unaligned(chartMeasures, groups.SelectMany(g => g.Chart), false, result);
        CompareLinks(tab, tabBeats, chart, chartBeats, alignment, result.Counts);
        LabelSections(chart, chartMeasures, chartBeats, result);
        result.Measures.Sort((a, b) => (a.TabMeasure < 0 ? int.MaxValue : a.TabMeasure).CompareTo(b.TabMeasure < 0 ? int.MaxValue : b.TabMeasure));
        return result;
    }

    /// <summary>Every chart measure and difference gets the section it plays in.</summary>
    private static void LabelSections(Arrangement chart, List<MeasureSounds> chartMeasures, IList<BeatMarker> chartBeats, ArrangementComparison result)
    {
        result.Sections = TabChartSections.Resolve(chart);
        if (result.Sections.Count == 0) return;

        var grid = MeasureGrid.Build(chartBeats);
        var names = new string?[chartMeasures.Count];
        for (var m = 0; m < chartMeasures.Count && m < grid.Count; m++)
        {
            var at = chartMeasures[m].Sounds.Count > 0 ? chartMeasures[m].Sounds[0].Position : grid[m].Start;
            names[m] = result.Sections.LastOrDefault(s => s.Position <= at)?.Name;
        }

        string? Of(int m) => m >= 0 && m < names.Length ? names[m] : null;
        result.Measures = [.. result.Measures.Select(x => x with { Section = Of(x.ChartMeasure) })];
        result.Differences = [.. result.Differences.Select(d => d with { Section = Of(d.ChartMeasure) })];
    }

    /// <summary>Lead, Rhythm, Combo or Bass.</summary>
    public static string RoleOf(Arrangement arrangement) =>
        ArrangementInstrument.IsBassPart(arrangement) ? "Bass" : arrangement.ArrangementType.ToString();

    private sealed record Context(List<FretAnchor> TabAnchors, List<FretAnchor> ChartAnchors,
        Dictionary<int, ChordTemplate> TabTemplates, Dictionary<int, ChordTemplate> ChartTemplates);

    private sealed record Onset(double Beat, List<StruckSound> Sounds);

    /// <summary>
    ///     One aligned span: a single bar pair, or the bars <see cref="TabChartBarring" /> merged onto
    ///     one grid. Both sides count beats from the span's start in the same unit.
    /// </summary>
    private static void CompareMeasure(MeasureSounds tab, MeasureSounds chart, ComparisonCounts counts,
        List<Difference> diffs, Context context)
    {
        var tabOnsets = Onsets(tab.Sounds, s => s.Beat);
        var chartOnsets = Onsets(chart.Sounds, s => s.Beat);
        counts.TabOnsets += tabOnsets.Count;
        counts.ChartOnsets += chartOnsets.Count;
        counts.TabSounds += tab.Sounds.Count;
        counts.ChartSounds += chart.Sounds.Count;

        var missing = new List<Onset>();
        var extra = new List<Onset>();
        var (i, j) = (0, 0);
        while (i < tabOnsets.Count || j < chartOnsets.Count)
        {
            if (i < tabOnsets.Count && j < chartOnsets.Count && Math.Abs(tabOnsets[i].Beat - chartOnsets[j].Beat) <= OnsetToleranceBeats)
            {
                counts.MatchedOnsets++;
                CompareOnset(tabOnsets[i], chartOnsets[j], tabOnsets[i].Sounds[0].Measure, chartOnsets[j].Sounds[0].Measure, counts, diffs, context);
                i++;
                j++;
            }
            else if (j >= chartOnsets.Count || (i < tabOnsets.Count && tabOnsets[i].Beat < chartOnsets[j].Beat))
            {
                extra.Add(tabOnsets[i++]);
            }
            else
            {
                missing.Add(chartOnsets[j++]);
            }
        }

        // A missing and an extra onset with the same sounds, close together: one shifted onset.
        foreach (var m in missing.ToList())
        {
            var shifted = extra.FirstOrDefault(e => Math.Abs(e.Beat - m.Beat) <= ShiftWindowBeats && Spell(e.Sounds) == Spell(m.Sounds));
            if (shifted == null) continue;

            missing.Remove(m);
            extra.Remove(shifted);
            diffs.Add(new Difference(shifted.Sounds[0].Measure, m.Sounds[0].Measure, m.Beat, DifferenceClasses.OnsetShift,
                Beat(shifted.Beat) + " " + Spell(shifted.Sounds), Beat(m.Beat) + " " + Spell(m.Sounds)));
        }

        foreach (var m in missing)
            diffs.Add(new Difference(tab.Index, m.Sounds[0].Measure, m.Beat, DifferenceClasses.MissingOnset, null, Spell(m.Sounds)));
        foreach (var e in extra)
            diffs.Add(new Difference(e.Sounds[0].Measure, chart.Index, e.Beat, DifferenceClasses.ExtraOnset, Spell(e.Sounds), null));
    }

    private static void CompareOnset(Onset tab, Onset chart, int tabMeasure, int chartMeasure, ComparisonCounts counts,
        List<Difference> diffs, Context context)
    {
        void Diff(string cls, string? t, string? c) => diffs.Add(new Difference(tabMeasure, chartMeasure, chart.Beat, cls, t, c));

        var tabLeft = tab.Sounds.ToList();
        var chartLeft = chart.Sounds.ToList();
        var matched = new List<(StruckSound Tab, StruckSound Chart)>();
        foreach (var c in chart.Sounds)
        {
            var t = tabLeft.FirstOrDefault(x => x.String == c.String && x.Fret == c.Fret);
            if (t == null) continue;

            tabLeft.Remove(t);
            chartLeft.Remove(c);
            matched.Add((t, c));
        }

        counts.ExactSounds += matched.Count;
        counts.PitchSounds += matched.Count;
        foreach (var (t, c) in matched)
            Fhp(t, c, counts, Diff, context);

        foreach (var c in chartLeft.ToList())
        {
            var t = tabLeft.FirstOrDefault(x => x.Midi != null && x.Midi == c.Midi);
            if (t == null) continue;

            tabLeft.Remove(t);
            chartLeft.Remove(c);
            counts.PitchSounds++;
            matched.Add((t, c));
            Diff(DifferenceClasses.OtherString, Spell(t), Spell(c));
        }

        // Whatever is left pairs up string-order as wrong notes; the rest is a chord-size difference.
        tabLeft.Sort((a, b) => a.String.CompareTo(b.String));
        chartLeft.Sort((a, b) => a.String.CompareTo(b.String));
        var wrong = Math.Min(tabLeft.Count, chartLeft.Count);
        for (var k = 0; k < wrong; k++)
        {
            var (t, c) = (tabLeft[k], chartLeft[k]);
            var octave = t.Midi != null && c.Midi != null && t.Midi != c.Midi && (t.Midi.Value - c.Midi.Value) % 12 == 0;
            Diff(octave ? DifferenceClasses.Octave : DifferenceClasses.WrongPitch, Spell(t), Spell(c));
        }

        foreach (var c in chartLeft.Skip(wrong))
            Diff(DifferenceClasses.ChordNoteMissing, null, Spell(c));
        foreach (var t in tabLeft.Skip(wrong))
            Diff(DifferenceClasses.ChordNoteExtra, Spell(t), null);

        foreach (var (t, c) in matched)
        {
            foreach (var technique in Scored)
            {
                var (inTab, inChart) = (t.Techniques.HasFlag(technique), c.Techniques.HasFlag(technique));
                if (!inTab && !inChart) continue;

                var tc = counts.Technique(technique);
                if (inTab && inChart) tc.Both++;
                else if (inTab) tc.TabOnly++;
                else tc.ChartOnly++;
                if (inTab != inChart) Diff(DifferenceClasses.Technique(technique, missing: inChart), Spell(t), Spell(c));
            }
        }

        if (tab.Sounds.Count >= 2 && chart.Sounds.Count >= 2) Chords(tab, chart, counts, Diff, context);
    }

    private static void Chords(Onset tab, Onset chart, ComparisonCounts counts, Action<string, string?, string?> diff, Context context)
    {
        counts.ChordOnsets++;
        var sameShape = Spell(tab.Sounds) == Spell(chart.Sounds);
        if (sameShape) counts.ChordShapeSame++;
        else diff(DifferenceClasses.ChordShape, Spell(tab.Sounds), Spell(chart.Sounds));

        var tabTemplate = Template(tab.Sounds, context.TabTemplates);
        var chartTemplate = Template(chart.Sounds, context.ChartTemplates);
        var tabName = tabTemplate?.Name.Trim() ?? "";
        var chartName = chartTemplate?.Name.Trim() ?? "";
        if (tabName.Length > 0 && chartName.Length > 0)
        {
            counts.ChordNamedBoth++;
            if (string.Equals(tabName, chartName, StringComparison.OrdinalIgnoreCase)) counts.ChordNameSame++;
            else diff(DifferenceClasses.ChordName, tabName, chartName);
        }

        if (!sameShape || chartTemplate == null) return;

        var fretted = chart.Sounds.Where(s => s.Fret > 0).Select(s => s.String).ToList();
        if (fretted.Count == 0 || fretted.Any(s => chartTemplate.GetFinger(s) < 0)) return;

        counts.FingeredChords++;
        var same = fretted.Count(s => tabTemplate != null && tabTemplate.GetFinger(TabString(tab, s)) == chartTemplate.GetFinger(s));
        counts.FingeredStrings += fretted.Count;
        counts.FingeredStringsSame += same;
        if (same == fretted.Count) counts.FingeringSame++;
        else diff(DifferenceClasses.Fingering, Fingers(tabTemplate, fretted.Select(s => TabString(tab, s)).ToList()), Fingers(chartTemplate, fretted));
    }

    private static void Fhp(StruckSound tab, StruckSound chart, ComparisonCounts counts, Action<string, string?, string?> diff, Context context)
    {
        if (chart.Fret <= 0) return;

        var t = Governing(context.TabAnchors, tab.Position);
        var c = Governing(context.ChartAnchors, chart.Position);
        if (t == null || c == null) return;

        counts.FhpSounds++;
        if (t == c) counts.FhpSame++;
        else diff(DifferenceClasses.Fhp, t.Value.ToString(CultureInfo.InvariantCulture), c.Value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    ///     Linked measures: the detector's groups on the import against the chart's real repeats (equal
    ///     sounds-only measure content), through the alignment.
    /// </summary>
    private static void CompareLinks(Arrangement tab, IList<BeatMarker> tabBeats, Arrangement chart, IList<BeatMarker> chartBeats,
        MeasureAlignment alignment, ComparisonCounts counts)
    {
        if (alignment.Pairs.Count == 0) return;

        var tabIndex = MeasureContentIndex.ForHardestLevel(tab, tabBeats, MeasureContentOptions.Detection);
        var chartIndex = MeasureContentIndex.ForHardestLevel(chart, chartBeats, MeasureContentOptions.SoundsOnly);
        var detected = LinkCandidateDetector.Detect([tabIndex]);
        var toChart = alignment.Pairs.ToDictionary(p => p.Tab, p => p.Chart);
        var toTab = alignment.Pairs.ToDictionary(p => p.Chart, p => p.Tab);
        var chartKeys = new Dictionary<int, MeasureContentKey>();
        foreach (var c in toTab.Keys.Where(c => c < chartIndex.Measures.Count))
        {
            var key = chartIndex.KeyFor(c);
            if (key.SoundCount > 0) chartKeys[c] = key;
        }

        var slot = new Dictionary<int, (string Group, int Offset)>();
        foreach (var group in detected.Groups)
        {
            foreach (var placement in group.Placements)
            {
                for (var k = 0; k < group.MeasureCount; k++)
                    slot.TryAdd(placement.FirstMeasure + k, (group.Letter, k));
            }

            for (var a = 0; a < group.Placements.Count; a++)
            {
                for (var b = a + 1; b < group.Placements.Count; b++)
                {
                    for (var k = 0; k < group.MeasureCount; k++)
                    {
                        var bothAligned = toChart.TryGetValue(group.Placements[a].FirstMeasure + k, out var ca) &
                                          toChart.TryGetValue(group.Placements[b].FirstMeasure + k, out var cb);
                        if (!bothAligned) continue;

                        counts.LinkPairs++;
                        if (chartKeys.TryGetValue(ca, out var ka) && chartKeys.TryGetValue(cb, out var kb) && ka.Equals(kb)) counts.LinkPairsChartEqual++;
                    }
                }
            }
        }

        foreach (var same in chartKeys.GroupBy(kv => kv.Value).Where(g => g.Count() > 1))
        {
            var measures = same.Select(kv => kv.Key).Order().ToList();
            for (var a = 0; a < measures.Count; a++)
            {
                for (var b = a + 1; b < measures.Count; b++)
                {
                    counts.ChartRepeatPairs++;
                    var linked = slot.TryGetValue(toTab[measures[a]], out var sa) && slot.TryGetValue(toTab[measures[b]], out var sb) && sa == sb;
                    if (linked) counts.ChartRepeatPairsLinked++;
                }
            }
        }
    }

    private static void Unaligned(List<MeasureSounds> measures, IEnumerable<int> aligned, bool tab, ArrangementComparison result)
    {
        var set = aligned.ToHashSet();
        foreach (var m in measures.Where(m => !set.Contains(m.Index) && m.Sounds.Count > 0))
        {
            var onsets = Onsets(m.Sounds, s => s.Beat);
            if (tab)
            {
                result.Counts.TabSounds += m.Sounds.Count;
                result.Counts.TabOnsets += onsets.Count;
            }
            else
            {
                result.Counts.ChartSounds += m.Sounds.Count;
                result.Counts.ChartOnsets += onsets.Count;
            }

            result.Differences.Add(new Difference(tab ? m.Index : -1, tab ? -1 : m.Index, 0,
                tab ? DifferenceClasses.UnalignedTabMeasure : DifferenceClasses.UnalignedChartMeasure,
                tab ? $"{m.Sounds.Count} sounds" : null, tab ? null : $"{m.Sounds.Count} sounds"));
            result.Measures.Add(new MeasureComparison(tab ? m.Index : -1, tab ? -1 : m.Index, 0,
                tab ? m.Sounds.Count : 0, tab ? 0 : m.Sounds.Count, 0, tab ? onsets.Count : 0, tab ? 0 : onsets.Count, 0, 1));
        }
    }

    private static List<Onset> Onsets(IReadOnlyList<StruckSound> sounds, Func<StruckSound, double> beat)
    {
        var onsets = new List<Onset>();
        foreach (var s in sounds.OrderBy(beat).ThenBy(s => s.String))
        {
            var b = beat(s);
            if (onsets.Count > 0 && Math.Abs(onsets[^1].Beat - b) < 1e-6) onsets[^1].Sounds.Add(s);
            else onsets.Add(new Onset(b, [s]));
        }

        return onsets;
    }

    private static int? Governing(List<FretAnchor> anchors, FractionalPosition position)
    {
        int? fret = null;
        foreach (var a in anchors)
        {
            if (a.Position > position) break;
            fret = a.Fret;
        }

        return fret;
    }

    private static List<FretAnchor> Anchors(Arrangement arrangement) =>
        [.. (arrangement.Levels.MaxBy(l => l.DifficultyIndex)?.Anchors ?? []).OrderBy(a => a.Position)];

    private static Dictionary<int, ChordTemplate> Templates(Arrangement arrangement)
    {
        var map = new Dictionary<int, ChordTemplate>();
        foreach (var t in arrangement.ChordTemplates)
            map.TryAdd(t.Id, t);
        return map;
    }

    private static ChordTemplate? Template(List<StruckSound> sounds, Dictionary<int, ChordTemplate> templates)
    {
        var chord = sounds.Select(s => s.Chord).FirstOrDefault(c => c != null);
        return chord != null && templates.TryGetValue(chord.TemplateId, out var t) ? t : null;
    }

    /// <summary>The tab's own string for the sound the tab plays on chart string <paramref name="chartString" />.</summary>
    private static int TabString(Onset tab, int chartString) => tab.Sounds.FirstOrDefault(x => x.String == chartString)?.TabString ?? chartString;

    private static string Fingers(ChordTemplate? template, List<int> strings) =>
        string.Join(" ", strings.Select(s => $"{s}:{(template == null ? "-" : template.GetFinger(s).ToString(CultureInfo.InvariantCulture))}"));

    private static string Tuning(Arrangement a) =>
        string.Join(" ", a.TuningPitches.Take(Math.Max(1, a.StringCount)).Select(t => t.Step.ToString(CultureInfo.InvariantCulture)));

    private static string Spell(StruckSound s) => $"{s.String}:{s.Fret}";

    private static string Spell(List<StruckSound> sounds) => string.Join("+", sounds.OrderBy(s => s.String).ThenBy(s => s.Fret).Select(Spell));

    private static string Beat(double beat) => "@" + beat.ToString("0.###", CultureInfo.InvariantCulture);
}
