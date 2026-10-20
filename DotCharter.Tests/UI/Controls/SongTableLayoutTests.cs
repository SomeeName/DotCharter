using DotCharter.UI.Controls;

namespace DotCharter.Tests.UI.Controls;

/// <summary>
///     #212: the song tables' column layout, pure. The song column gets the space first; the fixed
///     columns give way one at a time in the declared order while it is under its floor, never
///     more than that and never back at a narrower width. The headless half (what is drawn, in
///     both hosts, at 1280 / 1600 / 1920) is <c>Perf/SongTableColumnsUiPerfSmokeTests</c>.
/// </summary>
public sealed class SongTableLayoutTests
{
    public static TheoryData<string> Layouts() => ["Library", "SongSelect", "PlayModeSongList"];

    /// <summary>
    ///     The row widths the hosts give the panel, from their markup: a 1280 px window's Library
    ///     in the charting context is 1204 px wide, minus 2 × 28 margins, the 18 px gutter and the
    ///     316 px detail rail, minus the row's 2 × 18 inset = 778. Full-bleed (playing) it is 854.
    ///     Song Select at 1280: 1280 − 2 × 28 − 18 − 340 − 2 × 18 = 830. The live Play Mode list
    ///     (#231) at 1280: 1280 − the 420 px detail rail − the row's 2 × 22 inset = 816.
    /// </summary>
    [Theory]
    [InlineData("Library", 778, new[] { "year", "status" })]
    [InlineData("Library", 854, new[] { "year", "status" })]
    [InlineData("Library", 1098, new string[0])]
    [InlineData("Library", 1418, new string[0])]
    [InlineData("SongSelect", 830, new[] { "time", "difficulty" })]
    [InlineData("SongSelect", 1150, new string[0])]
    [InlineData("SongSelect", 1470, new string[0])]
    [InlineData("PlayModeSongList", 816, new[] { "time" })]
    [InlineData("PlayModeSongList", 1136, new string[0])]
    [InlineData("PlayModeSongList", 1456, new string[0])]
    public void AtTheHostsRowWidths_TheseColumnsGiveWay(string name, double width, string[] gone)
    {
        var layout = Layout(name);
        var shown = layout.ShownAt(width);
        var hidden = layout.Columns.Where((_, i) => !shown[i]).Select(c => c.Name).ToArray();
        Assert.Equal(gone, hidden);
        Assert.True(layout.SongWidth(width, shown) >= SongTableLayout.SongFloor,
            $"{name} at {width}: the song column has {layout.SongWidth(width, shown):0.#} px");
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public void TheSongColumnKeepsItsFloor_WhileAnythingCanStillGiveWay(string name)
    {
        var layout = Layout(name);
        for (var width = 200.0; width <= 2600; width += 1)
        {
            var shown = layout.ShownAt(width);
            var song = layout.SongWidth(width, shown);
            var anyLeft = layout.Columns.Where((c, i) => c.GivesWay > 0 && shown[i]).Any();
            Assert.True(song >= SongTableLayout.SongFloor || !anyLeft,
                $"{name} at {width}: the song column has {song:0.#} px while a column that could give way is still shown");
        }
    }

    /// <summary>No column leaves that did not have to: putting the last one to go back would take the song column under its floor.</summary>
    [Theory]
    [MemberData(nameof(Layouts))]
    public void NoColumnGivesWayItDidNotHaveTo(string name)
    {
        var layout = Layout(name);
        for (var width = 200.0; width <= 2600; width += 1)
        {
            var shown = layout.ShownAt(width);
            var gone = Enumerable.Range(0, layout.Columns.Count).Where(i => !shown[i]).ToList();
            if (gone.Count == 0) continue;
            var last = gone.MaxBy(i => layout.Columns[i].GivesWay);
            var restored = (bool[])shown.Clone();
            restored[last] = true;
            Assert.True(layout.SongWidth(width, restored) < SongTableLayout.SongFloor,
                $"{name} at {width}: '{layout.Columns[last].Name}' gave way although the song column would keep its floor with it");
        }
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public void AColumnThatLeftNeverComesBackNarrower_AndTheyLeaveInTheirDeclaredOrder(string name)
    {
        var layout = Layout(name);
        var ranks = layout.Columns.Where(c => c.GivesWay > 0).Select(c => c.GivesWay).ToList();
        Assert.Equal(ranks.Count, ranks.Distinct().Count());
        Assert.Equal(0, layout.Columns[layout.SongColumn].GivesWay);

        var wider = layout.ShownAt(double.PositiveInfinity);
        Assert.All(wider, Assert.True);
        for (var width = 2600.0; width >= 200; width -= 1)
        {
            var shown = layout.ShownAt(width);
            for (var i = 0; i < shown.Length; i++)
                Assert.False(shown[i] && !wider[i], $"{name} at {width}: '{layout.Columns[i].Name}' came back at a narrower width");

            // Whatever has gone, everything that gives way EARLIER has gone too.
            var gone = Enumerable.Range(0, shown.Length).Where(i => !shown[i]).Select(i => layout.Columns[i].GivesWay).ToList();
            if (gone.Count > 0)
                Assert.Equal(Enumerable.Range(1, gone.Max()), gone.Order());
            wider = shown;
        }
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public void ThePlaces_TileTheWidth_WithOneGapBetweenShownColumns(string name)
    {
        var layout = Layout(name);
        foreach (var width in new[] { 778.0, 830, 854, 1098, 1418, 2400 })
        {
            var shown = layout.ShownAt(width);
            var places = layout.Place(width, shown);
            var visible = Enumerable.Range(0, places.Length).Where(i => shown[i]).ToList();
            Assert.Equal(0, places[visible[0]].Left);
            for (var k = 1; k < visible.Count; k++)
            {
                var (left, previous) = (places[visible[k]].Left, places[visible[k - 1]]);
                Assert.Equal(previous.Left + previous.Width + SongTableLayout.Gap, left, 3);
            }

            var end = places[visible[^1]];
            Assert.Equal(width, end.Left + end.Width, 3);
            Assert.All(visible.Where(i => i != layout.SongColumn), i => Assert.Equal(layout.Columns[i].Width, places[i].Width));
            Assert.All(Enumerable.Range(0, places.Length).Where(i => !shown[i]), i => Assert.Equal(0, places[i].Width));
        }
    }

    private static SongTableLayout Layout(string name) => name switch
    {
        "Library" => SongTableLayout.Library,
        "SongSelect" => SongTableLayout.SongSelect,
        "PlayModeSongList" => SongTableLayout.PlayModeSongList,
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };
}
