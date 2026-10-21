namespace DotCharter.Services.Import.GuitarPro.Report.AnswerKey;

/// <summary>One span of bars of the tab and of the chart that cover the same stretch of music.</summary>
/// <param name="Tab">The tab bars, in order.</param>
/// <param name="Chart">The chart bars, in order.</param>
/// <param name="Pickup">The tab's first bar is a partial bar that ends where the chart's first bar does.</param>
public sealed record BarGroup(IReadOnlyList<int> Tab, IReadOnlyList<int> Chart, bool Pickup);

/// <summary>
///     Pairs the tab's bars with the chart's by TIME, not by bar index. Two bars of the same length
///     in the same unit compare beat for beat. Where the lengths differ, because the two barred the
///     passage differently, bars are taken on from both sides until the music they cover is equally
///     long, and the sounds of that span are compared at their offset from its start. Stretching one
///     bar onto the other would put every onset on a grid it was never on.
/// </summary>
public static class TabChartBarring
{
    /// <summary>The most bars a span may take on from either side before it gives up.</summary>
    public const int MaxSpanBars = 16;

    private const double Epsilon = 1e-9;

    /// <summary>The aligned pairs as spans, each bar in at most one.</summary>
    public static List<BarGroup> Group(IReadOnlyList<(int Tab, int Chart)> pairs, IReadOnlyList<MeasureSounds> tab, IReadOnlyList<MeasureSounds> chart)
    {
        var groups = new List<BarGroup>();
        var usedTab = new HashSet<int>();
        var usedChart = new HashSet<int>();
        for (var k = 0; k < pairs.Count; k++)
        {
            var (t, c) = pairs[k];
            if (usedTab.Contains(t) || usedChart.Contains(c)) continue;

            var tabs = new List<int> { t };
            var charts = new List<int> { c };
            var pickup = false;
            if (!SameBar(tab[t], chart[c]))
            {
                if (!Extend(tabs, charts, tab, chart, usedTab, usedChart))
                {
                    tabs = [t];
                    charts = [c];
                    pickup = k == 0;
                }
            }

            usedTab.UnionWith(tabs);
            usedChart.UnionWith(charts);
            groups.Add(new BarGroup(tabs, charts, pickup));
        }

        return groups;
    }

    /// <summary>
    ///     The span's two sides, each on one grid. A plain pair is the bars themselves. Anything else is in quarter
    ///     notes from the span's start; a pickup's tab side is moved to end where the chart bar does.
    /// </summary>
    public static (MeasureSounds Tab, MeasureSounds Chart) Merge(BarGroup group, IReadOnlyList<MeasureSounds> tab, IReadOnlyList<MeasureSounds> chart)
    {
        if (group.Tab.Count == 1 && group.Chart.Count == 1 && SameBar(tab[group.Tab[0]], chart[group.Chart[0]]))
            return (tab[group.Tab[0]], chart[group.Chart[0]]);

        var tabSide = Quarters(group.Tab.Select(i => tab[i]).ToList(), 0);
        var chartSide = Quarters(group.Chart.Select(i => chart[i]).ToList(), 0);
        return group.Pickup ? (Quarters(group.Tab.Select(i => tab[i]).ToList(), chartSide.BeatCount - tabSide.BeatCount), chartSide) : (tabSide, chartSide);
    }

    private static bool SameBar(MeasureSounds a, MeasureSounds b) => a.BeatCount == b.BeatCount && a.Denominator == b.Denominator;

    /// <summary>How long the bar plays, in whole notes.</summary>
    private static double Length(MeasureSounds bar) => (double)bar.BeatCount / Math.Max(1, bar.Denominator);

    /// <summary>Takes on the next bar of the shorter side until both sides are equally long.</summary>
    private static bool Extend(List<int> tabs, List<int> charts, IReadOnlyList<MeasureSounds> tab, IReadOnlyList<MeasureSounds> chart,
        HashSet<int> usedTab, HashSet<int> usedChart)
    {
        var (lengthTab, lengthChart) = (Length(tab[tabs[0]]), Length(chart[charts[0]]));
        while (Math.Abs(lengthTab - lengthChart) > Epsilon && tabs.Count + charts.Count < MaxSpanBars)
        {
            if (lengthTab < lengthChart)
            {
                var next = tabs[^1] + 1;
                if (next >= tab.Count || usedTab.Contains(next)) return false;

                tabs.Add(next);
                lengthTab += Length(tab[next]);
            }
            else
            {
                var next = charts[^1] + 1;
                if (next >= chart.Count || usedChart.Contains(next)) return false;

                charts.Add(next);
                lengthChart += Length(chart[next]);
            }
        }

        return Math.Abs(lengthTab - lengthChart) <= Epsilon;
    }

    /// <summary>The bars end to end, every beat in quarter notes from the first bar's start plus <paramref name="shift" />.</summary>
    private static MeasureSounds Quarters(IReadOnlyList<MeasureSounds> bars, double shift)
    {
        var sounds = new List<StruckSound>();
        var offset = shift;
        foreach (var bar in bars)
        {
            var quarter = 4.0 / Math.Max(1, bar.Denominator);
            sounds.AddRange(bar.Sounds.Select(s => s with { Beat = offset + s.Beat * quarter }));
            offset += bar.BeatCount * quarter;
        }

        return new MeasureSounds(bars[0].Index, (int)Math.Round(offset - shift), sounds);
    }
}
