using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Library;
using DotCharter.Presentation.Fakes.Library;
using DotCharter.Presentation.Fakes.PlayMode;
using DotCharter.UI.Controls;
using DotCharter.Tone.UI.Controls.Fire;
using DotCharter.UI.Views.Library;
using DotCharter.UI.Views.PlayMode.Redesign;

namespace DotCharter.Tests.Perf;

/// <summary>
///     #212: the song tables of the Library and of Play Mode's Song Select give the SONG column
///     its floor at every window width, and every column caption starts where its values start.
///     <para>
///         At 1280 px the Library's Song column was 45 px ("Stran…", "Sym…") while 728 px of
///         fixed columns stayed mostly blank, and the right-aligned Year / Diff / Time captions
///         (label, then the funnel) started ~30 px left of their numbers. Both tables now lay
///         out through <see cref="SongTablePanel" />: the fixed columns give way in a declared
///         order below the floor, and the caption row and the rows share one
///         <see cref="SongTableLayout" />, so a caption sits over its column by construction.
///     </para>
///     <para>
///         Asserted on what is DRAWN, not on the panel's own numbers: the title cell's arranged
///         width, and the left edge of each caption's text against the left edge of every value
///         printed under it (the first glyph's x from the text layout, so a right-aligned value
///         in a wide cell cannot pass on its cell's bounds). A hidden caption's values must be
///         hidden too, and a shown column must have at least one value on screen, so the
///         fixture cannot make a column pass by printing nothing. Captions are relabelled with
///         the SHIPPED strings, which is what the release's de-DE / ja-JP re-run
///         (<c>$HeadlessLayoutFilter</c>) measures. <c>DOTCHARTER_UI_SWEEP_DUMP=1</c> writes a
///         PNG per case (<see cref="NarrowWidthLayout.Capture" />).
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class SongTableColumnsUiPerfSmokeTests
{
    private const double Tolerance = 2;

    private static readonly (int Width, int Height)[] Windows = [(1280, 720), (1600, 900), (1920, 1080)];

    /// <summary>The Library's own area: the charting context loses the 76 px activity rail and
    /// 68 px of title and status bar; the playing context is full-bleed under the 44 px back bar
    /// (the sizes <see cref="LibraryDetailRailUiPerfSmokeTests" /> uses).</summary>
    public static TheoryData<string, string, int, int> LibraryCases()
    {
        var data = new TheoryData<string, string, int, int>();
        foreach (var variant in new[] { "Full", "LongText" })
        {
            foreach (var (width, height) in Windows)
            {
                data.Add(variant, "charting", width - 76, height - 68);
                data.Add(variant, "playing", width, height - 44);
            }
        }

        return data;
    }

    public static TheoryData<string, int, int> SongSelectCases()
    {
        var data = new TheoryData<string, int, int>();
        foreach (var variant in new[] { "Default", "LongText" })
        {
            foreach (var (width, height) in Windows) data.Add(variant, width, height);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(LibraryCases))]
    public async Task Library_TheSongColumnKeepsItsFloor_AndEveryCaptionSitsOverItsValues(string variant, string context, int width, int height)
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var screen = (FakeLibraryScreen)FakeLibraryScreen.Variants.Single(v => v.Name == variant).Create();
            (ILibraryColumn Column, string Key, string Cell)[] columns =
            [
                (screen.SongColumn, "Dialog_SongBrowser_SortArtist", "RowTitle"),
                (screen.PathsColumn, "SongBrowser_Col_Paths", "RowPaths"),
                (screen.TuningColumn, "SongBrowser_Col_Tuning", "RowTuning"),
                (screen.YearColumn, "SongBrowser_Col_Year", "RowYear"),
                (screen.DifficultyColumn, "SongBrowser_Col_Difficulty", "RowDifficulty"),
                (screen.LengthColumn, "SongBrowser_Col_Time", "RowLength"),
                (screen.StatusColumn, "SongBrowser_Col_Status", "RowStatus")
            ];
            foreach (var (column, key, _) in columns) ((FakeLibraryColumn)column).Title = LocalizationService.Instance[key];
            ((FakeLibraryColumn)screen.TitleSort).Title = LocalizationService.Instance["Dialog_SongBrowser_SortTitle"];
            ((FakeLibraryColumn)screen.ArchiveSort).Title = LocalizationService.Instance["Dialog_SongBrowser_SortPack"];

            var what = $"Library {variant} {context} {width}x{height}";
            WithShown(new LibraryView { DataContext = screen }, width, height, $"library-{variant}-{context}-{width}x{height}", view =>
            {
                var rows = AssertTable(view, "LibraryColumnHeader", "LibraryRows", [.. columns.Select(c => (c.Column, c.Cell))], what);

                // The song column captions its three orders (#306): all of them drawn, inside its cell.
                var songSorts = NarrowWidthLayout.Named<StackPanel>(view, "LibrarySongSorts");
                var sortTexts = songSorts.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text)).ToList();
                foreach (var order in new[] { screen.SongColumn, screen.TitleSort, screen.ArchiveSort })
                    Assert.Contains(sortTexts, t => t.Text == order.Title && t.Bounds.Width > 1);
                var cellRight = NarrowWidthLayout.Span(songSorts, view).Right;
                var inkRight = sortTexts.Max(t => NarrowWidthLayout.Span(t, view).Right);
                Assert.True(inkRight <= cellRight + 0.5,
                    $"{what}: the song column's sort captions run to x={inkRight:0.#}, past its cell's edge at x={cellRight:0.#}");

                // A problem never goes unsaid: with the Status column given way, the row's mark carries it.
                var statusShown = Caption(NarrowWidthLayout.Named<Border>(view, "LibraryColumnHeader"), screen.StatusColumn).IsEffectivelyVisible;
                foreach (var row in rows.Where(r => r.DataContext is ILibraryRow { HasProblem: true }))
                {
                    var mark = Cell(row, "RowStatusMark");
                    Assert.True(mark.IsEffectivelyVisible == !statusShown,
                        $"{what}: a problem row's status mark is {(mark.IsEffectivelyVisible ? "shown" : "hidden")} while the Status column is {(statusShown ? "shown" : "hidden")}");
                }

                Assert.All(rows.Where(r => r.DataContext is ILibraryRow { HasProblem: false }),
                    row => Assert.False(Cell(row, "RowStatusMark").IsEffectivelyVisible, $"{what}: a status mark on a row with no problem"));

                // …and its filter (status, origin, lessons) stays reachable: the funnel stands in over the cover column.
                var funnel = NarrowWidthLayout.Named<ContentControl>(view, "LibraryStatusFunnel");
                Assert.True(funnel.IsEffectivelyVisible == !statusShown,
                    $"{what}: the Status funnel's stand-in is {(funnel.IsEffectivelyVisible ? "shown" : "hidden")} while the Status column is {(statusShown ? "shown" : "hidden")}");
                if (!statusShown)
                {
                    var button = Assert.Single(funnel.GetVisualDescendants().OfType<Button>(), b => b.Classes.Contains("FacetFunnel"));
                    Assert.True(button.IsEffectivelyVisible && button.Bounds is { Width: > 1, Height: > 1 }, $"{what}: the stand-in funnel laid out as {button.Bounds}");
                }
            });
        }, TestContext.Current.CancellationToken);
    }

    [Theory]
    [MemberData(nameof(SongSelectCases))]
    public async Task SongSelect_TheSongColumnKeepsItsFloor_AndEveryCaptionSitsOverItsValues(string variant, int width, int height)
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var screen = FakeSongSelectScreen.Variants.Single(v => v.Name == variant).Create();
            (ILibraryColumn Column, string Key, string Cell)[] columns =
            [
                (screen.SongColumn, "SongBrowser_Col_Song", "SongSelectRowTitle"),
                (screen.PathsColumn, "SongBrowser_Col_Paths", "SongSelectRowPaths"),
                (screen.TuningColumn, "SongBrowser_Col_Tuning", "SongSelectRowTuning"),
                (screen.LengthColumn, "SongBrowser_Col_Time", "SongSelectRowLength"),
                (screen.DifficultyColumn, "SongBrowser_Col_Difficulty", "SongSelectRowDifficulty"),
                (screen.RecordColumn, "SongBrowser_Col_Record", "SongSelectRowRecord")
            ];
            foreach (var (column, key, _) in columns) ((FakeSongColumn)column).Title = LocalizationService.Instance[key];

            WithShown(new SongSelectScreenView { DataContext = screen }, width, height, $"song-select-{variant}-{width}x{height}", view =>
                AssertTable(view, "SongSelectRedesignColumnHeader", "SongSelectRedesignList", [.. columns.Select(c => (c.Column, c.Cell))],
                    $"Song select {variant} {width}x{height}"));
        }, TestContext.Current.CancellationToken);
    }

    /// <summary>
    ///     The song column (the first entry) keeps <see cref="SongTableLayout.SongFloor" /> on every
    ///     row; each caption's text starts where every value under it starts; a hidden caption hides
    ///     its values; and no caption's content (label, sort glyph, funnel) reaches the next one.
    /// </summary>
    private static List<ListBoxItem> AssertTable(Control view, string headerName, string listName,
        IReadOnlyList<(ILibraryColumn Column, string Cell)> columns, string what)
    {
        var header = NarrowWidthLayout.Named<Border>(view, headerName);
        var list = NarrowWidthLayout.Named<ListBox>(view, listName);
        var rows = list.GetRealizedContainers().OfType<ListBoxItem>().Where(r => r.IsEffectivelyVisible).ToList();
        Assert.True(rows.Count >= 4, $"{what}: only {rows.Count} rows on screen - the fixture is too thin to test a table");

        foreach (var row in rows)
        {
            var title = Cell(row, columns[0].Cell);
            Assert.True(title.Bounds.Width >= SongTableLayout.SongFloor - 0.5,
                $"{what}: the song '{(title as TextBlock)?.Text}' has {title.Bounds.Width:0.#} px (the floor is {SongTableLayout.SongFloor})");
        }

        var previousRight = double.NegativeInfinity;
        var previousTitle = "";
        foreach (var (column, cell) in columns)
        {
            var caption = Caption(header, column);
            var values = rows.Select(r => Cell(r, cell)).ToList();
            if (!caption.IsEffectivelyVisible)
            {
                Assert.DoesNotContain(values, HasValue);
                continue;
            }

            var label = caption.GetVisualDescendants().OfType<TextBlock>().First(t => t.IsEffectivelyVisible && t.Text == column.Title);
            var labelLeft = InkLeft(label, view);
            Assert.True(labelLeft >= previousRight + 1,
                $"{what}: the {previousTitle} caption runs to x={previousRight:0.#}, into the {column.Title} caption at x={labelLeft:0.#}");

            var printed = values.Where(HasValue).ToList();
            Assert.True(printed.Count > 0, $"{what}: no row on screen prints a {column.Title} value - this column is untested");
            foreach (var value in printed)
            {
                var valueLeft = InkLeft(value, view);
                Assert.True(Math.Abs(valueLeft - labelLeft) <= Tolerance,
                    $"{what}: the {column.Title} caption starts at x={labelLeft:0.#} and a value under it at x={valueLeft:0.#}");
            }

            previousRight = caption.GetVisualDescendants().OfType<Control>()
                .Where(c => c is TextBlock or PathIcon && c.IsEffectivelyVisible)
                .Max(c => NarrowWidthLayout.Span(c, view).Right);
            previousTitle = column.Title;
        }

        var headerRight = NarrowWidthLayout.Span(header, view).Right;
        Assert.True(previousRight <= headerRight + 0.5,
            $"{what}: the {previousTitle} caption runs to x={previousRight:0.#}, past the header's edge at x={headerRight:0.#}");
        return rows;
    }

    /// <summary>The column's caption: the header panel's child that shows it, not a stand-in for it.</summary>
    private static ContentControl Caption(Border header, ILibraryColumn column) =>
        Assert.IsType<SongTablePanel>(header.Child).Children
            .Where(c => SongTablePanel.GetStandInFor(c) < 0)
            .SelectMany(c => c is StackPanel group ? group.Children : [c]) // the Library's song cell holds three
            .OfType<ContentControl>()
            .Single(c => ReferenceEquals(c.Content, column));

    private static Control Cell(ListBoxItem row, string name) =>
        row.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == name)
        ?? throw new Xunit.Sdk.XunitException($"a song row has no cell named '{name}' - repoint this test");

    /// <summary>Whether the cell prints anything: visible, laid out, and not an empty text.</summary>
    private static bool HasValue(Control cell) =>
        cell.IsEffectivelyVisible && cell.Bounds.Width > 0.5 && cell switch
        {
            TextBlock text => !string.IsNullOrEmpty(text.Text),
            FireChipStrip strip => strip.Initials is { Count: > 0 },
            _ => true
        };

    /// <summary>Where the cell's first glyph (or, for a panel, its first child) starts, in <paramref name="space" />.</summary>
    private static double InkLeft(Control cell, Visual space)
    {
        var x = cell is TextBlock text ? text.Padding.Left + text.TextLayout.HitTestTextPosition(0).X : 0;
        return cell.TranslatePoint(new Point(x, 0), space)!.Value.X;
    }

    private static void WithShown(Control view, int width, int height, string capture, Action<Control> assert)
    {
        var window = NarrowWidthLayout.NewWindow(width, height, view);
        window.Show();
        try
        {
            NarrowWidthLayout.Settle(window);
            NarrowWidthLayout.Capture(window, capture);
            assert(view);
        }
        finally
        {
            window.Close();
        }
    }
}
