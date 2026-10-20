using DotCharter.UI.Views.Shell;

namespace DotCharter.Tests.UI;

/// <summary>
///     The toolbar overflow affordance (P2-16). On a narrow window the core
///     rail's trailing groups used to scroll out of reach with nothing saying
///     so; the » button appears exactly while the rail is narrower than its
///     content and opens <c>OverflowPopoverView</c> — second doors onto the
///     rail's own commands.
/// </summary>
public sealed class ToolbarOverflowTests
{
    [Theory]
    [InlineData(1000, 800, true)]   // content wider than the viewport — clipped
    [InlineData(800, 800, false)]   // fits exactly
    [InlineData(800.4, 800, false)] // sub-pixel jitter must not flicker the button
    [InlineData(400, 1200, false)]  // wide window
    public void RailOverflows_ExactlyWhenContentExceedsTheViewport(
        double extent, double viewport, bool expected)
        => Assert.Equal(expected, ToolbarView.RailOverflows(extent, viewport));

    /// <summary>
    ///     Source lint: the overflow button starts INVISIBLE (only the
    ///     scroll-state watcher may show it — an always-on » would claim
    ///     clipping that is not happening), and it hosts the popover VIEW, not
    ///     inline markup (the v0.43 pedalboard lesson).
    /// </summary>
    [Fact]
    public void TheOverflowButton_StartsHidden_AndHostsTheRealPopoverView()
    {
        var markup = UiSourceTree.ReadAllText(RepoPath(UiSourceTree.Resolve(Path.Combine(
            "DotCharter.UI", "Views", "Shell", "ToolbarView.axaml"))));

        var buttonStart = markup.IndexOf("x:Name=\"ToolbarOverflowButton\"", StringComparison.Ordinal);
        Assert.True(buttonStart >= 0, "the toolbar overflow button is gone");

        var buttonBlock = markup.Substring(buttonStart, Math.Min(1200, markup.Length - buttonStart));
        Assert.Contains("IsVisible=\"False\"", buttonBlock);
        Assert.Contains("OverflowPopoverView", buttonBlock);
    }

    /// <summary>The overflow's group rows route through RequestPopover, and the
    /// ToolbarView side must know every enum member it can be asked for —
    /// Tempo joined the enum with the overflow and would otherwise fall into
    /// the null arm and silently open nothing.</summary>
    [Fact]
    public void EveryTransportPopover_HasAButtonArmInTheToolbar()
    {
        var source = UiSourceTree.ReadAllText(RepoPath(UiSourceTree.Resolve(Path.Combine(
            "DotCharter.UI", "Views", "Shell", "ToolbarView.axaml.cs"))));

        foreach (var member in Enum.GetNames<DotCharter.UI.ViewModels.Shell.TransportPopover>())
            Assert.Contains($"TransportPopover.{member} =>", source);
    }

    private static string RepoPath(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "DotCharter.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return Path.Combine(dir!, relative);
    }
}
