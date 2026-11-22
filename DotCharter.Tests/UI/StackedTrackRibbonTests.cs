using System.Text.Json;
using DotCharter.Services.Editing.Tracks;
using DotCharter.UI.Editing.Tracks;

namespace DotCharter.Tests.UI;

/// <summary>Song-wide lyrics occupy the active timeline's ribbon and never a stacked track's ribbon.</summary>
public sealed class StackedTrackRibbonTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void StackedRibbonsGiveLyricsNoHeight_RegardlessOfRestoredVisibility(bool lyrics, bool autoHide, bool compact)
    {
        var visibility = JsonSerializer.Deserialize<RibbonVisibility>(lyrics ? "{\"ShowLyrics\":true}" : "{\"ShowLyrics\":false}")!;
        visibility.AutoHideEmpty = autoHide;
        visibility.CompactRows = compact;
        var content = new RibbonContent(2, true, true, true, true);

        var stacked = RibbonLaneRules.Compose(visibility, content, isStacked: true);
        var active = RibbonLaneRules.Compose(visibility, content);

        Assert.False(stacked.ShowLyrics);
        Assert.Equal(active.Lanes & ~RibbonLanes.Lyrics, stacked.Lanes);
        Assert.Equal(lyrics, active.ShowLyrics);
        Assert.Equal(lyrics ? active.RowPitch : 0f, active.TotalHeight - stacked.TotalHeight);
        Assert.Equal(lyrics, visibility.ShowLyrics);
    }

    [Fact]
    public void ShowAllLanesCannotRestoreLyricsToAStackedRibbon()
    {
        var visibility = new RibbonVisibility { ShowLyrics = false };
        visibility.ShowAllLanes();
        var content = new RibbonContent(2, true, true, true);
        Assert.True(RibbonLaneRules.Compose(visibility, content).ShowLyrics);
        Assert.False(RibbonLaneRules.Compose(visibility, content, isStacked: true).ShowLyrics);
    }
}
