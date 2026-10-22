using Avalonia.Controls;
using DotCharter.Services.Infrastructure.Features;
using DotCharter.UI.Views.Shell.Menus;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The menu half of the public-build switch (#824): the real File menu is
///     built, its contested row (Audio from YouTube, #820) is there with the switch on and
///     REMOVED (not disabled, not hidden) with it off. The compile constant cannot
///     flip in one run, so the off case calls <see cref="ContestedMenu.Prune" />
///     with <c>false</c> on the constructed menu — the same call the menu's own
///     constructor makes with <see cref="ContestedFeatures.Enabled" />.
/// </summary>
[Collection("HeadlessUI")]
public sealed class ContestedMenuUiPerfSmokeTests
{
    private static IEnumerable<MenuItem> AllRows(ItemsControl root)
    {
        foreach (var row in root.Items.OfType<MenuItem>())
        {
            yield return row;
            foreach (var inner in AllRows(row)) yield return inner;
        }
    }

    /// <summary>The rows marked with a CONTESTED feature; a package row (game-packages) is the plugin gate's, not this switch's.</summary>
    private static List<MenuItem> Marked(ItemsControl menu) =>
        [.. AllRows(menu).Where(r => ContestedFeatures.All.Any(f => f.Id == ContestedMenu.GetFeature(r)))];

    [Fact]
    public Task ContestedRows_ArePresentWhenOn_AndGoneWhenOff() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var menu = new FileMenu();
            var before = AllRows(menu).Count();

            // The default build has the switch on: the constructor kept the rows.
            Assert.Equal(ContestedFeatures.Enabled, Marked(menu).Count > 0);
            Assert.NotEmpty(Marked(menu));
            Assert.All(Marked(menu), r => Assert.Contains(ContestedMenu.GetFeature(r), ContestedFeatures.All.Select(f => f.Id)));
            Assert.Contains(Marked(menu), r => ContestedMenu.GetFeature(r) == ContestedFeatures.YoutubeImportId);

            Assert.Equal(0, ContestedMenu.Prune(menu, enabled: true));
            Assert.Equal(before, AllRows(menu).Count());

            var removed = ContestedMenu.Prune(menu, enabled: false);

            Assert.True(removed > 0);
            Assert.Empty(Marked(menu));
            Assert.Equal(before - removed, AllRows(menu).Count());
            Assert.True(AllRows(menu).Any(), "the other rows of the menu must survive");
        }, CancellationToken.None);
}
