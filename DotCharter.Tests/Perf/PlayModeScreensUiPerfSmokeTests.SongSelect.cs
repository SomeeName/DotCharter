// Song select - the first-run state, the row list and no-match panel, the tuning column alignment.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Localization;
using DotCharter.UI.Controls;
using DotCharter.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.Perf;

public sealed partial class PlayModeScreensUiPerfSmokeTests
{
    [Theory]
    [InlineData(1920, 1000)]
    [InlineData(1280, 720)]
    public async Task SongSelect_ControlsHaveWindowInsets(double width, double height)
    {
        await WithView((view, vm) =>
        {
            vm.CurrentScreen = PlayModeScreen.SongSelect;
            view.UpdateLayout();
            var header = Find(view, "SongSelectHeader");
            var back = header.GetVisualDescendants().OfType<Button>().First();
            AssertLaidOut(view, "SongSelectHeader");
            Assert.True(back.TranslatePoint(default, view)!.Value.X >= 18,
                "The Back button must have space from the window edge.");
            var browse = Find<Border>(view, "SongSelectBrowseBar");
            Assert.True(browse.Padding.Left >= 18 && browse.Padding.Right >= 18,
                "The browse controls need horizontal window padding.");
        }, seedPractice: false, width: width, height: height);
    }

    /// <summary>Song Select with an EMPTY library: the first-run panel is up,
    /// the no-match panel is not, and the browse bar still measures (it is the
    /// one piece of chrome that must never collapse while the list is empty —
    /// see SongSelectLayoutStructureTests for why).</summary>
    [Fact]
    public async Task SongSelectScreen_laysOutItsFirstRunState()
    {
        await WithView((view, vm) =>
        {
            // Deliberately NOT OpenSongSelectCommand: that one also activates the
            // LAN gossip service off the app container, which would bind real
            // sockets from a test. The SCREEN is what is under test here, and it
            // is reached by the same property the command sets.
            vm.CurrentScreen = PlayModeScreen.SongSelect;
            view.UpdateLayout();

            AssertLaidOut(view, "SongSelectScreenRoot");
            AssertLaidOut(view, "SongSelectHeader");
            AssertLaidOut(view, "SongSelectBrowseBar");
            AssertLaidOut(view, "SongSelectChipRow");
            // The 0.47 FILTERS toggle and its combo row are GONE (the unified
            // browser, 2026-08-15): the filters live in the caption row's
            // facet popovers now, so the caption header is the chrome that has
            // to be up — even over an empty library.
            AssertLaidOut(view, "SongSelectListHeader");

            Assert.True(vm.SongSelect.ShowSongSelectFirstRun,
                "an empty library must land on the first-run invitation, not the filtered-out panel");
            Assert.False(vm.SongSelect.ShowSongSelectNoMatch);
            Assert.False(vm.SongSelect.HasActiveSongFilters);
            AssertLaidOut(view, "SongSelectFirstRunPanel");
            Assert.False(Find(view, "SongSelectNoMatchPanel").IsVisible);
        }, seedPractice: false);
    }

    /// <summary>Song Select with a library behind it: rows realize, and a
    /// search that matches nothing flips to the OTHER empty state (the one
    /// that offers to clear the filters) rather than the first-run one.</summary>
    [Fact]
    public async Task SongSelectScreen_laysOutRowsAndTheNoMatchState()
    {
        await WithView((view, vm) =>
        {
            // Deliberately NOT OpenSongSelectCommand: that one also activates the
            // LAN gossip service off the app container, which would bind real
            // sockets from a test. The SCREEN is what is under test here, and it
            // is reached by the same property the command sets.
            vm.CurrentScreen = PlayModeScreen.SongSelect;
            // Own-chart rows: no PSARC browser, no LAN catalogue, and the row
            // template still has to render (Title falls back to the file name).
            vm.SongSelect.SetLibraryForTest([
                PlaySongItem.FromProject(Path.Combine(_dir, "Song A.rscp"), false, LocalizationService.Instance),
                PlaySongItem.FromProject(Path.Combine(_dir, "Song B.rscp"), false, LocalizationService.Instance)
            ]);
            view.UpdateLayout();

            Assert.Equal(2, vm.SongSelect.SongList.Count);
            Assert.False(vm.SongSelect.ShowSongSelectFirstRun);
            Assert.False(vm.SongSelect.ShowSongSelectNoMatch);
            var list = Find(view, "SongListBox");
            Assert.True(list.Bounds.Height > 50, $"the song list laid out as {list.Bounds}");
            // A realized row must actually draw its Fire row chrome.
            Assert.Equal(2, FireRows((ItemsControl)list).Count);
            Assert.All(FireRows((ItemsControl)list),
                r => Assert.True(r.Bounds.Height > 40, $"a song row laid out {r.Bounds}"));

            // The detail rail carries every ACTION on this screen (the mockup's
            // move — the row is information, the rail is what you DO). With
            // nothing picked it has to say so; picking a song has to bring the
            // notched PLAY up at a real size.
            AssertLaidOut(view, "SongSelectDetailRail");
            Assert.Null(vm.SelectedSong);
            // The gate is on the rail's body, so that is the element whose
            // IsVisible answers — `IsVisible` is the LOCAL property, never the
            // effective one, and the PLAY button carries its own binding.
            Assert.False(Find(view, "SongSelectDetailBody").IsVisible);

            vm.SelectedSong = vm.SongSelect.SongList[0];
            view.UpdateLayout();
            AssertLaidOut(view, "SongSelectDetailBody");
            AssertLaidOut(view, "SongSelectPlayButton");
            // An own chart has no stats behind it, so the "your best" panel is
            // DOWN — an empty best panel is the thing this screen must not draw.
            Assert.False(vm.SelectedSong!.HasBest);
            Assert.False(Find(view, "SongSelectBestPanel").IsVisible);
            vm.SelectedSong = null;
            view.UpdateLayout();

            // Filter everything away: the no-match panel takes over, the
            // first-run one stays down, and the clear-filters affordance arms.
            vm.SongSelect.SearchText = "zzzz-no-such-song";
            // Typing routes through CoalescedFilterPass.Request(), which posts
            // the pass at Background priority (a5254861) — UpdateLayout alone
            // never drains that queue, so the pass has to be run explicitly the
            // way the product's first idle frame would.
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
            view.UpdateLayout();
            Assert.True(vm.SongSelect.HasActiveSongFilters);
            Assert.True(vm.SongSelect.ShowSongSelectNoMatch);
            Assert.False(vm.SongSelect.ShowSongSelectFirstRun);
            AssertLaidOut(view, "SongSelectNoMatchPanel");

            // …and the escape hatch actually restores the list.
            vm.SongSelect.ClearSongFiltersCommand.Execute(null);
            view.UpdateLayout();
            Assert.Equal("", vm.SongSelect.SearchText);
            Assert.False(vm.SongSelect.HasActiveSongFilters);
            Assert.Equal(2, vm.SongSelect.SongList.Count);
        }, seedPractice: false);
    }

    /// <summary>
    ///     §6.2's two stated acceptance criteria, measured on the real laid-out
    ///     screen:
    ///     <list type="number">
    ///         <item>
    ///             THE TUNING COLUMN LINES UP DOWN THE WHOLE LIST. It is a fixed
    ///             150 px grid column; an <c>Auto</c> one measures per row, so
    ///             every row would still have a tuning cell and the column would
    ///             be ragged — which no bounds-per-element check can see. This
    ///             compares the cells' LEFT EDGES in the list's own space.
    ///         </item>
    ///         <item>
    ///             EXACTLY ONE PLAY BUTTON, and it is in the detail pane. The
    ///             per-row Play/board/download cluster moving into the rail is
    ///             the point of the redesign, and a stray row-level one is
    ///             invisible until you look at two rows at once.
    ///         </item>
    ///     </list>
    /// </summary>
    [Fact]
    public async Task SongSelectScreen_alignsTheTuningColumnAndHasOnePlayButton()
    {
        await WithView((view, vm) =>
        {
            vm.CurrentScreen = PlayModeScreen.SongSelect;
            vm.SongSelect.SetLibraryForTest([
                PlaySongItem.FromProject(Path.Combine(_dir, "Song A.rscp"), false, LocalizationService.Instance),
                PlaySongItem.FromProject(Path.Combine(_dir, "Song B.rscp"), false, LocalizationService.Instance),
                PlaySongItem.FromProject(Path.Combine(_dir, "Song C.rscp"), false, LocalizationService.Instance)
            ]);
            vm.SelectedSong = vm.SongSelect.SongList[0];
            view.UpdateLayout();

            var list = Find(view, "SongListBox");
            var cells = list.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.Name == "SongTuningColumn").ToList();
            Assert.Equal(vm.SongSelect.SongList.Count, cells.Count);

            var lefts = cells.Select(c => c.TranslatePoint(default, list)!.Value.X).ToList();
            Assert.All(lefts, x => Assert.Equal(lefts[0], x, 1));
            Assert.All(cells, c => Assert.Equal(150.0, ColumnWidthOf(c), 1));

            // ONE Play button on the whole screen, inside the detail rail.
            var root = Find(view, "SongSelectScreenRoot");
            var rail = Find(view, "SongSelectDetailRail");
            var plays = root.GetVisualDescendants().OfType<Button>()
                .Where(b => ReferenceEquals(b.Command, vm.PlaySongCommand)).ToList();
            Assert.Single(plays);
            Assert.Equal("SongSelectPlayButton", plays[0].Name);
            AssertContains(rail, plays[0], "the one Play button");
        }, seedPractice: false);
    }

    /// <summary>#187's twin check on Play Mode's Song Library: the Library and this list are one
    /// screen in two places, and the Library's rail lost three of four paths to its cover at
    /// 1280×720. Here the paths are ONE row of tiles under a 200 px cover, so every tile of a
    /// four-path song stays inside the rail and on screen at both sizes.</summary>
    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1000)]
    public async Task SongSelect_detailRail_showsEveryPathOfAFourPathSong(double width, double height)
    {
        await WithView((view, vm) =>
        {
            vm.CurrentScreen = PlayModeScreen.SongSelect;
            var entry = new DotCharter.Services.Library.LibrarySongEntry
            {
                SongKey = "fourpaths", Title = "Three Little Birds", Artist = "Bob Marley & The Wailers", Album = "Exodus",
                PackagePath = Path.Combine(_dir, "fourpaths_p.psarc"), SongLength = 186, TuningSummary = "E A D G B E",
                Arrangements =
                [
                    .. new[] { ("Lead", 1), ("Rhythm", 2), ("Bass", 4), ("Combo", 3) }.Select(a =>
                        new DotCharter.Services.Library.LibraryArrangementEntry
                        {
                            ArrangementName = a.Item1, RouteMask = a.Item2, NoteCount = 300, SngPath = $"songs/bin/generic/fp_{a.Item1}.sng"
                        })
                ]
            };
            vm.SongSelect.SetLibraryForTest([PlaySongItem.FromPsarc(new DotCharter.UI.ViewModels.Panels.PsarcSongListItem(entry, LocalizationService.Instance))]);
            vm.SelectedSong = vm.SongSelect.SongList[0];
            view.UpdateLayout();

            var rail = Find(view, "SongSelectDetailRail");
            var tiles = Find(view, "SongSelectArrangementTiles").GetVisualDescendants().OfType<ToggleButton>().ToList();
            Assert.Equal(4, tiles.Count);
            var railBottom = rail.TranslatePoint(new Point(0, rail.Bounds.Height), view)!.Value.Y;
            Assert.All(tiles, tile =>
            {
                AssertContains(rail, tile, $"the {tile.Name ?? "path"} tile");
                var bottom = tile.TranslatePoint(new Point(0, tile.Bounds.Height), view)!.Value.Y;
                Assert.True(tile.Bounds.Height > 1 && bottom <= railBottom + 0.5 && bottom <= height,
                    $"{width}x{height}: a path tile ends at y={bottom:0.#}, the rail at {railBottom:0.#}");
            });
        }, seedPractice: false, width: width, height: height);
    }

    /// <summary>The behavioural half of
    /// <c>SongSelectLayoutStructureTests.TheSongList_DeclaresNoItemsPanelOverride</c>:
    /// with a big library behind it the list realizes a handful of containers,
    /// not one per row. The browser has carried the same pair since 0.44.</summary>
    [Fact]
    public async Task SongSelectScreen_staysVirtualized_overABigLibrary()
    {
        await WithView((view, vm) =>
        {
            vm.CurrentScreen = PlayModeScreen.SongSelect;
            vm.SongSelect.SetLibraryForTest(Enumerable.Range(0, 300)
                .Select(i => PlaySongItem.FromProject(Path.Combine(_dir, $"Song {i:D3}.rscp"), false, LocalizationService.Instance))
                .ToList());
            view.UpdateLayout();

            Assert.Equal(300, vm.SongSelect.SongList.Count);
            var realized = Find(view, "SongListBox").GetVisualDescendants().OfType<ListBoxItem>().Count();
            Assert.True(realized < 60,
                $"the song list realized {realized} containers for 300 rows — virtualization is off");
        }, seedPractice: false);
    }

    /// <summary>The width of the table column a cell sits in. The acceptance
    /// criterion is that the column is FIXED, so this reads the shared
    /// <see cref="SongTableLayout" /> rather than the cell's own measured size:
    /// a cell can happen to be 150 px wide inside the flexible song column.</summary>
    private static double ColumnWidthOf(Control cell)
    {
        var table = Assert.IsType<SongTablePanel>(cell.GetVisualParent());
        var index = SongTablePanel.GetColumn(cell);
        Assert.True(index != table.Columns!.SongColumn,
            "the tuning cell sits in the flexible song column - it must be a FIXED width or the values go ragged");
        Assert.Equal(table.Columns.Columns[index].Width, table.Places[index].Width, 1);
        return table.Columns.Columns[index].Width;
    }
}
