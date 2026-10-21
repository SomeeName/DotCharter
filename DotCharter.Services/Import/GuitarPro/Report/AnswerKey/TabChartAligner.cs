namespace DotCharter.Services.Import.GuitarPro.Report.AnswerKey;

/// <summary>
///     Which chart measure each tab measure plays. <see cref="Pairs" /> is monotonic; a measure in
///     neither list is unaligned (a count-in, a cut or added section). <see cref="FirstNoteOffset" />
///     is the chart's first sounding measure minus the tab's; <see cref="Method" /> says which
///     alignment was used: <c>dp</c> (measure content) or <c>first-notes</c> (the offset alone).
/// </summary>
public sealed record MeasureAlignment(
    IReadOnlyList<(int Tab, int Chart)> Pairs,
    int FirstNoteOffset,
    string Method,
    double MeanSimilarity)
{
    /// <summary>The share of paired measures that sit at <see cref="FirstNoteOffset" />.</summary>
    public double AtFirstNoteOffset => Pairs.Count == 0 ? 0 : (double)Pairs.Count(p => p.Chart - p.Tab == FirstNoteOffset) / Pairs.Count;
}

/// <summary>
///     Aligns a tab's (repeat-unfolded) measures with a finished chart's measures. The tab has no
///     audio sync, so this goes by content: a semi-global dynamic programme over measure pairs
///     (free leading and trailing gaps absorb a count-in, a pickup or an intro the chart cut),
///     scored by <see cref="Similarity" />. When that finds nothing better than noise, the offset
///     between the first sounding measures is used alone.
/// </summary>
public static class TabChartAligner
{
    /// <summary>Cost of skipping one measure inside the song (a cut or an added bar).</summary>
    public const double GapCost = 0.35;

    /// <summary>Two silent measures agree a little: a rest is content too, but weak evidence.</summary>
    public const double BothSilent = 0.25;

    /// <summary>Below this mean similarity over sounding pairs the DP is noise.</summary>
    public const double MinimumMeanSimilarity = 0.15;

    /// <summary>Slots per measure for the content tokens (48 = 16th triplets in 4/4).</summary>
    private const int Slots = 48;

    /// <summary>
    ///     0..1. Rhythm (onset slots, ±1 slot) weighs 0.4; the better of pitch and string+fret
    ///     (same slot ±1) weighs 0.6, so a part charted on other strings, or in another tuning
    ///     with the same frets, still aligns.
    /// </summary>
    public static double Similarity(MeasureSounds a, MeasureSounds b)
    {
        if (a.Sounds.Count == 0 && b.Sounds.Count == 0) return BothSilent;
        if (a.Sounds.Count == 0 || b.Sounds.Count == 0) return 0;

        var rhythm = Dice(Onsets(a), Onsets(b));
        var pitch = Dice(Tokens(a, s => s.Midi ?? -1), Tokens(b, s => s.Midi ?? -1));
        var fret = Dice(Tokens(a, s => s.String * 100 + s.Fret), Tokens(b, s => s.String * 100 + s.Fret));
        return 0.4 * rhythm + 0.6 * Math.Max(pitch, fret);
    }

    public static MeasureAlignment Align(IReadOnlyList<MeasureSounds> tab, IReadOnlyList<MeasureSounds> chart)
    {
        var firstTab = FirstSounding(tab);
        var firstChart = FirstSounding(chart);
        var offset = firstTab >= 0 && firstChart >= 0 ? firstChart - firstTab : 0;
        if (tab.Count == 0 || chart.Count == 0) return new MeasureAlignment([], offset, "none", 0);

        var (pairs, mean) = Programme(tab, chart);
        if (mean >= MinimumMeanSimilarity) return new MeasureAlignment(pairs, offset, "dp", mean);

        var shifted = Enumerable.Range(0, tab.Count)
            .Select(t => (Tab: t, Chart: t + offset))
            .Where(p => p.Chart >= 0 && p.Chart < chart.Count)
            .ToList();
        return new MeasureAlignment(shifted, offset, "first-notes", MeanSounding(shifted, tab, chart));
    }

    private static (List<(int Tab, int Chart)> Pairs, double Mean) Programme(IReadOnlyList<MeasureSounds> tab, IReadOnlyList<MeasureSounds> chart)
    {
        var n = tab.Count;
        var m = chart.Count;
        var score = new double[n + 1, m + 1]; // row and column 0 stay 0: leading gaps are free
        var move = new byte[n + 1, m + 1]; // 0 = start, 1 = diagonal, 2 = skip tab, 3 = skip chart
        var sim = new double[n, m];
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < m; j++)
                sim[i, j] = Similarity(tab[i], chart[j]);
        }

        for (var i = 1; i <= n; i++)
        {
            for (var j = 1; j <= m; j++)
            {
                var diagonal = score[i - 1, j - 1] + sim[i - 1, j - 1];
                var skipTab = score[i - 1, j] - GapCost;
                var skipChart = score[i, j - 1] - GapCost;
                (score[i, j], move[i, j]) = diagonal >= skipTab && diagonal >= skipChart
                    ? (diagonal, (byte)1)
                    : skipTab >= skipChart ? (skipTab, (byte)2) : (skipChart, (byte)3);
            }
        }

        // Trailing gaps are free: end anywhere on the last row or column.
        var (bi, bj) = (n, m);
        for (var j = 0; j <= m; j++)
        {
            if (score[n, j] > score[bi, bj]) (bi, bj) = (n, j);
        }

        for (var i = 0; i <= n; i++)
        {
            if (score[i, m] > score[bi, bj]) (bi, bj) = (i, m);
        }

        var pairs = new List<(int Tab, int Chart)>();
        while (bi > 0 && bj > 0)
        {
            switch (move[bi, bj])
            {
                case 1:
                    pairs.Add((bi - 1, bj - 1));
                    bi--;
                    bj--;
                    break;
                case 2:
                    bi--;
                    break;
                default:
                    bj--;
                    break;
            }
        }

        pairs.Reverse();
        return (pairs, MeanSounding(pairs, tab, chart));
    }

    private static double MeanSounding(List<(int Tab, int Chart)> pairs, IReadOnlyList<MeasureSounds> tab, IReadOnlyList<MeasureSounds> chart)
    {
        var sounding = pairs.Where(p => tab[p.Tab].Sounds.Count > 0 || chart[p.Chart].Sounds.Count > 0).ToList();
        return sounding.Count == 0 ? 0 : sounding.Average(p => Similarity(tab[p.Tab], chart[p.Chart]));
    }

    private static int FirstSounding(IReadOnlyList<MeasureSounds> measures)
    {
        for (var i = 0; i < measures.Count; i++)
        {
            if (measures[i].Sounds.Count > 0) return i;
        }

        return -1;
    }

    private static int Slot(MeasureSounds m, StruckSound s) => (int)Math.Round(s.Beat / Math.Max(1, m.BeatCount) * Slots);

    private static List<(int Slot, int Value)> Onsets(MeasureSounds m) =>
        [.. m.Sounds.Select(s => Slot(m, s)).Distinct().Select(s => (s, 0))];

    private static List<(int Slot, int Value)> Tokens(MeasureSounds m, Func<StruckSound, int> value) =>
        [.. m.Sounds.Select(s => (Slot(m, s), value(s)))];

    /// <summary>Dice over tokens that match on value with slots at most one apart.</summary>
    private static double Dice(List<(int Slot, int Value)> a, List<(int Slot, int Value)> b)
    {
        if (a.Count + b.Count == 0) return 0;

        var used = new bool[b.Count];
        var shared = 0;
        foreach (var x in a)
        {
            var hit = -1;
            for (var k = 0; k < b.Count; k++)
            {
                if (used[k] || b[k].Value != x.Value || Math.Abs(b[k].Slot - x.Slot) > 1) continue;
                if (hit < 0 || Math.Abs(b[k].Slot - x.Slot) < Math.Abs(b[hit].Slot - x.Slot)) hit = k;
            }

            if (hit < 0) continue;
            used[hit] = true;
            shared++;
        }

        return 2.0 * shared / (a.Count + b.Count);
    }
}
