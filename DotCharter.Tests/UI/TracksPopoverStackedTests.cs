using System.Text.RegularExpressions;

namespace DotCharter.Tests.UI;

/// <summary>
///     With one arrangement the Tracks popover said "add a second arrangement" twice: once in the
///     Multi-Track card and again under "Stacked arrangements · Unavailable". The stacked section
///     now shows only when there is something to stack (live capture, 2026-09-24).
/// </summary>
public sealed class TracksPopoverStackedTests
{
    [Fact]
    public void TheStackedSection_ShowsOnlyWithASecondArrangement()
    {
        var markup = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Shell", "Popovers", "TracksPopoverView.axaml")));
        var section = Regex.Match(markup, @"<Border[^>]*x:Name=""StackedTracksSection""[^>]*>");
        Assert.True(section.Success, "the Stacked section lost its name; re-read this test");
        Assert.Contains(@"IsVisible=""{Binding MultiTrackOverlay.HasOtherArrangements}""", section.Value, StringComparison.Ordinal);

        // The Multi-Track card is what explains the one-arrangement case now.
        Assert.Contains(@"IsVisible=""{Binding !MultiTrackOverlay.HasOtherArrangements}""", markup, StringComparison.Ordinal);
        Assert.Contains("Loc Toolbar_MultiTrack_EmptyHint", markup, StringComparison.Ordinal);
    }
}
