using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.UI.Views.Editor;
using DotCharter.UI.Views.Editor.Inspector;
using DotCharter.UI.Views.Shell;

namespace DotCharter.Tests.Perf;

/// <summary>
///     #175: the Mixer, Song and Phrases tabs still took 150-320 ms on their first
///     visit after a project opened (the app's own surface-timing log, owner's
///     machine). The startup prewarm lays every hidden tab out ONCE, usually before a
///     project exists; whatever the project then changes behind a hidden tab — its
///     lists, and the visibility / text / enabled state a project or arrangement
///     switch flips — was laid out on the click. Every tab panel now has a
///     <see cref="HiddenSurfaceRewarmer" />, and a project or arrangement switch asks
///     the hidden ones for one background re-realization.
/// </summary>
[Collection("HeadlessUI")]
public sealed class InspectorTabRewarmUiPerfSmokeTests
{
    private const double W = 1200;
    private const double H = 820;

    [Fact]
    public Task AChangeBehindAHiddenTab_IsLaidOutInTheBackground_BeforeTheClick() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var inspector = new InspectorView();
            var grid = inspector.FindControl<Grid>("InspectorTabPanels");
            Assert.NotNull(grid);
            var panels = grid!.Children.OfType<Control>().ToList();
            for (var i = 1; i < panels.Count; i++) panels[i].IsVisible = false;
            Assert.Equal(panels.Count, inspector.TabRewarmers.Count);

            var window = new Window { Width = W, Height = H, Content = inspector };
            window.Show();
            try
            {
                window.UpdateLayout();
                // The startup prewarm, before any project is open — each tab and then
                // its chunked sections, a chunk per turn, as the drain does it (#253).
                foreach (var panel in inspector.HiddenSurfaces())
                {
                    HiddenSubtreePrewarmer.Realize(panel, new Size(W, H));
                    while (HiddenSubtreePrewarmer.RealizeNextChunk(panel, new Size(W, H))) { }
                }

                foreach (var tab in new Control[]
                         {
                             panels.OfType<MixerInspectorView>().Single(),
                             panels.OfType<PhrasesInspectorView>().Single(),
                         })
                {
                    // What a project switch does behind the hidden tab: content changes.
                    var text = tab.GetVisualDescendants().OfType<TextBlock>()
                        .First(t => t.IsVisible && t.Bounds.Width > 0);
                    text.Text = new string('W', 60);
                    Assert.True(StaleNodes(tab).Count > 0,
                        $"changing a text under the hidden {tab.GetType().Name} left nothing to lay out — the harness proves nothing");

                    inspector.RequestTabRewarm();
                    Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);

                    Assert.False(tab.IsVisible, "the harness showed the tab — nothing here would mean anything");
                    var stale = StaleNodes(tab);
                    Assert.True(stale.Count == 0,
                        $"{stale.Count} node(s) under the hidden {tab.GetType().Name} still need a layout after the " +
                        $"background rewarm ({string.Join(", ", stale.Select(s => s.GetType().Name).Distinct())}) — " +
                        "the click on the tab pays for them");
                    Assert.True(inspector.TabRewarmers[panels.IndexOf(tab)].Rewarms >= 1,
                        $"the {tab.GetType().Name} rewarmer never re-realized anything");
                }
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);

    /// <summary>The trigger in the app: a project or arrangement switch reaches
    /// <see cref="InspectorView.RequestTabRewarm" /> through the singleton
    /// <c>ProjectState</c>, subscribed only while the pane is attached.</summary>
    [Fact]
    public void AProjectOrArrangementSwitch_AsksTheHiddenTabsForARewarm()
    {
        var source = SourceText.StripCommentLines(File.ReadAllText(Path.Combine(TestPaths.RepoRoot(),
            "DotCharter.UI", "Views", "Editor", "InspectorView.axaml.cs")));
        Assert.Contains("_watchedState?.PropertyChanged += OnProjectStateChanged;", source);
        Assert.Contains("_watchedState?.PropertyChanged -= OnProjectStateChanged;", source);
        Assert.Contains("nameof(ProjectState.ActiveProject) or nameof(ProjectState.ActiveArrangement)", source);
        Assert.Contains("RequestTabRewarm();", source);
        Assert.Contains("WatchProjectState(null);", source);
    }

    /// <summary>What the real show would still have to lay out: stale nodes reached
    /// through visible elements only.</summary>
    private static List<Layoutable> StaleNodes(Visual root)
    {
        var stale = new List<Layoutable>();

        void Walk(Visual parent)
        {
            foreach (var child in parent.GetVisualChildren())
            {
                if (!child.IsVisible) continue;
                if (child is Layoutable { IsMeasureValid: false } or Layoutable { IsArrangeValid: false })
                    stale.Add((Layoutable)child);
                Walk(child);
            }
        }

        Walk(root);
        return stale;
    }
}
