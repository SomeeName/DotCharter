using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.UI.ViewModels.PlayMode;

/// <summary>One bar of the results-screen timing histogram.</summary>
/// <param name="Height">Pixel height, already normalized against the tallest bin.</param>
/// <param name="IsCentre">True for the two bins straddling 0 ms — drawn in the accent
/// colour so "dead on" is visually anchored.</param>
/// <param name="IsEarly">Left of centre. Early and late are tinted differently so the
/// shape reads as rushing/dragging without consulting the axis.</param>
public sealed record TimingBarRow(double Height, bool IsCentre, bool IsEarly)
{
    /// <summary>Centre bins ("dead on") are drawn solid; the tails are dimmed, so the
    /// shape of the distribution reads at a glance without an axis lookup. Bound
    /// directly rather than through a converter — one less moving part in the view.</summary>
    public double Opacity => IsCentre ? 1.0 : 0.55;
}

/// <summary>One section's timing bias on the results screen.</summary>
public sealed record TimingSectionRow(string Name, string BiasLabel, string HitsLabel, bool LeansEarly);

/// <summary>
///     Turns a <see cref="LiveTimingReport" /> histogram into bar geometry. Pure, so
///     the awkward cases (an empty run, a single bin holding every hit) are settled
///     here rather than in a view.
/// </summary>
public static class TimingHistogramMath
{
    /// <summary>Tallest bar, in px.</summary>
    public const double MaxBarHeight = 90.0;

    /// <summary>Shortest bar for a NON-EMPTY bin, in px. A bin with one hit next to a
    /// bin with 400 would otherwise round to nothing and read as "no hits here",
    /// which is exactly the tail you want to see.</summary>
    public const double MinVisibleBarHeight = 3.0;

    /// <summary>
    ///     Bar heights normalized against the tallest bin. An all-zero histogram
    ///     (no timed hits — e.g. every hit was CQT pull-verified and therefore
    ///     untimed) yields all-zero heights rather than dividing by zero.
    /// </summary>
    public static IReadOnlyList<double> BarHeights(IReadOnlyList<int>? histogram)
    {
        if (histogram is null || histogram.Count == 0) return [];

        var peak = 0;
        foreach (var bin in histogram)
        {
            if (bin > peak)
                peak = bin;
        }

        if (peak <= 0) return new double[histogram.Count];

        var heights = new double[histogram.Count];
        for (var i = 0; i < histogram.Count; i++)
        {
            if (histogram[i] <= 0) continue;
            var scaled = histogram[i] / (double)peak * MaxBarHeight;
            heights[i] = Math.Max(scaled, MinVisibleBarHeight);
        }

        return heights;
    }

    /// <summary>
    ///     Bin <paramref name="index" /> straddles 0 ms. Bins are
    ///     <see cref="LiveTimingReport.HistogramBinWidthMs" /> wide spanning
    ///     ±<see cref="LiveTimingReport.HistogramRangeMs" />, so with an even bin count
    ///     the zero line falls BETWEEN the two middle bins — both are centre bins.
    /// </summary>
    public static bool IsCentreBin(int index, int binCount)
    {
        if (binCount <= 0) return false;
        var mid = binCount / 2;
        return index == mid || index == mid - 1;
    }

    /// <summary>Bin <paramref name="index" /> lies entirely before 0 ms (early/rushing).</summary>
    public static bool IsEarlyBin(int index, int binCount) => index < binCount / 2;
}
