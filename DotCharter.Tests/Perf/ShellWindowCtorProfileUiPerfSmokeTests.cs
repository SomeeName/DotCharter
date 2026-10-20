using System.Diagnostics;
using Avalonia.Markup.Xaml.Styling;
using DotCharter.UI.Views;

namespace DotCharter.Tests.Perf;

/// <summary>
///     Where <c>windowCtor</c> — the largest remaining cold-start phase, ~1.9 s
///     median on a four-agent box with r2r=yes (measured at v0.48) —
///     actually goes. LOGGED, never asserted; Debug + JIT, so read the SHAPE.
///     <para>
///         Three numbers: a COLD <c>new MainWindow()</c> (the compiled XAML of
///         the shell's ~196 elements, the window-scope
///         <c>EditorWorkspaceStyles</c> include, the layout controller, the
///         focus guard), a WARM second construction (what is left once the
///         JIT and the first style parse are paid — the steady-state cost), and
///         the window-scope style include loaded on its own.
///     </para>
///     <para>
///         Measured 2026-08-29 (Debug, headless, r2r=no — see the class that
///         reads this for the numbers). The gap between cold and warm is JIT +
///         first-parse; a published R2R build pays only the warm side plus the
///         one-time style parse, which is why a dev build's cold number is not
///         the product's.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class ShellWindowCtorProfileUiPerfSmokeTests(ITestOutputHelper output)
{
    [Fact]
    public async Task WindowConstruction_ColdAndWarm_IsLogged()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var styleWatch = Stopwatch.StartNew();
            var include = new StyleInclude(new Uri("avares://DotCharter/"))
            {
                Source = new Uri("avares://DotCharter/Views/Editor/EditorWorkspaceStyles.axaml")
            };
            _ = include.Loaded;
            styleWatch.Stop();

            var cold = Stopwatch.StartNew();
            var first = new MainWindow();
            cold.Stop();

            var warm = Stopwatch.StartNew();
            var second = new MainWindow();
            warm.Stop();

            output.WriteLine(
                $"new MainWindow(): cold {cold.Elapsed.TotalMilliseconds:0} ms, warm {warm.Elapsed.TotalMilliseconds:0} ms; " +
                $"EditorWorkspaceStyles include alone {styleWatch.Elapsed.TotalMilliseconds:0} ms. " +
                $"(Debug, headless, r2r=no: the cold-warm gap is JIT and first-parse, not product cost.)");

            // Where the elements ARE: every UserControl / DockControl in the built
            // tree with its own descendant count, biggest first.
            var all = Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(first).ToList();
            output.WriteLine($"  MainWindow: {all.Count} logical descendants in total");
            var heavy = all
                .Where(l => l is Avalonia.Controls.UserControl or Dock.Avalonia.Controls.DockControl)
                .Select(l => (Name: l.GetType().Name + (l is Avalonia.Controls.Control c && c.Name != null ? "#" + c.Name : ""),
                    Count: Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(l).Count()))
                .OrderByDescending(t => t.Count)
                .Take(25);
            foreach (var (name, count) in heavy) output.WriteLine($"    {name}: {count}");

            // The views MainWindow.axaml instantiates INLINE, each cold then warm,
            // so the deferrable share of the constructor is visible.
            foreach (var (name, make) in new (string, Func<Avalonia.Controls.Control>)[]
                     {
                         ("MainMenuView", () => new DotCharter.UI.Views.Shell.MainMenuView()),
                         ("ToolbarView", () => new DotCharter.UI.Views.Shell.ToolbarView()),
                         ("Preview3DView", () => new DotCharter.UI.Views.Editor.Preview3DView()),
                         ("AmpFxRigView", () => new DotCharter.Tone.UI.Views.PlayMode.AmpFxRigView()),
                         ("DialogContainerView", () => new DotCharter.UI.Views.Shell.DialogContainerView()),
                         ("StatusBar", () => new DotCharter.UI.Views.Shell.StatusBar()),
                         ("CommandPaletteView", () => new DotCharter.UI.Views.CommandPalette.CommandPaletteView()),
                         // …and the custom-drawn / docking controls the shell declares itself.
                         ("DockControl", () => new Dock.Avalonia.Controls.DockControl()),
                         ("ChartRenderer", () => new DotCharter.UI.Controls.ChartRenderer()),
                         ("TimelineGutterControl", () => new DotCharter.UI.Controls.TimelineGutterControl()),
                         ("MinimapOverviewControl", () => new DotCharter.UI.Controls.MinimapOverviewControl()),
                         ("MarqueeSelectionOverlay", () => new DotCharter.UI.Controls.MarqueeSelectionOverlay())
                     })
            {
                var c = Stopwatch.StartNew();
                var a = make();
                c.Stop();
                var w = Stopwatch.StartNew();
                var b = make();
                w.Stop();
                var count = Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(a).Count();
                output.WriteLine($"  {name}: cold {c.Elapsed.TotalMilliseconds:0} ms, warm {w.Elapsed.TotalMilliseconds:0} ms, {count} logical descendants");
                _ = b;
            }

            // Neither window is shown; drop them so the keyboard-routing class's
            // one-window invariant is unaffected (it counts its own builds).
            first.Content = null;
            second.Content = null;
        }, CancellationToken.None);
    }
}
