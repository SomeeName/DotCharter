using Avalonia.Controls;
using Avalonia.VisualTree;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.UI.Controls.Dsp;
using DotCharter.Tone.UI.Controls.Fire;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using DotCharter.Tone.UI.Views.PlayMode;
using DotCharter.Tone.UI.Views.PlayMode.RigEditors;
using DotCharter.Localization;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The amp face editor over a real chain: the family view is the one the
///     host opens for both amp heads, the shell picture resolves, every face
///     knob and secondary knob is a live control, and the lamp follows the
///     bypass. Own process (headless suite).
///     <para>
///         Since 2026-09-27 a unit with a generated faceplate is edited on the BOARD
///         and the dock opens the unit inspector (<c>RigDockUnit.EditorFamilyFor</c>);
///         the amp face is the family view for a head WITHOUT a faceplate. This class
///         switches the faceplates off for its lifetime to keep that fallback honest.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class RigAmpFaceUiPerfSmokeTests : IDisposable
{
    public RigAmpFaceUiPerfSmokeTests() => RigFaceplates.SuppressedForTests = true;

    public void Dispose() => RigFaceplates.SuppressedForTests = false;

    [Theory]
    [InlineData("namamp")]
    [InlineData("amp")]
    public async Task TheAmpFace_OpensForBothHeads_WithEveryKnobLive(string moduleId)
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var metadata = new JuceDspChainService();
            metadata.LoadCatalog();
            var catalogue = metadata.Catalog;
            var chain = new FakeDspChainService(catalogue);
            using var rig = TestRigBuilders.Pedalboard(chain, new FakeDspTonePresetService(), LocalizationService.Instance);
            rig.AddGear(catalogue.Single(m => m.Id == moduleId));
            rig.AddGear(catalogue.Single(m => m.Id == "cab"));
            rig.SelectNodeAt(0);
            var model = new AmpFxRigViewModel(rig);
            var view = new AmpFxRigView { DataContext = model };
            var window = new Window { Width = 1920, Height = 1080, Content = view };
            window.Show();
            try
            {
                for (var pass = 0; pass < 4; pass++) window.UpdateLayout();
                var unit = model.DockUnit!;
                Assert.Equal(RigEditorFamily.AmpFace, unit.Family);
                Assert.IsType<RigAmpFaceView>(RigEditorTemplateSelector.BuildFor(unit.Family));

                var face = Assert.Single(view.GetVisualDescendants().OfType<RigAmpFaceView>());
                Assert.Empty(view.GetVisualDescendants().OfType<RigPedalEditorView>());
                var head = face.FindControl<Panel>("RigAmpFace")!;
                Assert.True(head.Bounds.Width > 600 && head.Bounds.Height >= 218, $"head {head.Bounds}");

                var shell = head.GetVisualDescendants().OfType<RigArtworkImage>().First();
                Assert.Equal(RigArtwork.ShellFor(moduleId), shell.ResolvedFile);
                Assert.NotNull(RigArtworkStore.Get(shell.ResolvedFile));

                var faceKnobs = face.FindControl<ItemsControl>("RigAmpFaceKnobs")!.GetVisualDescendants().OfType<FireKnob>().ToList();
                Assert.Equal(unit.PrimaryKnobs.Count, faceKnobs.Count);
                Assert.True(faceKnobs.Count >= 5, "the head should carry its face knobs");
                Assert.All(faceKnobs, k => Assert.True(k.Bounds.Width > 10 && k.Bounds.Height > 10));

                var secondaryRow = face.FindControl<Panel>("RigAmpSecondary")!;
                var secondary = secondaryRow.GetVisualDescendants().OfType<FireKnob>().Count();
                Assert.Equal(unit.SecondaryKnobs.Count, secondary);
                Assert.Equal(unit.Node.KnobParams.Count, faceKnobs.Count + secondary);

                // The gear column: the head fills the dock up to the design's
                // 1180 px and the secondary row ends on the SAME right edge —
                // a head at its content width left the cards short of it and
                // the SIGNAL card wrapped underneath (2026-09-08).
                //
                // Stated against the slot the face was LAID OUT in, not against
                // a window fraction: since lane E (2026-09-13) the face sits in
                // a `RigScaleToFit`, which lays it out at `dock / scale` logical
                // pixels and draws it at `scale`. The design cap is a logical
                // number and stays one — the head is DRAWN wider than 1180 at
                // 4K and that is the point of the scaler — but "the head takes
                // everything the capture card's floor leaves" is a claim about
                // the logical slot, and that slot is no longer the dock's width.
                var columns = face.FindControl<Grid>("RigAmpColumns")!;
                var wanted = Math.Min(RigAmpFaceView.GearMaxWidth,
                    columns.Bounds.Width - RigAmpFaceView.CaptureMinWidth - RigAmpFaceView.ColumnGap);
                // +1.5: `CaptureWidthFor` runs from SizeChanged, so it sees the
                // PREVIOUS pass's width, and under a fractional scale the two
                // differ by a pixel. Measured 1181 against the 1180 cap for
                // `namamp`; it is rounding, not a column that escaped its cap.
                Assert.True(head.Bounds.Width <= RigAmpFaceView.GearMaxWidth + 1.5,
                    $"head wider than the design cap: {head.Bounds}");
                Assert.True(head.Bounds.Width >= wanted - 2,
                    $"head did not fill its slot: {head.Bounds} in {columns.Bounds} (wanted {wanted:F0})");
                Assert.True(Math.Abs(secondaryRow.Bounds.Right - head.Bounds.Right) < 1.5,
                    $"secondary row {secondaryRow.Bounds} does not end with the head {head.Bounds}");
                var signal = face.GetVisualDescendants().OfType<RigNodeMetersView>().Single();
                Assert.True(signal.Bounds.Width > 200, $"the SIGNAL card measured {signal.Bounds}");
                var cards = secondaryRow.GetVisualDescendants().OfType<Border>()
                    .Where(b => b.Classes.Contains("FireAmpSecondaryCard")).ToList();
                // The neural head has resonance/input/output cards before the
                // SIGNAL card; the legacy head has the SIGNAL card alone.
                Assert.True(cards.Count >= (unit.SecondaryKnobs.Count > 0 ? 2 : 1), "the secondary row should carry its cards");
                // Siblings in one horizontal StackPanel (the knob cards) share a
                // parent, and the SIGNAL card is a Grid column beside them — so
                // every card's Bounds.Y is against the same row.
                var top = cards[0].Bounds.Y;
                Assert.All(cards, c => Assert.True(Math.Abs(c.Bounds.Y - top) < 1.5,
                    $"a secondary card wrapped onto another row: {c.Bounds} against {top}"));

                var lamp = Assert.Single(head.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("FireAmpLamp"));
                Assert.Contains("Lit", lamp.Classes);
                unit.Node.Bypassed = true;
                Assert.DoesNotContain("Lit", lamp.Classes);
                unit.Node.Bypassed = false;

                Assert.Single(face.GetVisualDescendants().OfType<RigNodeMetersView>());
            }
            finally { window.Close(); }
        }, TestContext.Current.CancellationToken);
    }
}
