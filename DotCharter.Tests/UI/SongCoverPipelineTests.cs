using DotCharter.Services.Library;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.Tests.UI;

/// <summary>
///     The cover PIPELINE — the half of song-list scroll cost no headless
///     filter-pass probe sees. Three claims:
///     <list type="number">
///         <item>a row that merely scrolls past NEVER touches the disk (the
///         dwell gate aborts a load whose row was released first),</item>
///         <item>a released cover is PARKED on a byte-capped LRU, not disposed
///         — scrolling back is free and memory stays bounded (the pure
///         <see cref="CoverParkPolicy{TKey}" /> carries that contract),</item>
///         <item>decodes pass ONE global throttle, wired where the loads are
///         (source lint — the decode itself needs a Skia platform the default
///         gate does not boot).</item>
///     </list>
/// </summary>
public sealed class SongCoverPipelineTests
{
    // ── the pure park policy ──────────────────────────────────────────────────

    [Fact]
    public void Parking_PastTheBudget_EvictsOldestFirst()
    {
        var policy = new CoverParkPolicy<string>(maxBytes: 100);

        Assert.Empty(policy.Park("a", 40));
        Assert.Empty(policy.Park("b", 40));
        var evicted = policy.Park("c", 40); // 120 > 100 → "a" goes

        Assert.Equal(["a"], evicted);
        Assert.Equal(80, policy.ParkedBytes);
        Assert.Equal(2, policy.ParkedCount);
    }

    [Fact]
    public void Reparking_RefreshesRecency()
    {
        var policy = new CoverParkPolicy<string>(maxBytes: 100);
        policy.Park("a", 40);
        policy.Park("b", 40);

        policy.Park("a", 40);               // "a" is the fresh end now
        var evicted = policy.Park("c", 40); // so "b" is the oldest

        Assert.Equal(["b"], evicted);
    }

    [Fact]
    public void Unparking_RemovesWithoutEviction_AndFreesBudget()
    {
        var policy = new CoverParkPolicy<string>(maxBytes: 100);
        policy.Park("a", 60);
        policy.Unpark("a");

        Assert.Equal(0, policy.ParkedBytes);
        Assert.Empty(policy.Park("b", 90)); // the freed budget is really free
    }

    [Fact]
    public void AnItemBiggerThanTheWholeBudget_IsEvictedImmediately()
    {
        // Documented behavior, not a trap: with the 640 px decode cap a cover
        // tops out ~1.6 MB against a 64 MB budget, so this only fires if the
        // cap ever regresses — and then it degrades to dispose-on-release
        // rather than blowing the budget.
        var policy = new CoverParkPolicy<string>(maxBytes: 100);

        Assert.Equal(["big"], policy.Park("big", 500));
        Assert.Equal(0, policy.ParkedBytes);
    }

    // ── the dwell gate ────────────────────────────────────────────────────────

    /// <summary>A load whose row was released before the dwell elapsed aborts
    /// WITHOUT touching the library service — and the abort resets the one-shot
    /// guard, so the row loads normally when it is wanted again.</summary>
    [Fact]
    public async Task AReleaseBeforeTheDwell_AbortsTheLoad_AndARewantRetries()
    {
        var service = new CountingLibraryService();
        var item = new PsarcSongListItem(new LibrarySongEntry
        {
            SongKey = "k", Title = "t", Artist = "a",
            CoverImageEntryPath = "gfx/cover.dds" // has a source, not yet on disk
        }, FakeLocalization.English());

        var dwell = new TaskCompletionSource();
        var load = item.LoadCoverImageAsync(service, dwell.Task);

        item.MarkCoverUnwanted();   // the row scrolled past
        dwell.SetResult();
        await load;

        Assert.Equal(0, service.CoverCalls);

        // Wanted again (scrolled back): the retry really loads.
        await item.LoadCoverImageAsync(service, Task.CompletedTask);
        Assert.Equal(1, service.CoverCalls);
    }

    [Fact]
    public async Task ARowStillWantedAfterTheDwell_Loads()
    {
        var service = new CountingLibraryService();
        var item = new PsarcSongListItem(new LibrarySongEntry
        {
            SongKey = "k2", Title = "t", Artist = "a",
            CoverImageEntryPath = "gfx/cover.dds"
        }, FakeLocalization.English());

        await item.LoadCoverImageAsync(service, Task.CompletedTask);

        Assert.Equal(1, service.CoverCalls);
    }

    // ── the wiring, which no unit test can see ────────────────────────────────

    [Fact]
    public void BothSurfaces_ParkOnRecycle_AndUnparkOnRealize()
    {
        var browserVm = ReadSource("DotCharter.UI", "ViewModels", "Panels", "SongBrowserViewModel.cs");
        var playView = ReadSource("DotCharter.UI", "Views", "PlayMode", "Screens", "SongSelectView.axaml.cs");

        foreach (var (name, source) in new[] { ("SongBrowserViewModel", browserVm), ("SongSelectView", playView) })
        {
            Assert.Contains("SongCoverCache.Shared.Park(", source, StringComparison.Ordinal);
            Assert.Contains("SongCoverCache.Shared.Unpark(", source, StringComparison.Ordinal);
            Assert.DoesNotContain(name == "SongSelectView" ? "inner.DisposeCoverImage()" : "item.DisposeCoverImage()",
                source, StringComparison.Ordinal);
        }

        // The one global throttle sits inside the row's own load; the warm batch
        // must not stack a second local one on top.
        var item = ReadSource("DotCharter.UI", "ViewModels", "Panels", "PsarcSongListItem.cs");
        Assert.Contains("SongCoverCache.DecodeThrottle.WaitAsync", item, StringComparison.Ordinal);
        var warm = browserVm[browserVm.IndexOf("private void WarmInitialCovers", StringComparison.Ordinal)..];
        warm = warm[..warm.IndexOf("\n    }", StringComparison.Ordinal)];
        Assert.DoesNotContain("SemaphoreSlim", warm, StringComparison.Ordinal);
    }

    // ── fixtures ──────────────────────────────────────────────────────────────

    private sealed class CountingLibraryService : ILibraryService
    {
        public int CoverCalls { get; private set; }

        public Task<IReadOnlyList<LibrarySongEntry>> ScanAsync(string rootPath)
            => Task.FromResult<IReadOnlyList<LibrarySongEntry>>([]);

        public Task<IReadOnlyList<LibrarySongEntry>> ScanAsync(
            string rootPath, IProgress<LibraryScanProgress>? progress, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<LibrarySongEntry>>([]);

        public Task<IReadOnlyList<LibrarySongEntry>> ScanAsync(
            string rootPath, IProgress<LibraryScanProgress>? progress,
            IProgress<IReadOnlyList<LibrarySongEntry>>? songFound, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<LibrarySongEntry>>([]);

        public Task<LibraryLoadedSong> LoadSongAsync(LibrarySongEntry song) => throw new NotSupportedException();

        public Task<string> LoadCoverImageAsync(LibrarySongEntry song)
        {
            CoverCalls++;
            return Task.FromResult(""); // nothing on disk — the decode never runs
        }
    }

    private static string ReadSource(params string[] parts)
    {
        var path = Path.Combine(new[] { TestPaths.RepoRoot() }.Concat(parts).ToArray());
        Assert.True(File.Exists(path), $"Source file not found at {path}");
        return UiSourceTree.ReadAllText(path);
    }
}
