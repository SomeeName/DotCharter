using System.Globalization;

namespace DotCharter.UI.Controls.Layers.Helpers;

/// <summary>
///     Pure decision logic for the timeline's TIME ruler (<c>BackgroundLayer.DrawRuler</c>):
///     how far apart its labels are and how each one reads. The step is the smallest
///     "nice" interval (<see cref="NiceStepsMs" />) whose on-screen width holds the widest
///     label plus a gap, so the labels never run into each other at any zoom; they sit on
///     multiples of the step, so they do not jump while the view scrolls; and they use the
///     shortest form, <c>m:ss</c> under an hour. Unit-tested without a GPU
///     (<c>UI/Rendering/TimeRulerPolicyTests</c>).
/// </summary>
public static class TimeRulerPolicy
{
    /// <summary>The steps a ruler may use, finest first: 1, 2, 5, 10, 15, 30 s, then
    /// 1, 2, 5, 10, 15, 30 min and an hour.</summary>
    public static readonly double[] NiceStepsMs =
    [
        1_000, 2_000, 5_000, 10_000, 15_000, 30_000,
        60_000, 120_000, 300_000, 600_000, 900_000, 1_800_000, 3_600_000
    ];

    /// <summary>Clear space between one label's end and the next label's start.</summary>
    public const double MinLabelGapPx = 10.0;

    /// <summary>A label starts this far right of its tick.</summary>
    public const double LabelOffsetPx = 2.0;

    /// <summary>
    ///     The step for a ruler drawn at <paramref name="pixelsPerMs" /> whose widest label is
    ///     <paramref name="labelWidthPx" /> wide: the smallest nice step that holds a label
    ///     and its gap. Past the coarsest, the coarsest.
    /// </summary>
    public static double StepMs(double pixelsPerMs, double labelWidthPx)
    {
        if (!(pixelsPerMs > 0) || double.IsInfinity(pixelsPerMs)) return NiceStepsMs[^1];
        foreach (var step in NiceStepsMs)
        {
            if (step * pixelsPerMs >= labelWidthPx + MinLabelGapPx) return step;
        }

        return NiceStepsMs[^1];
    }

    /// <summary>The first tick at or after <paramref name="viewStartMs" /> (never before the
    /// song's start): a multiple of <paramref name="stepMs" />.</summary>
    public static double FirstTickMs(double viewStartMs, double stepMs) =>
        Math.Max(0, Math.Ceiling(viewStartMs / stepMs) * stepMs);

    /// <summary>The label for a tick: <c>m:ss</c> under an hour, <c>h:mm:ss</c> from one.</summary>
    public static string Format(double ms)
    {
        var total = (long)Math.Round(Math.Max(0, ms) / 1000.0);
        var hours = total / 3600;
        var minutes = total / 60 % 60;
        var seconds = total % 60;
        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:00}:{seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes}:{seconds:00}");
    }
}
