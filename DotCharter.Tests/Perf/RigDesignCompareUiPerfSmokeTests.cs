using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using DotCharter.Tone.UI.Views.PlayMode;
using DotCharter.Localization;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The rig screen in the same states the tone-rig prototype's own capture
///     script takes (its default rig: noise gate → split {A: amp, cab · B: drive,
///     legacy amp, cab · C: chorus, delay} → merge → reverb), at the prototype's
///     1586×1000 viewport, so the two can be compared side by side. Every
///     state must lay out with the expected unit open; with
///     <c>DOTCHARTER_UI_SWEEP_DUMP=1</c> and <c>DOTCHARTER_RIG_CAPTURE_DIR</c>
///     set it also writes <c>app-&lt;state&gt;.png</c>.
/// </summary>
[Collection("HeadlessUI")]
public sealed class RigDesignCompareUiPerfSmokeTests
{
    [Theory]
    [InlineData("triple-cab", 3, "cab")]
    [InlineData("triple-amp", 3, "namamp")]
    [InlineData("triple-pedal", 3, "chorus")]
    [InlineData("triple-split", 3, "splitter")]
    [InlineData("dual-drive", 2, "overdrive")]
    [InlineData("series-amp", 1, "namamp")]
    [InlineData("series-cab", 1, "cab")]
    [InlineData("series-favorites", 1, "cab")]
    [InlineData("triple-graphiceq", 3, "graphiceq")]
    [InlineData("triple-wah", 3, "wah")]
    [InlineData("triple-compressor", 3, "compressor")]
    [InlineData("tones", 1, "cab")]
    public async Task TheScreen_MatchesThePrototypeState(string state, int lanes, string selected)
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var metadata = new JuceDspChainService();
            metadata.LoadCatalog();
            var catalogue = metadata.Catalog;
            var chain = new FakeDspChainService(catalogue);
            using var rig = TestRigBuilders.Pedalboard(chain, new FakeDspTonePresetService(), LocalizationService.Instance);
            PedalNodeViewModel Add(string id) => rig.AddGear(catalogue.Single(m => m.Id == id))!;

            // the prototype's default rig; a "replace a1" state swaps the neural amp
            var laneA = state.StartsWith("triple-", StringComparison.Ordinal) && selected is "graphiceq" or "wah" or "compressor"
                ? selected : "namamp";
            Add("gate");
            var a1 = Add(laneA); var a2 = Add("cab");
            // the prototype's Series shows lane A alone; the other lanes stay hidden
            var b1 = lanes > 1 ? Add("overdrive") : null; var b2 = lanes > 1 ? Add("amp") : null; var b3 = lanes > 1 ? Add("cab") : null;
            var c1 = lanes > 1 ? Add("chorus") : null; var c2 = lanes > 1 ? Add("delay") : null;
            Add("reverb");
            if (lanes > 1)
            {
                rig.SelectNodeAt(0);
                (lanes == 3 ? rig.RoutingSplit3Command : rig.RoutingSplit2Command).Execute(null);
                rig.MoveTo(rig.Nodes.Single(n => n.IsSplitter), 1);                  // after the gate
                rig.MoveTo(rig.Nodes.Single(n => n.IsMerger), rig.Nodes.Count - 2); // before the reverb
                foreach (var (node, branch) in new[] { (a1, 1), (a2, 1), (b1!, 2), (b2!, 2), (b3!, 2), (c1!, lanes == 3 ? 3 : 2), (c2!, lanes == 3 ? 3 : 2) })
                    chain.SetNodeBranch(node.NodeId, branch);
                rig.ResyncAll();
            }
            var pick = rig.Nodes.ToList().FindIndex(n => n.ModuleTypeId == selected);
            Assert.True(pick >= 0, $"no {selected} in the chain");
            rig.SelectNodeAt(pick);

            var model = new AmpFxRigViewModel(rig);
            if (state.EndsWith("favorites", StringComparison.Ordinal))
            {
                foreach (var id in new[] { "namamp", "cab", "overdrive", "delay" })
                    rig.GearRail.ToggleFavoriteCommand.Execute(id);
                model.GearLibraryTabs[1].Command.Execute(null);
            }
            if (state == "tones") rig.ToggleBrowserCommand.Execute(null);
            var view = new AmpFxRigView { DataContext = model };
            var window = new Window { Width = 1586, Height = 1000, Content = view };
            window.Show();
            try
            {
                for (var pass = 0; pass < 4; pass++) window.UpdateLayout();
                view.FindControl<Button>("RigFitButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                for (var pass = 0; pass < 3; pass++) window.UpdateLayout();

                Assert.Equal(selected, model.DockUnit?.Node.ModuleTypeId);
                if (state == "tones")
                {
                    Assert.True(view.GetVisualDescendants().OfType<Border>()
                        .First(b => b.Name == "RigToneBrowser").Bounds.Height > 100);
                }
                else
                {
                    Assert.True(view.FindControl<Border>("RigDockBody")!.Bounds.Height > 100);
                }

                var laneRows = view.GetVisualDescendants().OfType<Border>().Count(b => b.Classes.Contains("FireLaneRow"));
                Assert.Equal(lanes == 1 ? 0 : lanes, laneRows);

                var dir = Environment.GetEnvironmentVariable("DOTCHARTER_RIG_CAPTURE_DIR");
                if (UiSweepRunner.CaptureEnabled && !string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                    using var frame = window.CaptureRenderedFrame();
                    Assert.NotNull(frame);
                    frame.Save(Path.Combine(dir, $"app-{state}.png"), new PngBitmapEncoderOptions());
                }
            }
            finally { window.Close(); }
        }, TestContext.Current.CancellationToken);
    }
}
