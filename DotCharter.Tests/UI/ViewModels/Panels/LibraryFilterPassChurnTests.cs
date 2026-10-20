using System.Collections.Concurrent;
using System.Collections.Specialized;
using DotCharter.Services.Infrastructure.Storage;
using DotCharter.Services.Library;
using DotCharter.UI.ViewModels.Panels;
using Harness = DotCharter.Tests.UI.ViewModels.Panels.SongBrowserFilterChipsTests.Harness;

namespace DotCharter.Tests.UI.ViewModels.Panels;

/// <summary>
///     #180: what one Library keystroke does besides filtering, measured as WORK — selection
///     changes, collection events, disk reads on the calling thread — over a library of the
///     owner's scale (<see cref="SyntheticSongLibrary" />). Each was a measured share of a
///     keystroke: the list's transient null after a Reset rebuilt the detail rail twice per
///     pass, the chips and the paths republished one event per item, and the archive size and
///     the cover lookups read the disk on the UI thread.
/// </summary>
public sealed class LibraryFilterPassChurnTests
{
    /// <summary>A Reset clears the ListBox's selection and the list writes that null back
    /// through the two-way binding; a song the pass KEPT must stay selected through it.</summary>
    [Fact]
    public async Task APassThatKeepsTheSelectedSong_KeepsItSelected_ThroughTheListsReset()
    {
        var h = await Scanned();
        var screen = new LibraryScreen(h.Vm);
        ActLikeTheListBox(screen);
        var keep = h.Vm.Songs.First(s => s.Artist == "Metallica");
        h.Vm.SelectedSong = keep;
        var changes = CountSelectionChanges(h);
        var resets = 0;
        ((INotifyCollectionChanged)screen.Rows).CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset) resets++;
        };

        h.Vm.SearchText = "metallica";
        h.Drain();

        Assert.True(resets > 0, "the pass did not reset the rows — the fixture no longer exercises the Reset path");
        Assert.Same(keep, h.Vm.SelectedSong);
        Assert.Same(keep, ((LibraryRow?)screen.SelectedRow)?.Item);
        Assert.Equal(0, changes());
    }

    /// <summary>A song the pass filtered OUT gives way to the first row — with or without a
    /// list attached, in ONE selection change (it used to go via null whenever a list was).</summary>
    [Fact]
    public async Task APassThatDropsTheSelectedSong_SelectsTheFirstRow_InOneChange()
    {
        var h = await Scanned();
        var gone = h.Vm.Songs.First(s => s.Artist != "Metallica");
        h.Vm.SelectedSong = gone;
        var changes = CountSelectionChanges(h);

        h.Vm.SearchText = "metallica";
        h.Drain();

        Assert.DoesNotContain(gone, h.Vm.Songs);
        Assert.Same(h.Vm.Songs[0], h.Vm.SelectedSong);
        Assert.Equal(1, changes());
    }

    /// <summary>The chip row is ONE change per pass, and none when the chips are equal — a
    /// Clear plus an Add per chip made the Library rebuild its chip row once per event.</summary>
    [Fact]
    public async Task TheChips_RepublishOncePerPass_AndNotAtAllWhenUnchanged()
    {
        var h = await Scanned();
        var events = 0;
        h.Vm.ActiveFilterChips.CollectionChanged += (_, _) => events++;

        h.Vm.SearchText = "metallica";
        h.Drain();
        Assert.Equal(1, events);

        events = 0;
        h.Vm.SortByColumnCommand.Execute("year"); // a pass, the same chips
        Assert.Equal(0, events);
    }

    /// <summary>The rail's paths are ONE change per selection, not a Clear plus an Add each.</summary>
    [Fact]
    public async Task ThePaths_RepublishOncePerSelection()
    {
        var h = await Scanned();
        var song = h.Vm.Songs.First(s => s.Song.Arrangements.Count >= 3);
        var events = 0;
        h.Vm.ArrangementChoices.CollectionChanged += (_, _) => events++;

        h.Vm.SelectedSong = song;

        Assert.True(h.Vm.ArrangementChoices.Count >= 3, "the fixture song has fewer than three paths");
        Assert.Equal(1, events);
    }

    /// <summary>The archive's size is a disk read, on a drive that may be asleep: a selection
    /// never waits for it. It arrives through the caller's context afterwards.</summary>
    [Fact]
    public async Task TheArchiveSize_IsReadOffTheSelectionsThread()
    {
        var file = Path.Combine(Path.GetTempPath(), $"dotcharter-180-{Guid.NewGuid():N}_p.psarc");
        await File.WriteAllBytesAsync(file, new byte[3 * 1024 * 1024 / 2], TestContext.Current.CancellationToken);
        var previous = SynchronizationContext.Current;
        var context = new QueueContext();
        try
        {
            var h = await Scanned();
            h.Library.Result = [.. SyntheticSongLibrary.Build(40), Entry(file)];
            await h.Vm.ScanCommand.ExecuteAsync(null);
            h.Drain();
            var song = h.Vm.Songs.Single(s => s.Song.PackagePath == file);

            SynchronizationContext.SetSynchronizationContext(context);
            h.Vm.SelectedSong = song;
            Assert.Equal("", h.Vm.ArchiveDetailText);

            // A blocking wait: an await here would post this test's own continuation to the queue.
            Assert.True(context.WaitForWork(TimeSpan.FromSeconds(60)), "the size read never finished (starved box?)");
            context.RunAll();
            Assert.Equal($"{1.5:0.#} MB", h.Vm.ArchiveDetailText); // the product formats in the current culture
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
            File.Delete(file);
        }
    }

    /// <summary>A cover load never asks the disk whether a cover exists on the thread that
    /// asked for it (the UI thread: a row coming on screen).</summary>
    [Fact]
    public async Task ACoverLoad_ChecksForTheFileOffTheCallersThread()
    {
        var artwork = new RecordingArtwork();
        var service = new SongBrowserFilterChipsTests.StubLibraryService();
        var item = new PsarcSongListItem(new LibrarySongEntry
        {
            SongKey = "k", Title = "t", Artist = "a", CoverImagePath = @"F:\covers\k.png", CoverImageEntryPath = "gfx/k.dds"
        }, FakeLocalization.English());

        Task? load = null;
        var caller = new Thread(() => load = item.LoadCoverImageAsync(service, Task.CompletedTask, artwork));
        caller.Start();
        caller.Join();
        await load!;

        Assert.NotEmpty(artwork.Threads); // the probe saw the checks at all
        Assert.DoesNotContain(caller.ManagedThreadId, artwork.Threads);
    }

    /// <summary>Both song lists keep their selection in view with ONE scroll per selection change
    /// (<see cref="DotCharter.UI.Controls.SelectionScroll" />), never Avalonia's index-following
    /// auto-scroll, which laid the list out once per row a pass inserted above the selection. The
    /// behavioural half is <c>Perf/LibraryResponsivenessUiPerfSmokeTests</c>.</summary>
    [Theory]
    [InlineData("Library/LibraryView.axaml", "LibraryRows")]
    [InlineData("PlayMode/Screens/SongSelectView.axaml", "SongListBox")]
    public void BothSongLists_ScrollTheirSelectionOncePerChange(string view, string list)
    {
        var path = Path.Combine([TestPaths.RepoRoot(), "DotCharter.UI", "Views", .. view.Split('/')]);
        var element = System.Xml.Linq.XDocument.Parse(UiSourceTree.ReadAllText(path)).Descendants()
            .SingleOrDefault(e => e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == list));

        Assert.True(element is not null, $"{list} was renamed — repoint this lint.");
        Assert.Equal("True", element!.Attributes().SingleOrDefault(a => a.Name.LocalName == "SelectionScroll.Coalesced")?.Value);
    }

    // ── fixture ───────────────────────────────────────────────────────────────

    private static async Task<Harness> Scanned()
    {
        var h = await Harness.Scanned();
        h.Library.Result = SyntheticSongLibrary.Build();
        await h.Vm.ScanCommand.ExecuteAsync(null);
        h.Drain();
        return h;
    }

    /// <summary>What the ListBox does to its two-way SelectedItem: a Reset drops the selection,
    /// and so does removing the selected row.</summary>
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

    private static Func<int> CountSelectionChanges(Harness h)
    {
        var count = 0;
        h.Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SongBrowserViewModel.SelectedSong)) count++;
        };
        return () => count;
    }

    private static LibrarySongEntry Entry(string path) => new()
    {
        SongKey = "sized", Title = "Sized", Artist = "Zz Sized", Album = "A", PackagePath = path, SongLength = 200,
        TuningSummary = "E A D G B E",
        Arrangements = [new LibraryArrangementEntry { ArrangementName = "Lead", SngPath = "s/sized_lead.sng" }]
    };

    /// <summary>Continuations posted here wait until the test runs them.</summary>
    private sealed class QueueContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _work = new();
        private readonly SemaphoreSlim _posted = new(0);

        public override void Post(SendOrPostCallback d, object? state)
        {
            _work.Enqueue((d, state));
            _posted.Release();
        }

        public bool WaitForWork(TimeSpan cap) => _posted.Wait(cap);

        public void RunAll()
        {
            while (_work.TryDequeue(out var item)) item.Callback(item.State);
        }
    }

    private sealed class RecordingArtwork : IArtworkSource
    {
        public ConcurrentBag<int> Threads { get; } = [];

        public bool Exists(ArtworkKey key)
        {
            Threads.Add(Environment.CurrentManagedThreadId);
            return false;
        }

        public Stream? Open(ArtworkKey key) => null;

        public Stream CreateStaged(string extension, out string path) => throw new NotSupportedException();

        public void DeleteStaged(string path)
        {
        }
    }
}
