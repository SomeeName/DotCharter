using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using DotCharter.Localization;
using DotCharter.UI.Controls;
using DotCharter.UI.Views.Editor;

namespace DotCharter.Tests.Perf;

/// <summary>
///     Through the real <see cref="InspectorView" /> in a themed window. The strip: the
///     selection tabs (Note, Chord shapes) come first, the divider stands between them and the song
///     tabs, and the tab is called Note. (Since #751 no tab wears the section rail: the Song and
///     Lyrics tabs are design D's cards and the Mixer has its section strip.)
/// </summary>
[Collection("HeadlessUI")]
public sealed class InspectorGroupedTabsUiPerfSmokeTests
{
    [Theory]
    [InlineData(1100)]
    [InlineData(720)]
    public async Task TheStrip_PutsTheSelectionTabsFirst_AndTheDividerBeforeSong(int width) =>
        await HeadlessSessionFixture.Dispatch(() => InspectorNarrowWidthUiPerfSmokeTests.WithPane(width, view =>
        {
            var row = view.FindControl<OverflowTabStrip>("InspectorTabRow")!;
            var tabs = row.ShownTabs.OfType<ToggleButton>().ToList();
            var tags = tabs.Select(t => (string)t.Tag!).ToList();
            Assert.Equal(["0", "7", "2", "3", "4", "5", "6", "1"], tags.Concat(row.OverflowTabs.OfType<ToggleButton>().Select(t => (string)t.Tag!)));

            // the first tab says Note and keeps its tooltip
            Assert.Equal(LocalizationService.Instance["Inspector_Tab_Note"], AutomationProperties.GetName(tabs[0]));
            Assert.False(string.IsNullOrWhiteSpace(ToolTip.GetTip(tabs[0]) as string));

            var divider = row.DividerBounds;
            Assert.NotNull(divider);
            var chord = tabs.Single(t => (string)t.Tag! == "7");
            var song = tabs.Single(t => (string)t.Tag! == "2");
            Assert.True(divider!.Value.Height > 4 && divider.Value.Width >= 1, $"the divider laid out as {divider}");
            Assert.True(divider.Value.X >= chord.Bounds.Right - 0.5 && divider.Value.Right <= song.Bounds.X + 0.5,
                $"the divider {divider} is not between Chord shapes ({chord.Bounds}) and Song ({song.Bounds})");
            Assert.True(song.Bounds.X - chord.Bounds.Right >= row.GroupGap - 0.5, "the group gap was not kept before Song");
        }), CancellationToken.None);
}
