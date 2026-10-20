using DotCharter.Presentation.Contracts.Library;
using DotCharter.UI.ViewModels.Panels;
using Harness = DotCharter.Tests.UI.ViewModels.Panels.SongBrowserFilterChipsTests.Harness;
using DotCharter.Presentation.Shared.Library;

namespace DotCharter.Tests.UI.ViewModels.Panels;

/// <summary>
///     The Library contract over the real song browser (wave 5): the adapter presents, the
///     browser still scans, filters and loads. What has to hold across that seam: the rows
///     follow the browser's list (the same row per song, the same collection instance, so the
///     ListBox is edited rather than re-bound), a row prints only exceptions, selection and sort
///     round-trip, and a column's clear is still ONE filter pass.
/// </summary>
public sealed class LibraryScreenTests
{
    [Fact]
    public async Task TheRows_FollowTheBrowsersList_WithOneRowPerSong_InOneCollection()
    {
        var h = await Harness.Scanned();
        var screen = new LibraryScreen(h.Vm);
        var rows = screen.Rows;
        Assert.Equal(h.Vm.Songs.Count, rows.Count);
        var first = rows[0];

        h.Vm.SearchText = "Track 1";
        h.Drain();
        Assert.Same(rows, screen.Rows);
        Assert.Equal(h.Vm.Songs.Count, rows.Count);
        Assert.True(rows.Count < Harness.DistinctSongs, "the search narrowed nothing — the fixture proves nothing");

        h.Vm.SearchText = "";
        h.Drain();
        Assert.Same(first, rows.Single(r => ((LibraryRow)r).Item == ((LibraryRow)first).Item));
    }

    [Fact]
    public async Task ARow_PrintsOnlyWhatDiffers()
    {
        var h = await Harness.Scanned();
        h.Library.Result = [Song("plain", "E A D G B E"), Song("drop", "D A D G B E")];
        await h.Vm.ScanCommand.ExecuteAsync(null);
        h.Drain();
        var screen = new LibraryScreen(h.Vm);
        var rows = screen.Rows.Cast<LibraryRow>().ToList();

        var standard = rows.Single(r => r.Item.Song.SongKey == "plain");
        Assert.Equal("", standard.TuningLabel);
        Assert.Equal("", standard.StatusLabel);
        Assert.False(standard.HasProblem);
        Assert.Equal(["L", "R"], standard.Paths);

        var unusual = rows.Single(r => r.Item.Song.SongKey == "drop");
        Assert.True(unusual.TuningNeedsAttention);
        Assert.Equal("Drop D", unusual.TuningLabel);
    }

    private static DotCharter.Services.Library.LibrarySongEntry Song(string key, string tuning, string? file = null) => new()
    {
        SongKey = key, Title = key, Artist = "Artist", Album = "Album", Year = 1990, SongLength = 200, TuningSummary = tuning,
        PackagePath = $@"C:\lib\{file ?? key}_p.psarc",
        Arrangements =
        [
            new DotCharter.Services.Library.LibraryArrangementEntry { ArrangementName = "Lead", SngPath = $"s/{key}_lead.sng" },
            new DotCharter.Services.Library.LibraryArrangementEntry { ArrangementName = "Rhythm", SngPath = $"s/{key}_rhythm.sng" }
        ]
    };

    /// <summary>A list item is announced by its data's ToString; the rows said "…LibraryRow".</summary>
    [Fact]
    public async Task ARow_IsAnnouncedByItsTitleAndArtist()
    {
        var h = await Harness.Scanned();
        h.Library.Result = [Song("plain", "E A D G B E")];
        await h.Vm.ScanCommand.ExecuteAsync(null);
        h.Drain();

        var row = Assert.Single(new LibraryScreen(h.Vm).Rows);
        Assert.Equal("plain, Artist", row.ToString());
    }

    /// <summary>The rail said "E Standard" five times (the fact chip, then every path). A path names
    /// its tuning only where it differs from the song's (review, 2026-09-23).</summary>
    [Fact]
    public void APath_NamesItsTuning_OnlyWhereItDiffersFromTheSongs()
    {
        var lead = new PsarcArrangementChoice(FakeLocalization.English(), "Lead", "Lead", 325, "E A D G B E");
        var bass = new PsarcArrangementChoice(FakeLocalization.English(), "Bass", "Bass", 390, "D A D G B E");
        var vocals = new PsarcArrangementChoice(FakeLocalization.English(), "Vocals", "Vocals", 0, "E A D G B E");
        var song = lead.TuningLabel;
        Assert.NotEqual(song, bass.TuningLabel);

        Assert.Equal(lead.NoteCountLabel, new LibraryPath(lead, song).Detail);
        Assert.Equal($"{bass.TuningLabel} · {bass.NoteCountLabel}", new LibraryPath(bass, song).Detail);
        Assert.Equal("", new LibraryPath(vocals, song).Detail);
    }

    [Fact]
    public async Task AnUnknownYear_ReadsAsADash()
    {
        var h = await Harness.Scanned();
        h.Library.Result = [Harness.Unknown()];
        await h.Vm.ScanCommand.ExecuteAsync(null);
        h.Drain();
        var screen = new LibraryScreen(h.Vm);

        var row = Assert.Single(screen.Rows);
        Assert.Equal("—", row.YearLabel);
        Assert.Equal("—", row.DifficultyLabel);
        Assert.Empty(row.Paths);
    }

    [Fact]
    public async Task Selection_RoundTrips_AndFillsTheRail()
    {
        var h = await Harness.Scanned();
        var screen = new LibraryScreen(h.Vm);
        var raised = new List<string?>();
        screen.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        screen.SelectedRow = screen.Rows[2];

        Assert.Same(((LibraryRow)screen.Rows[2]).Item, h.Vm.SelectedSong);
        Assert.Same(screen.Rows[2], screen.SelectedRow);
        Assert.Equal(h.Vm.SelectedSong!.Title, screen.DetailTitle);
        Assert.Contains(nameof(ILibraryScreen.DetailTitle), raised);
        Assert.Equal(h.Vm.ArrangementChoices.Count, screen.Paths.Count);
        // The rail lists the paths as rows; the archive line names the file and does not repeat them.
        Assert.StartsWith(h.Vm.SelectedSong.FileName, screen.ArchiveText);
        Assert.DoesNotContain(h.Vm.SelectedSong.ArrangementSummary, screen.ArchiveText);
    }

    /// <summary>The selected song's cover decodes after it is picked; the rail must follow it (the live app showed the row's cover and an empty rail).</summary>
    [Fact]
    public async Task TheRailsCover_FollowsTheSelectedSongsCover_AsItArrives()
    {
        var h = await Harness.Scanned();
        var screen = new LibraryScreen(h.Vm);
        screen.SelectedRow = screen.Rows[1];
        var raised = new List<string?>();
        screen.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        RaiseCoverChanged(h.Vm.SelectedSong!);
        Assert.Contains(nameof(ILibraryScreen.DetailCover), raised);

        // …and stops following it once another song is picked.
        var old = h.Vm.SelectedSong!;
        screen.SelectedRow = screen.Rows[2];
        raised.Clear();
        RaiseCoverChanged(old);
        Assert.DoesNotContain(nameof(ILibraryScreen.DetailCover), raised);
    }

    /// <summary>
    ///     Issue #36: the selection restored from the last session is ALREADY set when the screen
    ///     is built. The screen must ask for that song's cover itself (no selection change will) and
    ///     follow its arrival — the live rail showed the placeholder until another song was picked.
    /// </summary>
    [Fact]
    public async Task ASongSelectedBeforeTheScreenIsBuilt_GetsItsCoverRequested_AndTheRailFollowsIt()
    {
        var h = await Harness.Scanned();
        // Every song has a cover, and the scan warms only the first viewport's worth
        // (SongBrowserViewModel.WarmInitialCovers) — so the LAST row's cover is asked for by
        // nobody but the screen, which is the live case: a restored song further down the list.
        h.Library.Result = [.. Enumerable.Range(0, 30).Select(i => WithCover(Song($"s{i:D2}", "E A D G B E")))];
        await h.Vm.ScanCommand.ExecuteAsync(null);
        h.Drain();
        h.Vm.SelectedSong = h.Vm.Songs[^1];
        var restored = h.Vm.SelectedSong.Song.SongKey;

        var screen = new LibraryScreen(h.Vm);
        var raised = new List<string?>();
        screen.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        // The load dwells (SongCoverCache.DwellMs) before it reaches the library service; the
        // deadline is a deadlock detector, not a budget.
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!h.Library.CoverRequests.Contains(restored) && DateTime.UtcNow < deadline)
            await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.True(h.Library.CoverRequests.Contains(restored),
            "the restored selection's cover was never requested within 60 s (or the box was starved)");

        RaiseCoverChanged(h.Vm.SelectedSong!);
        Assert.Contains(nameof(ILibraryScreen.DetailCover), raised);
    }

    private static DotCharter.Services.Library.LibrarySongEntry WithCover(DotCharter.Services.Library.LibrarySongEntry song) => new()
    {
        SongKey = song.SongKey, Title = song.Title, Artist = song.Artist, Album = song.Album, Year = song.Year,
        SongLength = song.SongLength, TuningSummary = song.TuningSummary, PackagePath = song.PackagePath,
        Arrangements = song.Arrangements, CoverImageEntryPath = $"gfxassets/album_art/album_{song.SongKey}_256.dds"
    };

    // A bitmap needs the platform; the notification is what the rail listens to.
    private static void RaiseCoverChanged(PsarcSongListItem item) =>
        typeof(CommunityToolkit.Mvvm.ComponentModel.ObservableObject)
            .GetMethod("OnPropertyChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                [typeof(string)])!
            .Invoke(item, [nameof(PsarcSongListItem.CoverImage)]);

    [Fact]
    public async Task AHeader_Sorts_AndItsArrowFollows()
    {
        var h = await Harness.Scanned();
        var screen = new LibraryScreen(h.Vm);

        screen.TuningColumn.SortCommand.Execute(null);
        Assert.Equal(6, h.Vm.SortMode);
        Assert.Equal("↑", screen.TuningColumn.SortGlyph);
        Assert.Equal("", screen.SongColumn.SortGlyph);

        screen.TuningColumn.SortCommand.Execute(null);
        Assert.Equal("↓", screen.TuningColumn.SortGlyph);
        Assert.False(screen.PathsColumn.IsSortable);
    }

    /// <summary>
    ///     #306: the header is the Library's one sort control, so every order the browser can sort
    ///     by needs a caption. Title and pack / file were reachable only from the sort dropdown the
    ///     redesign removed; the song column now captions them beside its artist sort.
    /// </summary>
    [Fact]
    public async Task EveryOrderTheBrowserSortsBy_HasAHeaderCaption()
    {
        var h = await Harness.Scanned();
        var screen = new LibraryScreen(h.Vm);
        // Title first: the list starts in artist order, where the artist caption would flip it.
        ILibraryColumn[] sortable =
            [screen.TitleSort, screen.SongColumn, screen.ArchiveSort, screen.TuningColumn, screen.YearColumn, screen.DifficultyColumn, screen.LengthColumn];

        var reached = new HashSet<int>();
        foreach (var column in sortable)
        {
            Assert.True(column.IsSortable, $"{column.Title} does not sort");
            column.SortCommand.Execute(null);
            h.Drain();
            reached.Add(h.Vm.SortMode);
            Assert.Equal("↑", column.SortGlyph);
            Assert.All(sortable.Where(c => !ReferenceEquals(c, column)), other => Assert.Equal("", other.SortGlyph));
        }

        // Every mode LibrarySort.Library.Order knows (its switch: 0..6).
        Assert.Equal(Enumerable.Range(0, 7), reached.Order());
        Assert.Equal(-1, LibrarySort.Library.ModeOf("paths"));
        Assert.False(screen.TitleSort.HasFilter);
        Assert.False(screen.ArchiveSort.HasFilter);
    }

    [Fact]
    public async Task TheTitleCaption_OrdersTheRowsByTitle_AndThePackCaption_ByFileName()
    {
        var h = await Harness.Scanned();
        h.Library.Result = [Song("charlie", "E A D G B E", "Alpha"), Song("alpha", "E A D G B E", "Charlie"), Song("bravo", "E A D G B E", "Bravo")];
        await h.Vm.ScanCommand.ExecuteAsync(null);
        h.Drain();
        var screen = new LibraryScreen(h.Vm);

        screen.TitleSort.SortCommand.Execute(null);
        h.Drain();
        Assert.Equal(["alpha", "bravo", "charlie"], screen.Rows.Select(r => r.Title));

        // The file names run the other way to the titles here, so a title order cannot pass for it.
        screen.ArchiveSort.SortCommand.Execute(null);
        h.Drain();
        Assert.Equal(["charlie", "bravo", "alpha"], screen.Rows.Select(r => r.Title));
        screen.ArchiveSort.SortCommand.Execute(null);
        h.Drain();
        Assert.Equal("↓", screen.ArchiveSort.SortGlyph);
        Assert.Equal(["alpha", "bravo", "charlie"], screen.Rows.Select(r => r.Title));
    }

    /// <summary>The name alone hides which strings move: the cell's tooltip and the rail's tuning fact carry the notes.</summary>
    [Fact]
    public async Task AnUnusualTuning_NamesItsStringsNotes_InTheTipAndTheRail()
    {
        var h = await Harness.Scanned();
        h.Library.Result = [Song("plain", "E A D G B E"), Song("drop", "D A D G B E")];
        await h.Vm.ScanCommand.ExecuteAsync(null);
        h.Drain();
        var screen = new LibraryScreen(h.Vm);
        var rows = screen.Rows.Cast<LibraryRow>().ToList();

        var drop = rows.Single(r => r.Item.Song.SongKey == "drop");
        Assert.Equal("Drop D", drop.TuningLabel);
        Assert.Equal("Drop D (D A D G B E)", drop.TuningTip);
        Assert.Equal("", rows.Single(r => r.Item.Song.SongKey == "plain").TuningTip);

        screen.SelectedRow = drop;
        Assert.Contains("Drop D (D A D G B E)", screen.DetailFacts);

        // E Standard moves nothing, so the rail does not spell out its notes.
        screen.SelectedRow = rows.Single(r => r.Item.Song.SongKey == "plain");
        Assert.Contains("E Standard", screen.DetailFacts);
        Assert.DoesNotContain(screen.DetailFacts, f => f.Contains('('));
    }

    [Fact]
    public async Task ASharedColumnsClear_IsOneFilterPass()
    {
        var h = await Harness.Scanned();
        var screen = new LibraryScreen(h.Vm);
        h.Vm.TuningFacet.Options[0].IsChecked = true;
        h.Vm.CapoFacet.Options[0].IsChecked = true;
        h.Drain();
        Assert.True(screen.TuningColumn.IsFiltered);

        var before = h.Vm.FilterPass.PassCount;
        screen.TuningColumn.ClearFilterCommand.Execute(null);
        h.Drain();

        Assert.Equal(before + 1, h.Vm.FilterPass.PassCount);
        Assert.False(screen.TuningColumn.IsFiltered);
        Assert.False(h.Vm.CapoFacet.IsActive);
    }

    [Fact]
    public async Task AChip_RemovesItsOwnFilter_AndTheLessonsRow_IsTheToggle()
    {
        var h = await Harness.Scanned();
        var screen = new LibraryScreen(h.Vm);
        h.EngageEveryFilter();
        Assert.Equal(h.Vm.ActiveFilterChips.Count, screen.ActiveFilters.Count);

        screen.ActiveFilters[0].RemoveCommand.Execute(null);
        h.Drain();
        Assert.Equal(h.Vm.ActiveFilterChips.Count, screen.ActiveFilters.Count);

        var lessons = screen.StatusColumn.FilterOptions.Last();
        Assert.True(lessons.IsSelected, "EngageEveryFilter shows lessons; the row reads the toggle");
        lessons.IsSelected = false;
        Assert.True(h.Vm.HideLessons);
    }
}
