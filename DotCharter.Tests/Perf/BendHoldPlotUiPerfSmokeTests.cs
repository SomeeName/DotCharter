using DotCharter.UI.Controls;
using DotCharter.UI.ViewModels.Editor.Inspector;
using static DotCharter.Tests.Perf.NoteSectionStripUiPerfSmokeTests;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The Note tab's Bends graph over a note bent 0 to Full in the first quarter of its sustain, in the REAL
///     <see cref="DotCharter.UI.Views.Editor.InspectorView" /> at the primary Small size (530 x 370). The plot is built,
///     attached and measured non-zero, its editor holds the two points, and it draws the held rest of the sustain as a
///     flat line from the last point to the right edge (GP draws the hold; display only, no stored point is added).
///     Run it under <c>DOTCHARTER_HEADLESS_CULTURE=de-DE</c> and <c>ja-JP</c> too; with
///     <c>DOTCHARTER_UI_SWEEP_DUMP=1</c> it writes <c>TestResults/note-section-strip/807-bends-graph.png</c>.
/// </summary>
[Collection("HeadlessUI")]
public sealed class BendHoldPlotUiPerfSmokeTests
{
    [Fact]
    public async Task TheBendsGraph_LaysOut_AndDrawsTheHoldToTheSustainEnd() =>
        await HeadlessSessionFixture.Dispatch(() => WithPane(530, 370, pane =>
        {
            var note = pane.Level.Notes.First(n => n.BendValues.Count == 0 && n.Sustain.DoubleValue > 0);
            note.BendValues.Add(new BendValue(note.Position, 0));
            note.BendValues.Add(new BendValue(note.Position + QuarterOf(note.Sustain), 2));
            Select(pane, note);
            Open(pane, SoundSection.Bends);

            var plot = Named<ColumnFlowBendPlot>(Sound(pane), "SoundBendsPlot");
            Assert.True(plot.IsEffectivelyVisible, "the Bends graph is not on screen");
            Assert.True(plot.Bounds.Width > 0 && plot.Bounds.Height > 0,
                $"the Bends graph measured {plot.Bounds.Width:0} x {plot.Bounds.Height:0}");
            var editor = plot.Editor ?? throw new Xunit.Sdk.XunitException("the Bends graph has no editor");
            Assert.Equal(2, editor.Points.Count);
            var hold = plot.HoldLine(editor) ?? throw new Xunit.Sdk.XunitException("the Bends graph draws no hold");
            Assert.Equal(hold.From.Y, hold.To.Y);
            Assert.True(hold.From.X < hold.To.X && hold.To.X <= plot.Bounds.Width,
                $"the hold runs {hold.From.X:0}..{hold.To.X:0} px in a {plot.Bounds.Width:0} px plot");
            Assert.Equal(plot.ToPixels(editor.Points[^1]), hold.From);
            Capture(pane, "807-bends-graph");
        }), CancellationToken.None);

    private static FractionalPosition QuarterOf(FractionalPosition sustain)
    {
        var total = new Fraction(sustain.BeatId, 1) + sustain.Fraction;
        var quarter = total * new Fraction(1, 4);
        return new FractionalPosition(quarter.Numerator / quarter.Denominator,
            new Fraction(quarter.Numerator % quarter.Denominator, quarter.Denominator));
    }
}
