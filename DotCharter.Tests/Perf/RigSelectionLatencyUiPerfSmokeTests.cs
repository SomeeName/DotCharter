using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.VisualTree;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using DotCharter.Tone.UI.Views.PlayMode;
using DotCharter.Localization;

namespace DotCharter.Tests.Perf;

/// <summary>
///     How long the rig screen takes to react to the gestures the owner makes
///     most: selecting a unit (the editor dock swaps to that unit's family
///     view), moving a unit, switching the routing mode and zooming. Every
///     number is LOGGED, never asserted — the default gate forbids wall-clock
///     assertions — but each gesture must still leave the screen laid out.
///     Set <c>DOTCHARTER_RIG_CAPTURE_DIR</c> to also write the table to
///     <c>rig-latency.txt</c> there.
/// </summary>
[Collection("HeadlessUI")]
public sealed class RigSelectionLatencyUiPerfSmokeTests(ITestOutputHelper output)
{
    [Fact]
    public async Task SelectingMovingAndZooming_LayOutAndAreLogged()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var metadata = new JuceDspChainService();
            metadata.LoadCatalog();
            var catalogue = metadata.Catalog;
            var chain = new FakeDspChainService(catalogue);
            using var rig = TestRigBuilders.Pedalboard(chain, new FakeDspTonePresetService(), LocalizationService.Instance);
            foreach (var id in new[] { "namamp", "cab", "overdrive", "delay", "reverb", "graphiceq", "compressor", "wah" })
                rig.AddGear(catalogue.Single(m => m.Id == id));
            var model = new AmpFxRigViewModel(rig);
            var view = new AmpFxRigView { DataContext = model };
            var window = new Window { Width = 1920, Height = 1080, Content = view };
            window.Show();
            var lines = new List<string>();
            try
            {
                for (var pass = 0; pass < 3; pass++) window.UpdateLayout();

                // two numbers per gesture: the synchronous reaction (view models,
                // bindings, view construction) and the layout pass that follows
                double Time(string what, Action act)
                {
                    var sw = Stopwatch.StartNew();
                    act();
                    var react = sw.Elapsed.TotalMilliseconds;
                    window.UpdateLayout();
                    sw.Stop();
                    lines.Add($"{what,-40} react {react,7:0.0} ms   layout {sw.Elapsed.TotalMilliseconds - react,7:0.0} ms");
                    return sw.Elapsed.TotalMilliseconds;
                }

                // pass 0 pays every family's first inflation; the warm passes are
                // what a player feels stepping through the chain, so their BEST
                // and WORST are both printed (GC pauses show as the worst)
                var warm = new Dictionary<int, List<double>>();
                for (var pass = 0; pass < 4; pass++)
                {
                    for (var i = 0; i < rig.Nodes.Count; i++)
                    {
                        var id = rig.Nodes[i].ModuleTypeId;
                        var ms = Time($"select #{i} {id} (pass {pass})", () => rig.SelectNodeAt(i));
                        if (pass > 0) (warm.TryGetValue(i, out var l) ? l : warm[i] = []).Add(ms);
                        Assert.Equal(id, model.DockUnit?.Node.ModuleTypeId);
                        Assert.True(view.FindControl<Border>("RigDockBody")!.Bounds.Height > 100);
                    }
                }
                // the host keeps ONE view per family, hidden when not in use
                var host = view.GetVisualDescendants().OfType<RigModuleEditorView>().Single();
                var familyViews = host.FindControl<Panel>("RigEditorFamilyHost")!.Children;
                var families = rig.Nodes.Select(n => new RigDockUnit(n).Family).Distinct().Count();
                Assert.Equal(families, familyViews.Count);
                Assert.Single(familyViews, c => c.IsVisible);
                Assert.Same(host.ActiveFamilyView, familyViews.Single(c => c.IsVisible));
                Assert.All(familyViews.Where(c => !c.IsVisible), c => Assert.Null(c.DataContext));

                foreach (var (i, samples) in warm)
                    lines.Add($"{"warm select #" + i + " " + rig.Nodes[i].ModuleTypeId,-40} best {samples.Min(),7:0.0} ms   worst {samples.Max(),7:0.0} ms");

                // the dock unit alone, without any view
                for (var i = 0; i < 3; i++)
                {
                    var node = rig.Nodes[i];
                    var sw = Stopwatch.StartNew();
                    _ = new RigDockUnit(node, null, rig.Controls, rig.MidiControls, rig.ModuleMeters, rig);
                    lines.Add($"{"new RigDockUnit " + node.ModuleTypeId,-40} {sw.Elapsed.TotalMilliseconds,8:0.0} ms");
                }

                Time("move #2 to the end", () => rig.MoveTo(rig.Nodes[2], rig.Nodes.Count - 1));
                Time("move it back", () => rig.MoveTo(rig.Nodes[^1], 2));
                Time("routing → triple", () => rig.RoutingSplit3Command.Execute(null));
                Time("select the splitter", () => rig.SelectNodeAt(rig.Nodes.ToList().FindIndex(n => n.IsSplitter)));
                Time("routing → series", () => rig.RoutingSerialCommand.Execute(null));
                Time("zoom in", () => rig.ZoomInCommand.Execute(null));
                Time("zoom out", () => rig.ZoomOutCommand.Execute(null));
                Time("bypass #0", () => rig.Nodes[0].Bypassed = true);
                Time("select #0 again", () => rig.SelectNodeAt(0));

                var table = string.Join('\n', lines);
                output.WriteLine(table);
                var dir = Environment.GetEnvironmentVariable("DOTCHARTER_RIG_CAPTURE_DIR");
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(Path.Combine(dir, "rig-latency.txt"), table);
                }
            }
            finally { window.Close(); }
        }, TestContext.Current.CancellationToken);
    }
}
