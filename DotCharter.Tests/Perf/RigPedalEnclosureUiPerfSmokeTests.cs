using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
///     The pedal enclosure editor, attached and laid out over a real chain:
///     the enclosure measures, every parameter is a live control on its grid,
///     the footswitch is a real ToggleButton on the node's bypass, and the
///     enclosure scales DOWN to a short dock rather than overflowing it. Own
///     process (headless suite), not the default gate.
///     <para>
///         Since 2026-09-27 every shipped module but <c>rsamp</c> (drawn by design,
///         <c>RigFaceplates.DrawnEnclosureByDesign</c>) has a generated FACEPLATE and the
///         editor draws that instead (<c>RigFaceplateUiPerfSmokeTests</c>); the drawn
///         enclosure is the fallback for a module without one. This class switches
///         the faceplates off for its own lifetime to keep that fallback honest.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class RigPedalEnclosureUiPerfSmokeTests : IDisposable
{
    public RigPedalEnclosureUiPerfSmokeTests() => RigFaceplates.SuppressedForTests = true;

    public void Dispose() => RigFaceplates.SuppressedForTests = false;

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(1280, 720)]
    public async Task ThePedalEnclosure_LaysOutEveryControl_AndFitsTheDock(int width, int height)
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var metadata = new JuceDspChainService();
            metadata.LoadCatalog();
            var catalogue = metadata.Catalog;
            Assert.NotEmpty(catalogue);
            var chain = new FakeDspChainService(catalogue);
            using var rig = TestRigBuilders.Pedalboard(chain, new FakeDspTonePresetService(), LocalizationService.Instance);
            foreach (var id in new[] { "overdrive", "chorus", "delay" })
                rig.AddGear(catalogue.Single(m => m.Id == id));
            var delay = rig.Nodes.Single(n => n.ModuleTypeId == "delay");
            rig.SelectNodeAt(rig.Nodes.IndexOf(delay));
            // the footnote rides the DETAILS column beside the stage
            var model = new AmpFxRigViewModel(rig) { IsDetailsOpen = true };
            var view = new AmpFxRigView { DataContext = model };
            var window = new Window { Width = width, Height = height, Content = view };
            window.Show();
            try
            {
                for (var pass = 0; pass < 4; pass++) window.UpdateLayout();
                var unit = model.DockUnit!;
                Assert.True(unit.IsPedalFamily);

                var pedal = Assert.Single(view.GetVisualDescendants().OfType<RigPedalEditorView>());
                var enclosure = Assert.Single(pedal.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("FireEnclosure"));
                Assert.True(enclosure.Bounds.Width > 200 && enclosure.Bounds.Height > 150, $"enclosure {enclosure.Bounds}");

                var knobs = enclosure.GetVisualDescendants().OfType<FireKnob>().ToList();
                var pickers = enclosure.GetVisualDescendants().OfType<ComboBox>().ToList();
                Assert.Equal(unit.Node.KnobParams.Count, knobs.Count);
                Assert.Equal(unit.Node.ModelSelectors.Count, pickers.Count);
                Assert.Equal(unit.EnclosureControls.Count, knobs.Count + pickers.Count);
                foreach (var knob in knobs) Assert.True(knob.Bounds.Width > 10 && knob.Bounds.Height > 10, "a knob rendered nothing");
                Assert.All(knobs, k => Assert.True(k.HardwareStyle));
                Assert.All(knobs, k => Assert.NotNull(FireUnitState.GetAccent(k)));

                var footswitch = Assert.Single(enclosure.GetVisualDescendants().OfType<ToggleButton>());
                Assert.False(footswitch.IsChecked);
                footswitch.IsChecked = true;
                Assert.True(delay.Bypassed, "the footswitch is the node's bypass");
                footswitch.IsChecked = false;

                var fit = pedal.FindControl<RigScaleToFit>("RigEnclosureFit")!;
                Assert.InRange(fit.Scale, RigArtwork.MinEditorScale, RigArtwork.MaxEditorScale);
                var scroll = pedal.FindControl<ScrollViewer>("RigEditorScroll")!;
                Assert.True(fit.Bounds.Width <= scroll.Viewport.Width + 1, $"the enclosure ({fit.Bounds.Width}) overflows the editor ({scroll.Viewport.Width})");

                var footnote = Assert.Single(view.GetVisualDescendants().OfType<RigModuleEditorView>())
                    .GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("FireEditorFootnote"));
                Assert.True(footnote.IsVisible && footnote.Bounds.Height > 0);
            }
            finally { window.Close(); }
        }, TestContext.Current.CancellationToken);
    }

    /// <summary>
    ///     A ONE-control pedal (Clean Boost's lone Gain) lays its knob in one
    ///     column, centred on the enclosure, at the big single dial — not in
    ///     the left cell of an empty two-column grid ("clean boost looks kinda
    ///     weird", 2026-09-08). The enclosure keeps its two-column width.
    /// </summary>
    [Fact]
    public async Task ALoneKnob_StandsCentred_AtTheBigDial()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var metadata = new JuceDspChainService();
            metadata.LoadCatalog();
            var catalogue = metadata.Catalog;
            var chain = new FakeDspChainService(catalogue);
            using var rig = TestRigBuilders.Pedalboard(chain, new FakeDspTonePresetService(), LocalizationService.Instance);
            rig.AddGear(catalogue.Single(m => m.Id == "gain"));
            rig.SelectNodeAt(0);
            var model = new AmpFxRigViewModel(rig);
            var view = new AmpFxRigView { DataContext = model };
            var window = new Window { Width = 1920, Height = 1080, Content = view };
            window.Show();
            try
            {
                for (var pass = 0; pass < 4; pass++) window.UpdateLayout();
                var unit = model.DockUnit!;
                Assert.True(unit.IsPedalFamily);
                Assert.Single(unit.EnclosureControls);

                var pedal = Assert.Single(view.GetVisualDescendants().OfType<RigPedalEditorView>());
                var enclosure = Assert.Single(pedal.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("FireEnclosure"));
                Assert.Equal(2 * 142 + 68, unit.EnclosureNaturalWidth);

                var grid = Assert.Single(enclosure.GetVisualDescendants().OfType<UniformGrid>());
                Assert.Equal(1, grid.Columns);
                var knob = Assert.Single(enclosure.GetVisualDescendants().OfType<FireKnob>());
                Assert.Equal(RigDockUnit.SingleDial, knob.DialSize);
                Assert.True(knob.Bounds.Width >= RigDockUnit.SingleDial, $"the dial grew the knob: {knob.Bounds}");

                var knobCentre = knob.TranslatePoint(new Point(knob.Bounds.Width / 2, 0), enclosure)!.Value.X;
                Assert.Equal(enclosure.Bounds.Width / 2, knobCentre, 2.0);
            }
            finally { window.Close(); }
        }, TestContext.Current.CancellationToken);
    }
}
