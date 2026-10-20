using DotCharter.Services.Library;
using DotCharter.Services.Library.Prefetch;

namespace DotCharter.Tests.Services.Parsing.Psarc.Prefetch;

/// <summary>#425: the prefetcher prepares the song the selection settled on,
/// off the calling thread, and logs one line per prefetch.</summary>
public sealed class LibraryAudioPrefetcherTests
{
    [Fact]
    public async Task The_settled_song_is_prepared_once_and_logged()
    {
        var logger = new FakeAppLogger();
        var preparer = new RecordingPreparer();
        var delays = new List<TaskCompletionSource>();
        using var prefetcher = new LibraryAudioPrefetcher(preparer, logger, (_, ct) =>
        {
            var tcs = new TaskCompletionSource();
            ct.Register(() => tcs.TrySetCanceled(ct));
            delays.Add(tcs);
            return tcs.Task;
        });

        _ = prefetcher.SelectionChangedForTests(Song("a"));
        var settled = prefetcher.SelectionChangedForTests(Song("b"));
        delays[1].SetResult();
        await settled;

        var prepared = Assert.Single(preparer.Calls);
        Assert.Equal("b", prepared.SongKey);
        Assert.True(logger.HasEntryContaining("INFO", "PSARC audio prefetch finished. Song='b', outcome=Prepared"));
    }

    /// <summary>Also the proof the work is off the caller's thread: the preparer
    /// blocks until the test releases it AFTER <c>SelectionChanged</c> returned.</summary>
    [Fact]
    public async Task A_prefetch_cancelled_mid_work_logs_cancelled_and_reports_no_fault()
    {
        var logger = new FakeAppLogger();
        var preparer = new RecordingPreparer { CancelAfterCall = true };
        using var prefetcher = new LibraryAudioPrefetcher(preparer, logger, (_, _) => Task.CompletedTask);

        var first = prefetcher.SelectionChangedForTests(Song("a"));
        await preparer.Entered.Task;
        _ = prefetcher.SelectionChangedForTests(null);
        preparer.Proceed.SetResult();
        await first;

        Assert.True(logger.HasEntryContaining("DEBUG", "PSARC audio prefetch cancelled. Song='a'"));
        Assert.False(logger.HasEntryContaining("WARN", "prefetch failed"));
    }

    private static LibrarySongEntry Song(string key) => new() { SongKey = key, PackagePath = $"{key}_p.psarc" };

    private sealed class RecordingPreparer : ILibraryAudioPreparer
    {
        public List<LibrarySongEntry> Calls { get; } = [];
        public bool CancelAfterCall { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Proceed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public LibraryAudioPrepareOutcome PrepareAudio(LibrarySongEntry song, CancellationToken cancellationToken)
        {
            lock (Calls) Calls.Add(song);
            if (!CancelAfterCall) return LibraryAudioPrepareOutcome.Prepared;

            Entered.SetResult();
            Assert.True(Proceed.Task.Wait(TimeSpan.FromSeconds(60)), "deadlock detector: the test never released the preparer");
            cancellationToken.ThrowIfCancellationRequested();
            return LibraryAudioPrepareOutcome.Prepared;
        }
    }
}
