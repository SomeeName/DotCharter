// Where the dialog grammar comes FROM: resources pulled through DynamicResource, DialogStyles.axaml included once by the overlay host and only by the three outside it, and the container materializing every dialog exactly once for the prewarm.

using System.Text.RegularExpressions;
using DotCharter.UI;
using DotCharter.UI.ViewModels.Shell;
using DotCharter.UI.Views.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

public sealed partial class DialogConstructionUiPerfSmokeTests
{
    // ── source lints (no session needed) ──────────────────────────────────

    /// <summary>
    ///     Icon geometry in a dialog must come through
    ///     <c>{DynamicResource IconX}</c>; a <c>{StaticResource}</c> throws the
    ///     moment the view is built without the app-scoped dictionary in scope,
    ///     and <c>ViewLocator</c> then swallows that into a "Not Found: …"
    ///     placeholder. A source lint because there is no runtime signal once
    ///     the app IS in scope.
    /// </summary>
    [Fact]
    public void DialogViews_PullResourcesThroughDynamicResource()
    {
        var offenders = new List<string>();
        foreach (var file in DialogAxamlFiles())
        {
            foreach (Match m in Regex.Matches(UiSourceTree.ReadAllText(file), @"\{StaticResource\s+(?<key>[A-Za-z0-9_]+)"))
                offenders.Add($"{Path.GetFileName(file)}: {{StaticResource {m.Groups["key"].Value}}}");
        }

        Assert.True(offenders.Count == 0,
            "Dialog views must resolve resources dynamically — a StaticResource throws at construction and " +
            "ViewLocator renders the dialog as a 'Not Found:' placeholder:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    ///     The grammar is included ONCE, by the overlay host. A per-view
    ///     StyleInclude would re-instantiate the whole style set for every one
    ///     of ~30 dialogs (the same mistake the old duplicated Theme.axaml
    ///     include made). The one dialog that is NOT a child of that host is the
    ///     sanctioned exception and is listed here by name.
    /// </summary>
    [Fact]
    public void DialogStyles_AreIncludedOnceByTheOverlayHost_AndOnlyByTheWindowOutsideIt()
    {
        // The real include, not a mention: every converted dialog names the file
        // in a comment ("Grammar: Views/Dialogs/DialogStyles.axaml"), which a
        // substring search would report as a stray include.
        const string include = "StyleInclude Source=\"avares://DotCharter/Views/Dialogs/DialogStyles.axaml\"";
        var uiRoot = Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI");

        var host = UiSourceTree.ReadAllText(Path.Combine(uiRoot, "Views", "Shell", "DialogContainerView.axaml"));
        Assert.Contains(include, host, StringComparison.Ordinal);

        string[] sanctioned =
        [
            // Its own top-level Window, and deliberately NON-MODAL so the
            // dashboard can be watched while the app is used.
            "DiagnosticsWindow.axaml"
        ];

        var strays = DialogAxamlFiles()
            .Where(f => !sanctioned.Contains(Path.GetFileName(f), StringComparer.Ordinal))
            .Where(f => UiSourceTree.ReadAllText(f).Contains(include, StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(strays.Count == 0,
            "These dialogs re-include DialogStyles.axaml even though DialogContainerView already does:\n  " +
            string.Join("\n  ", strays));
    }

    /// <summary>
    ///     The prewarm contract: <c>HiddenSurfaces()</c> builds every deferred
    ///     dialog view ONCE and hands back its closed scrim.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Two failures this catches, both silent. <b>Duplication:</b> the
    ///         prewarm and the first open both materialize, so a missing
    ///         re-entrancy guard would leave two views over one view model and the
    ///         dialog would render twice (the topmost one bound to the same state,
    ///         so it looks like a rendering bug, not a lifetime one). <b>Nothing
    ///         at all:</b> if the surfaces come back empty the prewarm is a no-op
    ///         and every first open still pays its 2-102 ms of layout — measured
    ///         at ~690 ms across the set in <c>LocalOnly_ViewInflationProbe</c>.
    ///     </para>
    ///     <para>
    ///         Needs the real app session for the shell view model, hence a
    ///         smoke test rather than a gate test.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task DialogContainer_MaterializesEveryDialogOnce_ForThePrewarm()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var host = new DialogContainerView
            {
                DataContext = App.Services?.GetRequiredService<MainWindowViewModel>()
            };

            var eager = host.DialogPanel.Children.Count;
            var first = host.HiddenSurfaces();
            var afterFirst = host.DialogPanel.Children.Count;

            Assert.True(afterFirst > eager,
                "HiddenSurfaces() materialized nothing — the dialog prewarm would be a no-op.");
            Assert.NotEmpty(first);

            // Every returned surface is a CLOSED dialog's own scrim.
            Assert.All(first, s => Assert.False(s.IsVisible));

            // ...and asking again neither rebuilds nor duplicates.
            var second = host.HiddenSurfaces();
            Assert.Equal(afterFirst, host.DialogPanel.Children.Count);
            Assert.Equal(first.Count, second.Count);
            Assert.All(first.Zip(second), p => Assert.Same(p.First, p.Second));

            var byViewModel = host.DialogPanel.Children
                .Where(c => c.DataContext != null)
                .GroupBy(c => c.DataContext!)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key.GetType().Name)
                .ToList();
            Assert.True(byViewModel.Count == 0,
                "These dialog view models ended up with more than one view:\n  " + string.Join("\n  ", byViewModel));
        }, CancellationToken.None);
    }
}
