using DotCharter.UI.Controls.Layers.Helpers;

namespace DotCharter.Tests.UI.Rendering;

/// <summary>
///     The TIME ruler's labels (<see cref="TimeRulerPolicy" />): never overlapping at any zoom,
///     stepped by a nice interval, on multiples of that step, in the shortest form. Zoomed out
///     the ruler used to print a label every second at 26 px apart — an unreadable
///     "00:00:00:00…" strip.
/// </summary>
public sealed class TimeRulerPolicyTests
{
    /// <summary>Text-free widths shaped like the batcher's: 7 px a character.</summary>
    private static double Width(string text) => 7.0 * text.Length;

    private static IEnumerable<double> Zooms()
    {
        for (var z = 0.0002; z < 4; z *= 1.37) yield return z;
    }

    [Fact]
    public void NoTwoLabels_Overlap_AtAnyZoom()
    {
        foreach (var zoom in Zooms())
        {
            foreach (var viewStart in new[] { 0.0, 37_250, 3_540_000 })
            {
                var viewEnd = viewStart + (1800 / zoom);
                var widest = Width(TimeRulerPolicy.Format(viewEnd));
                var step = TimeRulerPolicy.StepMs(zoom, widest + TimeRulerPolicy.LabelOffsetPx);
                var previousEnd = double.NegativeInfinity;
                for (var t = TimeRulerPolicy.FirstTickMs(viewStart, step); t <= viewEnd; t += step)
                {
                    var left = ((t - viewStart) * zoom) + TimeRulerPolicy.LabelOffsetPx;
                    Assert.True(previousEnd + TimeRulerPolicy.MinLabelGapPx <= left + 1e-6 || step == TimeRulerPolicy.NiceStepsMs[^1],
                        $"at {zoom:0.#####} px/ms the label at {t} ms runs into the one before it (step {step} ms)");
                    previousEnd = left + Width(TimeRulerPolicy.Format(t));
                }
            }
        }
    }

    [Fact]
    public void TheStep_IsAlwaysFromTheNiceList_AndTheSmallestThatFits()
    {
        foreach (var zoom in Zooms())
        {
            var step = TimeRulerPolicy.StepMs(zoom, 40);
            Assert.Contains(step, TimeRulerPolicy.NiceStepsMs);
            var index = Array.IndexOf(TimeRulerPolicy.NiceStepsMs, step);
            if (index > 0)
            {
                Assert.True(TimeRulerPolicy.NiceStepsMs[index - 1] * zoom < 40 + TimeRulerPolicy.MinLabelGapPx,
                    $"at {zoom} px/ms a finer step than {step} ms would have fit");
            }
        }

        // The zoomed-out view of the before images: 26 px a second needs a 2 s step.
        Assert.Equal(2_000, TimeRulerPolicy.StepMs(0.026, 30));
        Assert.Equal(1_000, TimeRulerPolicy.StepMs(0.2, 30));
        Assert.Equal(10_000, TimeRulerPolicy.StepMs(0.006, 30));
    }

    [Fact]
    public void Labels_SitOnMultiplesOfTheStep()
    {
        foreach (var step in TimeRulerPolicy.NiceStepsMs)
        {
            foreach (var start in new[] { 0.0, 1.0, 999.0, 12_345.0, 600_001.0 })
            {
                var first = TimeRulerPolicy.FirstTickMs(start, step);
                Assert.True(first >= start && first - start < step, $"first tick {first} for view start {start}, step {step}");
                Assert.Equal(0, first % step);
            }
        }

        Assert.Equal(0, TimeRulerPolicy.FirstTickMs(-4_000, 5_000));
    }

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(5_000, "0:05")]
    [InlineData(65_000, "1:05")]
    [InlineData(754_000, "12:34")]
    [InlineData(3_599_000, "59:59")]
    [InlineData(3_600_000, "1:00:00")]
    [InlineData(3_725_000, "1:02:05")]
    public void TheLabel_IsTheShortestForm(double ms, string expected) =>
        Assert.Equal(expected, TimeRulerPolicy.Format(ms));

    /// <summary>The layer steps its labels by the policy — not a fixed second.</summary>
    [Fact]
    public void TheRuler_StepsThroughThePolicy()
    {
        var src = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.UI", "Controls", "Layers", "BackgroundLayer.cs")));
        var ruler = src[src.IndexOf("void DrawRuler(", StringComparison.Ordinal)..];
        ruler = ruler[..ruler.IndexOf("void DrawDashedVerticalLine(", StringComparison.Ordinal)];
        Assert.Contains("TimeRulerPolicy.StepMs(", ruler, StringComparison.Ordinal);
        Assert.Contains("TimeRulerPolicy.FirstTickMs(", ruler, StringComparison.Ordinal);
        Assert.DoesNotContain("RulerStep", ruler, StringComparison.Ordinal);
    }
}
