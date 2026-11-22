using Avalonia;
using DotCharter.Tone.Audio.Equalizer;
using DotCharter.UI.Controls;

namespace DotCharter.Tests.UI;

/// <summary>
///     Inspector design D's EQ plot: a press on a handle grabs that band, and a press anywhere else moves the
///     SELECTED band (the Band card's), not whichever band happens to be nearest.
/// </summary>
public sealed class ColumnFlowEqPlotGrabTests
{
    private static readonly Size Plot = new(400, 100);

    private static Point HandleOf(EqBand band) => new(
        EqCurve.FrequencyToNormalized(band.FrequencyHz) * Plot.Width,
        EqCurve.GainDbToNormalized(band.HasGain ? band.GainDb : 0.0) * Plot.Height);

    [Fact]
    public void APressOnAHandle_GrabsThatBand()
    {
        var bands = new EqSection().Bands;
        var target = bands.Count - 1;

        Assert.Equal(target, ColumnFlowEqPlot.Grab(bands, 0, HandleOf(bands[target]), Plot));
    }

    [Fact]
    public void APressAwayFromEveryHandle_MovesTheSelectedBand()
    {
        var bands = new EqSection().Bands;
        var far = new Point(Plot.Width / 2, Plot.Height - 1);
        Assert.All(bands, band =>
        {
            var handle = HandleOf(band);
            Assert.True(Math.Pow(handle.X - far.X, 2) + Math.Pow(handle.Y - far.Y, 2) > Math.Pow(ColumnFlowEqPlot.GrabRadius, 2),
                "the probe point sits on a handle — move it");
        });

        Assert.Equal(2, ColumnFlowEqPlot.Grab(bands, 2, far, Plot));
    }

    [Fact]
    public void NoBandsOrNoPlot_GrabsNothing()
    {
        Assert.Equal(-1, ColumnFlowEqPlot.Grab([], 0, default, Plot));
        Assert.Equal(-1, ColumnFlowEqPlot.Grab(new EqSection().Bands, 0, default, default));
    }

    /// <summary>
    ///     The frequency labels never touch: in a narrow column (the paged Mixer's ~220 px plot) "10k" and "20k" ran
    ///     together, so "20k" is left out there; a wide plot keeps all five. Widths are a fixed 6 px a character.
    /// </summary>
    [Theory]
    [InlineData(220.0, false)]
    [InlineData(600.0, true)]
    public void TheAxisLabels_NeverTouch_AndDropTwentyKInANarrowPlot(double width, bool keepsTwentyK)
    {
        static double Measure(string label) => label.Length * 6.0;
        var placed = ColumnFlowEqPlot.PlaceAxisLabels(width, Measure);

        for (var i = 1; i < placed.Count; i++)
        {
            Assert.True(placed[i].Left >= placed[i - 1].Left + Measure(placed[i - 1].Label) + 4,
                $"'{placed[i - 1].Label}' and '{placed[i].Label}' touch at {width} px");
        }

        Assert.All(placed, p => Assert.InRange(p.Left + Measure(p.Label), 0, width));
        Assert.Contains(placed, p => p.Label == "1k");
        Assert.Equal(keepsTwentyK, placed.Any(p => p.Label == "20k"));
    }

    /// <summary>A handle at either end of the range is drawn whole: its ring and outline stay inside the plot.</summary>
    [Fact]
    public void AnEdgeHandle_IsDrawnWholeInsideThePlot()
    {
        var right = ColumnFlowEqPlot.HandleCentre(Plot.Width, 0, Plot);
        var left = ColumnFlowEqPlot.HandleCentre(0, Plot.Height, Plot);

        Assert.True(right.X + 6.0 + 0.75 <= Plot.Width, $"the 20 kHz handle reaches {right.X + 6.75} px of {Plot.Width}");
        Assert.True(right.Y - 6.75 >= 0);
        Assert.True(left.X - 6.75 >= 0 && left.Y + 6.75 <= Plot.Height);
    }
}
