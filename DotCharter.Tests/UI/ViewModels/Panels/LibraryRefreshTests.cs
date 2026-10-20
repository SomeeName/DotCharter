using System.Collections.Specialized;
using System.Globalization;
using DotCharter.Localization;
using DotCharter.Services.Library;
using DotCharter.UI.ViewModels.Panels;
using Harness = DotCharter.Tests.UI.ViewModels.Panels.SongBrowserFilterChipsTests.Harness;

namespace DotCharter.Tests.UI.ViewModels.Panels;

/// <summary>
///     #186: Refresh in the Library is a MERGE of the same folder's scan, not a rebuild. It used to
///     empty the list, select the first song, drop the chosen path, scroll to the top, release
///     every decoded cover and then say nothing — and the owner's log had 23 scans of one folder in
///     5 s. Every fixture rescans <see cref="SyntheticSongLibrary" />, which builds NEW entry objects
///     with the same values each time, as the library index's cache does.
/// </summary>
public sealed class LibraryRefreshTests
{
    private const int Size = 240;

    [Fact]
    public async Task ARefresh_KeepsTheSelectedSong_AndItsPath()
    {
        var h = await Scanned();
        var screen = new LibraryScreen(h.Vm);
        ActLikeTheListBox(screen);
        var song = h.Vm.Songs.Skip(Size / 2).First(s => s.Song.Arrangements.Count >= 3);
        h.Vm.SelectedSong = song;
        var bass = h.Vm.ArrangementChoices.Single(c => c.ArrangementName == "Bass");
        h.Vm.SelectedArrangement = bass;
        var changes = CountChanges(h, nameof(SongBrowserViewModel.SelectedSong), nameof(SongBrowserViewModel.SelectedArrangement));

        await Refresh(h, SyntheticSongLibrary.Build(Size));

        Assert.Same(song, h.Vm.SelectedSong);
        Assert.Same(bass, h.Vm.SelectedArrangement);
        Assert.Same(song, ((LibraryRow?)screen.SelectedRow)?.Item);
        Assert.Equal(0, changes());
    }

    /// <summary>A selected song whose archive CHANGED is a new item; the selection and the path follow it.</summary>
    [Fact]
    public async Task ARefresh_ThatChangesTheSelectedSong_SelectsItsNewEntry_OnTheSamePath()
    {
        var h = await Scanned();
        var song = h.Vm.Songs.Skip(Size / 2).First(s => s.Song.Arrangements.Count >= 3);
        h.Vm.SelectedSong = song;
        h.Vm.SelectedArrangement = h.Vm.ArrangementChoices.Single(c => c.ArrangementName == "Bass");

        var next = SyntheticSongLibrary.Build(Size);
        var at = next.FindIndex(e => LibraryRescan.KeyOf(e) == LibraryRescan.KeyOf(song.Song));
        next[at] = Retitled(next[at]);
        await Refresh(h, next);

        Assert.NotSame(song, h.Vm.SelectedSong);
        Assert.Equal(LibraryRescan.KeyOf(song.Song), LibraryRescan.KeyOf(h.Vm.SelectedSong!.Song));
        Assert.Equal(next[at].Title, h.Vm.SelectedSong.Title);
        Assert.Equal("Bass", h.Vm.SelectedArrangement?.ArrangementName);
    }

    /// <summary>One song added and one removed are ONE Add and ONE Remove on the list (and on the
    /// Library's rows) — no Reset, so the realized rows and the scroll position stay.</summary>
    [Fact]
    public async Task ARefresh_UpdatesTheRowsInPlace()
    {
        var h = await Scanned();
        var screen = new LibraryScreen(h.Vm);
        var rowsBefore = screen.Rows.ToList();
        var gone = Unduplicated(h).Skip(10).First();
        var songEvents = Record(h.Vm.Songs);
        var rowEvents = Record((INotifyCollectionChanged)screen.Rows);

        await Refresh(h, [.. SyntheticSongLibrary.Build(Size).Where(e => LibraryRescan.KeyOf(e) != LibraryRescan.KeyOf(gone.Song)), NewSong()]);

        Assert.Equal([NotifyCollectionChangedAction.Add, NotifyCollectionChangedAction.Remove], songEvents.Order());
        Assert.Equal([NotifyCollectionChangedAction.Add, NotifyCollectionChangedAction.Remove], rowEvents.Order());
        Assert.DoesNotContain(gone, h.Vm.Songs);
        Assert.Contains(h.Vm.Songs, s => s.Song.SongKey == "zzBrandNew");
        // Every row that stayed is the SAME row object.
        Assert.All(rowsBefore.Where(r => ((LibraryRow)r).Item != gone), r => Assert.Contains(r, screen.Rows));
    }

    /// <summary>An unchanged song keeps its item, so its decoded cover is never released and
    /// decoded again; a removed song's cover is released.</summary>
    [Fact]
    public async Task ARefresh_ReleasesOnlyTheCoversOfSongsThatWent()
    {
        var h = await Scanned();
        var before = h.Vm.AllSongs.ToList();
        var generations = before.ToDictionary(s => s, s => s.CoverGeneration);
        var gone = Unduplicated(h).Skip(3).First();

        await Refresh(h, [.. SyntheticSongLibrary.Build(Size).Where(e => LibraryRescan.KeyOf(e) != LibraryRescan.KeyOf(gone.Song))]);

        Assert.Equal(before.Count - 1, h.Vm.AllSongs.Count);
        Assert.All(h.Vm.AllSongs, s =>
        {
            Assert.True(generations.TryGetValue(s, out var generation), $"'{s.Title}' is a new item: its cover would load again");
            Assert.Equal(generation, s.CoverGeneration);
        });
        Assert.True(gone.CoverGeneration > generations[gone], "the removed song's cover was not released");
    }

    [Fact]
    public async Task ARefresh_SaysWhatItChanged_AndNothingElseDoes()
    {
        var loc = LocalizationService.Instance;
        var h = await Scanned();
        var screen = new LibraryScreen(h.Vm);
        Assert.Equal("", h.Vm.RefreshResultText); // a first scan: the count readout is the answer

        await Refresh(h, SyntheticSongLibrary.Build(Size));
        Assert.Equal(loc["Library_Refresh_NoChanges"], h.Vm.RefreshResultText);
        Assert.Equal(h.Vm.RefreshResultText, screen.RefreshResultText);

        var gone = Unduplicated(h).First();
        await Refresh(h, [.. SyntheticSongLibrary.Build(Size).Where(e => LibraryRescan.KeyOf(e) != LibraryRescan.KeyOf(gone.Song)), NewSong()]);
        var parts = string.Join(loc["Library_Refresh_Separator"], loc.Format("Library_Refresh_Added", 1.ToString("N0", CultureInfo.CurrentCulture)),
            loc.Format("Library_Refresh_Removed", 1.ToString("N0", CultureInfo.CurrentCulture)));
        Assert.Equal(loc.Format("Library_Refresh_Changes", parts), h.Vm.RefreshResultText);

        h.Vm.SearchText = "metallica"; // it described the list before this search
        Assert.Equal("", h.Vm.RefreshResultText);
        Assert.Equal("", screen.RefreshResultText);
    }

    /// <summary>A Refresh while the same folder is being read starts nothing: the library is asked once.</summary>
    [Fact]
    public async Task ASecondRefresh_DuringAScan_DoesNotStartASecondScan()
    {
        var h = new SongBrowserScanRaceTests.Harness();
        h.Vm.PsarcLocation = @"C:\lib";
        var screen = new LibraryScreen(h.Vm);

        var first = h.Vm.ScanCommand.ExecuteAsync(null);
        Assert.True(screen.IsScanning);
        var second = h.Vm.ScanCommand.ExecuteAsync(null);
        screen.RefreshCommand.Execute(null);

        Assert.Equal(1, h.Library.Calls);
        Assert.True(h.Vm.IsScanning);

        h.Library.Complete(0, [SongBrowserScanRaceTests.Entry("a")]);
        await first;
        await second;
        Assert.False(h.Vm.IsScanning);
        Assert.Single(h.Vm.AllSongs);
    }

    // ── fixture ───────────────────────────────────────────────────────────────

    private static async Task<Harness> Scanned()
    {
        var h = await Harness.Scanned();
        // The harness scans its own small library first; this is the library under test, as a new folder.
        h.Vm.PsarcLocation = @"E:\Rocksmith\dlc";
        h.Library.Result = SyntheticSongLibrary.Build(Size);
        await h.Vm.ScanCommand.ExecuteAsync(null);
        h.Drain();
        return h;
    }

    private static async Task Refresh(Harness h, IReadOnlyList<LibrarySongEntry> result)
    {
        h.Library.Result = result;
        await h.Vm.ScanCommand.ExecuteAsync(null);
        h.Drain();
    }

    /// <summary>Listed songs no byte-copy shares a key with, so removing one removes its row.</summary>
    private static IEnumerable<PsarcSongListItem> Unduplicated(Harness h) =>
        h.Vm.Songs.Where(s => h.Vm.AllSongs.Count(o => o.Song.SongKey == s.Song.SongKey) == 1);

    private static LibrarySongEntry NewSong() => new()
    {
        SongKey = "zzBrandNew", Title = "Brand New Song", Artist = "Metallica", Album = "New", Year = 2026, SongLength = 200,
        TuningSummary = "E A D G B E", PackagePath = @"E:\Rocksmith\dlc\new\brandnew_p.psarc",
        Arrangements = [new LibraryArrangementEntry { ArrangementName = "Lead", SngPath = "songs/bin/generic/new_lead.sng" }]
    };

    private static LibrarySongEntry Retitled(LibrarySongEntry e) => new()
    {
        SongKey = e.SongKey, Charter = e.Charter, Version = e.Version, Title = e.Title + " (v2)", Artist = e.Artist, Album = e.Album,
        Year = e.Year, SongLength = e.SongLength, TuningSummary = e.TuningSummary, CapoSummary = e.CapoSummary,
        Arrangements = e.Arrangements, Difficulty = e.Difficulty, PackagePath = e.PackagePath
    };

    private static List<NotifyCollectionChangedAction> Record(INotifyCollectionChanged list)
    {
        var actions = new List<NotifyCollectionChangedAction>();
        list.CollectionChanged += (_, e) => actions.Add(e.Action);
        return actions;
    }

    private static Func<int> CountChanges(Harness h, params string[] properties)
    {
        var count = 0;
        h.Vm.PropertyChanged += (_, e) =>
        {
            if (properties.Contains(e.PropertyName)) count++;
        };
        return () => count;
    }

    /// <summary>What the ListBox does to its two-way SelectedItem: a Reset drops the selection, and
    /// so does removing the selected row.</summary>
    private static void ActLikeTheListBox(LibraryScreen screen)
    {
        ((INotifyCollectionChanged)screen.Rows).CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset ||
                (e.Action == NotifyCollectionChangedAction.Remove && e.OldItems!.Contains(screen.SelectedRow)))
            {
                screen.SelectedRow = null;
            }
        };
    }
}
