using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using DotCharter.Localization;
using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Gameplay.Leaderboards;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Services.Gameplay.Stats;
using DotCharter.Services.Library;
using DotCharter.UI.Controls;
using DotCharter.Tone.UI.Controls.Fire;
using DotCharter.UI.ViewModels.Panels;
using DotCharter.UI.ViewModels.PlayMode;
using DotCharter.UI.Views.PlayMode;

namespace DotCharter.Tests.Perf;

/// <summary>
///     #231: the LIVE Play Mode song list (<c>SongListBox</c> under <c>SongSelectListHeader</c> in
///     <see cref="PlayModeView" />, beside the 420 px detail rail) gives the SONG column its floor
///     at every window width, a long title ends in an ellipsis inside its own cell instead of
///     running over the path tags, and every caption starts where its values start.
///     <para>
///         At 1280 px the Song column was 186 px and the title sat in a horizontal
///         <c>StackPanel</c>, which measures with infinite width, so "For Those About to Rock (We
///         Salute You)" (284 px) was drawn over the <c>L R B</c> tags. The list now lays out
///         through <see cref="SongTablePanel" /> over <see cref="SongTableLayout.PlayModeSongList" />,
///         the mechanism #212 gave the Library and the redesigned Song Select, and its scrollbar
///         overlays the rows as theirs does, so the caption row and the rows are one width.
///     </para>
///     <para>
///         Asserted on what is DRAWN, as <see cref="SongTableColumnsUiPerfSmokeTests" /> does: the
///         song cell's arranged width, the title's text-layout right edge against the paths cell's
///         left edge, at least one title actually trimmed (so the long-title fixture is exercised),
///         and the first glyph of each caption against the first glyph of every value under it.
///         Captions are the shipped strings, so the release's de-DE / ja-JP re-run
///         (<c>$HeadlessLayoutFilter</c>) measures them. <c>DOTCHARTER_UI_SWEEP_DUMP=1</c> writes a
///         PNG per case.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class PlayModeSongListColumnsUiPerfSmokeTests : IDisposable
{
    private const double Tolerance = 2;

    /// <summary>Real CDLC titles of the lengths the issue measured, and one far past any width.</summary>
    private static readonly (string Artist, string Title)[] Songs =
    [
        ("AC/DC", "For Those About to Rock (We Salute You)"),
        ("The White Stripes",
            "Seven Nation Army (Live at the Royal Albert Hall, London, 2006) [Extended Remaster Edition, Bonus Track Version, "
            + "Recorded Live Under the Big Black Sky in Front of the Whole World]"),
        ("Arctic Monkeys", "I Bet You Look Good on the Dancefloor"),
        ("DragonForce", "Through the Fire and Flames"),
        ("Muse", "Knights of Cydonia"),
        ("Rush", "YYZ"),
        ("Iron Maiden", "Hallowed Be Thy Name (2015 Remaster)"),
        ("Queen", "Bohemian Rhapsody")
    ];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
            // A temp folder a later run cleans up.
        }
    }

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1280, 800)]
    [InlineData(1600, 900)]
    [InlineData(1920, 1080)]
    public async Task TheSongColumnKeepsItsFloor_TitlesTrimInTheirCell_AndCaptionsSitOverTheirValues(int width, int height)
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var profiles = new PlayerProfileService(FakeLocalization.English(), logger: null,
                filePath: Path.Combine(_dir, "player-profiles.json"), defaultProfileName: "Tester");
            var store = new PracticeProfileService(profiles, logger: null,
                legacyFilePath: Path.Combine(_dir, "practice-profiles.json"));
            var state = new ProjectState();
            var vm = TestRigBuilders.PlayMode(profiles, new GameplayStatsService(store),
                new LeaderboardService(profiles), state, toneRig: HeadlessToneRig.FromSession(state), localization: LocalizationService.Instance);
            vm.CurrentScreen = PlayModeScreen.SongSelect;
            vm.SongSelect.SetLibraryForTest([.. Enumerable.Range(0, 40).Select(Row)]);

            var view = new PlayModeView { DataContext = vm };
            var window = NarrowWidthLayout.NewWindow(width, height, view);
            window.Show();
            try
            {
                NarrowWidthLayout.Settle(window);
                NarrowWidthLayout.Capture(window, $"playmode-song-list-{width}x{height}");
                AssertTable(view, $"Play Mode song list {width}x{height}");
            }
            finally
            {
                window.Close();
            }
        }, TestContext.Current.CancellationToken);
    }

    private PlaySongItem Row(int i)
    {
        // Each artist run (the list groups by artist) holds a mix of short and long titles.
        var artist = Songs[i % Songs.Length].Artist;
        var title = Songs[(i + i / Songs.Length) % Songs.Length].Title;
        var entry = new LibrarySongEntry
        {
            SongKey = $"song{i}", Title = title, Artist = artist, Album = "Album",
            PackagePath = Path.Combine(_dir, $"song{i}_p.psarc"), SongLength = 185 + i * 7,
            TuningSummary = i % 3 == 0 ? "D A D G B E" : "E A D G B E",
            Arrangements =
            [
                .. new[] { ("Lead", 1), ("Rhythm", 2), ("Bass", 4) }.Select(a => new LibraryArrangementEntry
                {
                    ArrangementName = a.Item1, RouteMask = a.Item2, NoteCount = 300, SngPath = $"songs/bin/generic/s{i}_{a.Item1}.sng"
                })
            ]
        };
        return PlaySongItem.FromPsarc(new PsarcSongListItem(entry, LocalizationService.Instance));
    }

    private static void AssertTable(PlayModeView view, string what)
    {
        var header = NarrowWidthLayout.Named<Border>(view, "SongSelectListHeader");
        var list = NarrowWidthLayout.Named<ListBox>(view, "SongListBox");
        var rows = list.GetRealizedContainers().OfType<ListBoxItem>().Where(r => r.IsEffectivelyVisible).ToList();
        Assert.True(rows.Count >= 4, $"{what}: only {rows.Count} rows on screen - the fixture is too thin to test a table");

        // The song column keeps its floor, and the title stays inside it, trimmed, left of the path tags.
        var trimmed = 0;
        foreach (var row in rows)
        {
            var title = Assert.IsType<TextBlock>(Cell(row, "SongRowTitle"));
            var cell = TableCell(title);
            var cellSpan = NarrowWidthLayout.Span(cell, row);
            Assert.True(cellSpan.Right - cellSpan.Left >= SongTableLayout.SongFloor - 0.5,
                $"{what}: the song cell of '{title.Text}' has {cellSpan.Right - cellSpan.Left:0.#} px (the floor is {SongTableLayout.SongFloor})");

            var textRight = InkLeft(title, row) + title.TextLayout.Width;
            Assert.True(textRight <= cellSpan.Right + 0.5,
                $"{what}: '{title.Text}' is drawn to x={textRight:0.#}, past its cell's right edge at x={cellSpan.Right:0.#} - it does not trim");

            var paths = Cell(row, "SongRowPaths");
            if (paths.IsEffectivelyVisible)
            {
                var pathsLeft = NarrowWidthLayout.Span(paths, row).Left;
                Assert.True(textRight <= pathsLeft,
                    $"{what}: '{title.Text}' is drawn to x={textRight:0.#}, over the path tags at x={pathsLeft:0.#}");
            }

            if (title.TextLayout.TextLines.Any(l => l.HasCollapsed)) trimmed++;
        }

        Assert.True(trimmed > 0, $"{what}: no title on screen was trimmed - the long-title fixture is not exercised");

        // The caption row and every row are one table: same left edge, same width.
        var headerTable = (Control)header.GetVisualDescendants().OfType<TextBlock>()
            .Select(t => TableCell(t).GetVisualParent()).First(p => p is not null)!;
        var headerSpan = NarrowWidthLayout.Span(headerTable, view);
        foreach (var row in rows)
        {
            var rowSpan = NarrowWidthLayout.Span((Control)TableCell(Cell(row, "SongRowTitle")).GetVisualParent()!, view);
            Assert.True(Math.Abs(rowSpan.Left - headerSpan.Left) <= 0.5 && Math.Abs(rowSpan.Right - headerSpan.Right) <= 0.5,
                $"{what}: the caption row's columns span x={headerSpan.Left:0.#}..{headerSpan.Right:0.#} and a song row's x={rowSpan.Left:0.#}..{rowSpan.Right:0.#}");
        }

        (string Key, string Cell)[] columns =
        [
            ("SongBrowser_Col_Song", "SongRowTitle"),
            ("SongBrowser_Col_Paths", "SongRowPaths"),
            ("SongBrowser_Col_Tuning", "SongTuningColumn"),
            ("SongBrowser_Col_Record", "SongRowRecord"),
            ("SongBrowser_Col_Time", "SongRowLength")
        ];

        var previousRight = double.NegativeInfinity;
        var previousTitle = "";
        foreach (var (key, cellName) in columns)
        {
            var caption = LocalizationService.Instance[key];
            // A caption whose column gave way is never measured, so its template (and the label in it) is not built.
            var label = header.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Text == caption);
            var values = rows.Select(r => Cell(r, cellName)).ToList();
            if (label is not { IsEffectivelyVisible: true })
            {
                Assert.True(headerTable.GetVisualChildren().OfType<Control>().Any(c => !c.IsVisible),
                    $"{what}: the caption row prints no '{caption}' and hides no caption - repoint this test");
                Assert.DoesNotContain(values, HasValue);
                continue;
            }

            var labelLeft = InkLeft(label, view);
            Assert.True(labelLeft >= previousRight + 1,
                $"{what}: the {previousTitle} caption runs to x={previousRight:0.#}, into the {caption} caption at x={labelLeft:0.#}");

            var printed = values.Where(HasValue).ToList();
            Assert.True(printed.Count > 0, $"{what}: no row on screen prints a {caption} value - this column is untested");
            foreach (var value in printed)
            {
                var valueLeft = InkLeft(value, view);
                Assert.True(Math.Abs(valueLeft - labelLeft) <= Tolerance,
                    $"{what}: the {caption} caption starts at x={labelLeft:0.#} and a value under it at x={valueLeft:0.#}");
            }

            previousRight = TableCell(label).GetVisualDescendants().OfType<Control>()
                .Where(c => c is TextBlock or PathIcon && c.IsEffectivelyVisible)
                .Max(c => NarrowWidthLayout.Span(c, view).Right);
            previousTitle = caption;
        }

        var headerRight = NarrowWidthLayout.Span(header, view).Right;
        Assert.True(previousRight <= headerRight + 0.5,
            $"{what}: the {previousTitle} caption runs to x={previousRight:0.#}, past the header's edge at x={headerRight:0.#}");

        // One column set: the caption row and every row lay out through the same layout and show the same columns.
        var headerPanel = Assert.IsType<SongTablePanel>(headerTable);
        Assert.Same(SongTableLayout.PlayModeSongList, headerPanel.Columns);
        foreach (var row in rows)
        {
            var rowPanel = Assert.IsType<SongTablePanel>(TableCell(Cell(row, "SongRowTitle")).GetVisualParent());
            Assert.Same(headerPanel.Columns, rowPanel.Columns);
            Assert.Equal(headerPanel.Shown, rowPanel.Shown);
        }
    }

    /// <summary>The ancestor of <paramref name="control" /> (or itself) that is a cell of its table row.</summary>
    private static Control TableCell(Control control) =>
        control.GetSelfAndVisualAncestors().OfType<Control>()
            .FirstOrDefault(c => c.GetVisualParent() is SongTablePanel or Grid { ColumnDefinitions.Count: > 1 })
        ?? throw new Xunit.Sdk.XunitException($"'{control.Name}' is in no table row - repoint this test");

    private static Control Cell(ListBoxItem row, string name) =>
        row.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == name)
        ?? throw new Xunit.Sdk.XunitException($"a song row has no cell named '{name}' - repoint this test");

    /// <summary>Whether the cell prints anything: visible, laid out, and some text or tags in it.</summary>
    private static bool HasValue(Control cell) =>
        cell.IsEffectivelyVisible && cell.Bounds.Width > 0.5 && cell switch
        {
            TextBlock text => !string.IsNullOrEmpty(text.Text),
            FireChipStrip strip => strip.Initials is { Count: > 0 },
            _ => Texts(cell).Any()
        };

    private static IEnumerable<TextBlock> Texts(Control cell) =>
        cell.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text));

    /// <summary>Where the cell's first glyph starts in <paramref name="space" />: a text's first
    /// glyph, a panel's leftmost printed text, any other control's left edge.</summary>
    private static double InkLeft(Control cell, Visual space)
    {
        if (cell is TextBlock text)
            return text.TranslatePoint(new Point(text.Padding.Left + text.TextLayout.HitTestTextPosition(0).X, 0), space)!.Value.X;
        if (cell is not FireChipStrip && Texts(cell).ToList() is { Count: > 0 } texts)
            return texts.Min(t => InkLeft(t, space));
        return cell.TranslatePoint(default, space)!.Value.X;
    }
}
