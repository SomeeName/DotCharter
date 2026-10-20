using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.UI.Controls.Dsp;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using DotCharter.Tone.UI.Views.PlayMode;
using DotCharter.Tone.UI.Views.PlayMode.RigEditors;
using DotCharter.Localization;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The routing console over a real split chain: the family view opens for
///     the splitter, the three mode toggles resolve live commands, and every
///     lane has a strip with its level and pan sliders. Own process.
/// </summary>
[Collection("HeadlessUI")]
public sealed class RigRoutingConsoleUiPerfSmokeTests
{
    [Fact]
    public async Task TheSplitter_OpensTheConsole_WithOneStripPerLane()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var metadata = new JuceDspChainService();
            metadata.LoadCatalog();
            var catalogue = metadata.Catalog;
            var chain = new FakeDspChainService(catalogue);
            using var rig = TestRigBuilders.Pedalboard(chain, new FakeDspTonePresetService(), LocalizationService.Instance);
            foreach (var id in new[] { "overdrive", "delay", "reverb" })
                rig.AddGear(catalogue.Single(m => m.Id == id));
            rig.SelectNodeAt(0);
            rig.RoutingSplit3Command.Execute(null);
            rig.SelectNodeAt(rig.Nodes.IndexOf(rig.Nodes.Single(n => n.IsSplitter)));
            var model = new AmpFxRigViewModel(rig);
            var view = new AmpFxRigView { DataContext = model };
            var window = new Window { Width = 1920, Height = 1080, Content = view };
            window.Show();
            try
            {
                for (var pass = 0; pass < 4; pass++) window.UpdateLayout();
                var unit = model.DockUnit!;
                Assert.Equal(RigEditorFamily.Routing, unit.Family);
                Assert.IsType<RigRoutingConsoleView>(RigEditorTemplateSelector.BuildFor(unit.Family));
                var console = Assert.Single(view.GetVisualDescendants().OfType<RigRoutingConsoleView>());

                var modes = console.FindControl<Border>("RigRoutingModes")!.GetVisualDescendants().OfType<ToggleButton>().ToList();
                Assert.Equal(3, modes.Count);
                Assert.All(modes, m => Assert.True(m.Command?.CanExecute(null) ?? false, "a mode toggle has no live command"));
                Assert.True(modes[2].IsChecked);

                var strips = console.FindControl<ItemsControl>("RigRoutingLanes")!;
                var sliders = strips.GetVisualDescendants().OfType<Slider>().ToList();
                Assert.Equal(unit.Routing!.LaneCount * 2, sliders.Count);
                Assert.Equal(3, unit.Routing.LaneCount);
                Assert.All(sliders, s => Assert.True(s.Bounds.Width > 40, "a lane slider is too narrow to use"));
                Assert.Equal(6, strips.GetVisualDescendants().OfType<ToggleButton>().Count());

                // DUAL from the console is the rig's own routing command
                modes[1].Command!.Execute(null);
                for (var pass = 0; pass < 3; pass++) window.UpdateLayout();
                Assert.True(rig.IsRoutingSplit2);
                Assert.Equal(2, unit.Routing.LaneCount);
            }
            finally { window.Close(); }
        }, TestContext.Current.CancellationToken);
    }
}
