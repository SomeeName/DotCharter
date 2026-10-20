using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.UI.Controls.Fire;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using DotCharter.Tone.UI.Views.PlayMode;
using DotCharter.Localization;

namespace DotCharter.Tests.Perf;

[Collection("HeadlessUI")]
public sealed class RigHardwareUiPerfSmokeTests
{
    [Theory]
    [InlineData(1920, 1080, "namamp")]
    [InlineData(1920, 1080, "cab")]
    [InlineData(1920, 1080, "delay")]
    [InlineData(1920, 1080, "triple")]
    [InlineData(1280, 800, "namamp")]
    [InlineData(1280, 800, "triple")]
    public async Task HardwareWorkbench_KeepsControlsReachableAndOpensTheAmp(int width, int height, string surface)
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            // Catalogue inspection does not open an audio engine or device.
            var metadata = new JuceDspChainService();
            metadata.LoadCatalog();
            var catalogue = metadata.Catalog;
            Assert.NotEmpty(catalogue);
            var chain = new FakeDspChainService(catalogue);
            using var rig = TestRigBuilders.Pedalboard(chain, new FakeDspTonePresetService(), LocalizationService.Instance);
            foreach (var id in new[] { "namamp", "cab", "overdrive", "delay", "reverb" })
                rig.AddGear(catalogue.Single(m => m.Id == id));
            if (surface == "triple")
            {
                rig.SelectNodeAt(0);
                rig.RoutingSplit3Command.Execute(null);
                rig.MoveTo(rig.Nodes.Single(n => n.IsMerger), rig.Nodes.Count - 1);
                var index = 0;
                foreach (var node in rig.Nodes.Where(n => !n.IsJunction).ToArray())
                    chain.SetNodeBranch(node.NodeId, index++ % 3 + 1);
                rig.ResyncAll();
            }
            rig.SelectNodeAt(surface is "cab" or "delay" ? rig.Nodes.ToList().FindIndex(n => n.ModuleTypeId == surface) : -1);
            // the controllers and the meters are the unit's DETAILS, beside the stage
            var model = new AmpFxRigViewModel(rig) { IsDetailsOpen = true };
            var view = new AmpFxRigView { DataContext = model };
            var window = new Window { Width = width, Height = height, Content = view };
            window.Show();
            try
            {
                for (var pass = 0; pass < 4; pass++) window.UpdateLayout();
                Assert.Equal(surface is "cab" or "delay" ? surface : "namamp", model.DockUnit?.Node.ModuleTypeId);
                var fit = view.FindControl<Button>("RigFitButton")!;
                fit.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                for (var pass = 0; pass < 3; pass++) window.UpdateLayout();
                var viewport = view.FindControl<ScrollViewer>("RigBoardScroll")!;
                Assert.True(viewport.Extent.Width <= viewport.Viewport.Width + 2);
                Assert.True(viewport.Extent.Height <= viewport.Viewport.Height + 2,
                    $"Fit {rig.Zoom}: extent {viewport.Extent}, viewport {viewport.Viewport}, padding {viewport.Padding}");
                var board = view.FindControl<Border>("RigBoard")!;
                var editor = view.FindControl<Border>("RigDockBody")!;
                Assert.True(board.Bounds.Height >= 180);
                Assert.True(editor.IsVisible && editor.Bounds.Height > 100);
                var cards = view.GetVisualDescendants().OfType<Border>()
                    .Where(b => b.Classes.Contains("FireUnitCard")).ToArray();
                Assert.Equal(5, cards.Length);
                // The drag ghost is the CARD (class FireDragGhost), not its item
                // container, which also holds the lane wire and its arrowhead —
                // dragging a pedal used to carry the arrow along (owner, 2026-09-08).
                foreach (var card in cards)
                {
                    var container = card.FindAncestorOfType<ContentPresenter>();
                    Assert.NotNull(container);
                    Assert.Same(card, FireDragSession.GhostPartOf(container!));
                }
                foreach (var card in cards)
                {
                    // the unit's own gear, small: a picture with every knob drawn at
                    // its live value, never a hit target (the card takes the click
                    // and the drag) — the selected one opens big in the dock
                    var hardware = Assert.Single(card.GetVisualDescendants().OfType<RigFaceplateView>());
                    Assert.True(hardware.HasFaceplate && !hardware.IsInteractive && !hardware.IsHitTestVisible,
                        $"{((PedalNodeViewModel)card.DataContext!).ModuleTypeId}: the chain's picture is a live control");
                    Assert.Equal(model.BoardFaceHeight, hardware.Bounds.Height, 1);
                    Assert.True(card.Bounds.Width >= 40 && card.Bounds.Height > model.BoardFaceHeight, $"a unit card {card.Bounds}");
                    Assert.Empty(card.GetVisualDescendants().OfType<FireKnob>());
                }

                // the controller / assignment / MIDI panels are folded until opened
                var controllers = editor.GetVisualDescendants().OfType<Expander>().Single(e => e.Name == "RigEditorControllers");
                Assert.False(controllers.IsExpanded);
                controllers.IsExpanded = true;
                for (var pass = 0; pass < 3; pass++) window.UpdateLayout();
                Assert.Single(editor.GetVisualDescendants().OfType<RigPerformanceControlsView>());
                Assert.Single(editor.GetVisualDescendants().OfType<RigControlAssignmentView>());
                Assert.Single(editor.GetVisualDescendants().OfType<RigMidiControlsView>());
                // the one the unit SHOWS: the details column keeps the pedal family's
                // meters (hidden) beside a cabinet's own
                var meters = Assert.Single(editor.GetVisualDescendants().OfType<RigNodeMetersView>(), m => m.IsEffectivelyVisible);
                Assert.True(meters.Bounds.Width > 0 && meters.Bounds.Height > 0);
                Assert.Equal(2, meters.GetVisualDescendants().OfType<FireLevelMeter>().Count());

                // The design's header owns the zoom cluster; the footer shows IN and OUT.
                var header = view.FindControl<Border>("RigSignalStrip")!;
                Assert.Contains(view.FindControl<Button>("RigZoomIn")!, header.GetVisualDescendants());
                var footer = view.FindControl<Border>("RigFooter")!;
                foreach (var name in new[] { "RigInputMeter", "RigOutputMeter" })
                {
                    var meter = view.FindControl<FireLevelMeter>(name)!;
                    Assert.Contains(meter, footer.GetVisualDescendants());
                    Assert.True(meter.Bounds.Width > 0 && meter.Bounds.Height > 0, $"{name} laid out as {meter.Bounds}");
                }
                Assert.True(view.FindControl<Grid>("RigBoardCaption")!.Bounds.Height > 0);

                // A triple board ends every lane in a mixer node with its pan knob and M/S.
                var mixers = board.GetVisualDescendants().OfType<Border>()
                    .Where(b => b.Classes.Contains("FireMixerNode")).ToArray();
                Assert.Equal(surface == "triple" ? 3 : 0, mixers.Length);
                foreach (var mixer in mixers)
                {
                    Assert.True(mixer.Bounds.Width > 100 && mixer.Bounds.Height > 40, $"a mixer node laid out as {mixer.Bounds}");
                    Assert.Single(mixer.GetVisualDescendants().OfType<FireKnob>());
                    Assert.Equal(2, mixer.GetVisualDescendants().OfType<ToggleButton>().Count());
                }
                var artifactDir = Environment.GetEnvironmentVariable("DOTCHARTER_RIG_CAPTURE_DIR");
                if (UiSweepRunner.CaptureEnabled && !string.IsNullOrWhiteSpace(artifactDir))
                {
                    Directory.CreateDirectory(artifactDir);
                    using var frame = window.CaptureRenderedFrame();
                    Assert.NotNull(frame);
                    frame.Save(Path.Combine(artifactDir, $"rig-{surface}-{width}x{height}.png"), new PngBitmapEncoderOptions());
                }
            }
            finally { window.Close(); }
        }, TestContext.Current.CancellationToken);
    }
}
