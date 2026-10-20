using System.Globalization;
using System.Text.RegularExpressions;
using DotCharter.UI.Views.Shell;

namespace DotCharter.Tests.UI;

/// <summary>
///     The splitter above the inspector may not squeeze the editor's top area below what it refuses to
///     be: dragging it up past a flat 320 px made the timeline (minimum 360) paint over the song map
///     and run on under the inspector (owner report, 2026-09-23).
/// </summary>
public sealed class TimelineAreaMinHeightTests
{
    [Fact]
    public void TheMinimum_CoversTheTimeline_TheSongMap_AndTheSheetStrip()
    {
        Assert.Equal(360 + 52, TimelineAreaMinHeight.For(sheetVisible: false, songMapShown: true, gameplayPresentation: false));
        Assert.Equal(360, TimelineAreaMinHeight.For(false, songMapShown: false, gameplayPresentation: false));
        Assert.Equal(360 + 52 + SheetStripHeightPolicy.MinPx + 6, TimelineAreaMinHeight.For(true, true, false));
        Assert.Equal(0, TimelineAreaMinHeight.For(true, true, gameplayPresentation: true));
    }

    /// <summary>The constants restate the markup; a change to either side has to move the other.</summary>
    [Fact]
    public void TheConstants_MatchTheMarkupTheyRestate()
    {
        var window = MainWindowSource.ReadAllText();

        var sheet = window[window.IndexOf("x:Name=\"SheetSplitGrid\"", StringComparison.Ordinal)..];
        var timelineRow = Regex.Match(sheet, @"<RowDefinition Height=""\*"" MinHeight=""(?<px>\d+)"" />");
        Assert.True(timelineRow.Success, "the timeline row no longer states its minimum");
        Assert.Equal(TimelineAreaMinHeight.TimelineMinPx, double.Parse(timelineRow.Groups["px"].Value, CultureInfo.InvariantCulture));

        var map = Regex.Match(window, @"Classes=""MinimapFrame""\s+Height=""(?<px>\d+)""");
        Assert.True(map.Success, "the song map strip no longer states its height");
        Assert.Equal(TimelineAreaMinHeight.SongMapPx, double.Parse(map.Groups["px"].Value, CultureInfo.InvariantCulture));

        var layout = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Shell", "MainWindowShellLayout.cs")));
        Assert.Contains("TimelineAreaMinHeight.For(", layout, StringComparison.Ordinal);
    }
}
