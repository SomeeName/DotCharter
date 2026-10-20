using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.UI.Controls.Fire;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using DotCharter.Tone.UI.Views.PlayMode;
using DotCharter.Localization;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The rig board's signal path is ONE wire from the IN jack to the OUT
///     jack: one centre line, no gaps, one colour. Pinned after the owner's
///     "arrows in rig view not nicely connected" (2026-09-08): the "Lane A"
///     caption used to be a row INSIDE the serial run, so the run's connectors
///     sat 8 px below the jacks' leads — in lane A's colour against the leads'
///     accent — and the OUT arrowhead stopped 16 px short of its jack with a
///     bare lead running on past it. Every assertion here failed against that
///     markup — and the centre-line one then failed AGAIN against the first
///     fix, because the jack well was a StackPanel of ring + label whose centre
///     sat 8 px under the ring's; the well is the ring's height now.
/// </summary>
[Collection("HeadlessUI")]
public sealed class RigBoardWiresUiPerfSmokeTests
{
    [Theory]
    [InlineData(true)]  // series: the run is lane A, and so is everything else on the path
    [InlineData(false)] // around a split region: the trunk keeps the accent, and so does the path
    public async Task ThePath_IsOneLine_WithoutGaps_InOneColour_FromJackToJack(bool series)
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var metadata = new JuceDspChainService();
            metadata.LoadCatalog();
            var catalogue = metadata.Catalog;
            var chain = new FakeDspChainService(catalogue);
            using var rig = TestRigBuilders.Pedalboard(chain, new FakeDspTonePresetService(), LocalizationService.Instance);
            PedalNodeViewModel Add(string id) => rig.AddGear(catalogue.Single(m => m.Id == id))!;
            Add("gate");
            var amp = Add("namamp");
            var cab = Add("cab");
            Add("reverb");
            if (!series)
            {
                // gate · [split] · amp, cab in lane A · [merge] · reverb — the trunk
                // before the region is a serial run, so the first connector is
                // still the one into the gate.
                rig.SelectNodeAt(0);
                rig.RoutingSplit2Command.Execute(null);
                rig.MoveTo(rig.Nodes.Single(n => n.IsSplitter), 1);
                rig.MoveTo(rig.Nodes.Single(n => n.IsMerger), rig.Nodes.Count - 2);
                chain.SetNodeBranch(amp.NodeId, 1);
                chain.SetNodeBranch(cab.NodeId, 1);
                rig.ResyncAll();
            }
            Assert.Equal(series, rig.IsRoutingSerial);

            var model = new AmpFxRigViewModel(rig);
            var view = new AmpFxRigView { DataContext = model };
            var window = new Window { Width = 1586, Height = 1000, Content = view };
            window.Show();
            try
            {
                for (var pass = 0; pass < 4; pass++) window.UpdateLayout();

                var jackIn = view.FindControl<Grid>("RigJackIn")!;
                var leadIn = view.FindControl<RigWire>("RigLeadIn")!;
                var leadOut = view.FindControl<Panel>("RigLeadOut")!;
                var jackOut = view.FindControl<Grid>("RigJackOut")!;
                var ringIn = Ring(jackIn);
                var ringOut = Ring(jackOut);
                var firstWire = view.FindControl<ItemsControl>("RigBoardItems")!
                    .GetVisualDescendants().OfType<RigWire>().First(); // the connector into the first node
                var outWire = leadOut.GetVisualDescendants().OfType<RigWire>().Single();

                // BOTH corners are translated, not the origin plus the logical
                // size: the board sits under RigBoardZoomHost's transform, and
                // since W1 that transform carries the FIT as well as the user's
                // zoom, so on a split board at 1586 px it is ~0.95 rather than
                // exactly 1. Mixing a transformed origin with an untransformed
                // width made every adjacency assertion off by (1 - scale) × width
                // — and it would have done the same the first time anyone tested
                // this screen at a zoom other than 100 %.
                Rect R(Visual v) => new(
                    v.TranslatePoint(new Point(0, 0), window)!.Value,
                    v.TranslatePoint(new Point(v.Bounds.Width, v.Bounds.Height), window)!.Value);

                // ONE centre line: jack, lead, first connector, last connector, jack
                var y = R(ringIn).Center.Y;
                Assert.Equal(y, R(leadIn).Center.Y, 1.0);
                Assert.Equal(y, R(firstWire).Center.Y, 1.0);
                Assert.Equal(y, R(leadOut).Center.Y, 1.0);
                Assert.Equal(y, R(ringOut).Center.Y, 1.0);

                // NO GAPS: the lead starts on the IN ring and hands straight to
                // the first connector; the OUT connector's arrowhead lands on its ring
                Assert.Equal(R(ringIn).Right, R(leadIn).Left, 1.0);
                Assert.Equal(R(leadIn).Right, R(firstWire).Left, 1.0);
                Assert.Equal(R(leadOut).Right, R(ringOut).Left, 1.0);
                Assert.True(outWire.ShowsArrow, "the OUT connector ends in the arrowhead");
                Assert.False(leadIn.ShowsArrow, "the IN lead hands to a connector that has the arrowhead");

                // ONE colour: every wire and both jacks read the board's token
                var token = series ? "FireLaneA" : "FireAccent";
                Assert.Equal(token, model.SerialLaneToken);
                Assert.Equal(token, FireUnitState.GetLaneToken(leadIn));
                Assert.Equal(token, FireUnitState.GetLaneToken(firstWire));
                Assert.Equal(token, FireUnitState.GetLaneToken(outWire));
                var expected = FireUnitState.ColorFrom(ringIn, token);
                Assert.NotEqual(Colors.Gray, expected);
                Assert.Equal(expected, Assert.IsAssignableFrom<ISolidColorBrush>(ringIn.BorderBrush).Color);
                Assert.Equal(expected, Assert.IsAssignableFrom<ISolidColorBrush>(ringOut.BorderBrush).Color);

                // the "Lane A" caption stands ABOVE the path on a series board and
                // is gone around a split region (the lanes carry their own chips)
                var label = view.FindControl<TextBlock>("RigSerialLaneLabel")!;
                Assert.Equal(series, label.IsVisible);
                if (series)
                {
                    Assert.True(label.Bounds.Height > 0, "the caption laid out");
                    Assert.True(R(label).Bottom <= R(ringIn).Top + 1, $"caption {R(label)} is not above the jack {R(ringIn)}");
                }
            }
            finally { window.Close(); }
        }, TestContext.Current.CancellationToken);
    }

    private static Border Ring(Grid well)
        => well.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("FireJack"));
}
