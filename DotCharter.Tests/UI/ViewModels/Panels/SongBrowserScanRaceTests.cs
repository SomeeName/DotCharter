using DotCharter.Services.Editing.Selection;
using DotCharter.Localization;
using DotCharter.Services.Library;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Panels;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Services.Session;

namespace DotCharter.Tests.UI.ViewModels.Panels;

/// <summary>
///     A scan of ANOTHER folder supersedes the one in flight (Browse / SelectFolder call
///     the Scan() method directly, bypassing the AsyncRelayCommand's no-concurrency
///     guard; a second scan of the SAME folder is ignored since #186, see
///     <see cref="LibraryRefreshTests" />). The superseded call keeps running until its await unwinds, so its
///     catch/finally still execute afterwards — they used to set "scan cancelled",
///     IsScanning=false and ScanProgress=0 WHILE the replacement scan was still
///     mid-flight. Every tail of a superseded scan is now gated on its scan
///     generation.
/// </summary>
public sealed class SongBrowserScanRaceTests
{
    [Fact]
    public async Task SupersededScan_DoesNotClearTheLiveScansScanningState()
    {
        var h = new Harness();
        h.Vm.PsarcLocation = @"C:\lib";

        var first = h.Vm.ScanCommand.ExecuteAsync(null); // parks inside the library
        Assert.True(h.Vm.IsScanning);

        h.Vm.PsarcLocation = @"C:\lib2";                  // Browse picked another folder
        var second = h.Vm.ScanCommand.ExecuteAsync(null); // supersedes + cancels the first
        await first;                                      // the loser's catch + finally run here

        // The live scan owns the UI state: still scanning, and NOT "scan cancelled".
        Assert.True(h.Vm.IsScanning);
        Assert.NotEqual(LocalizationService.Instance["Status_PsarcScanCancelled"], h.Vm.StatusText);

        h.Library.Complete(1, [Entry("live")]);
        await second;

        Assert.False(h.Vm.IsScanning);
        Assert.Single(h.Vm.Songs);
        Assert.Equal("live", h.Vm.Songs[0].Song.SongKey);
    }

    /// <summary>A superseded scan that finished anyway (its work completed just
    /// before the cancel landed) must not rebuild the song list or the status from
    /// ITS result — the post-await reorder block used to overwrite the live scan's
    /// list with the loser's songs.</summary>
    [Fact]
    public async Task SupersededScan_ThatCompletedAnyway_DoesNotOverwriteTheLiveScansResult()
    {
        var h = new Harness { Library = { IgnoreCancellation = true } };
        h.Vm.PsarcLocation = @"C:\lib";

        var first = h.Vm.ScanCommand.ExecuteAsync(null);
        h.Vm.PsarcLocation = @"C:\lib2";
        var second = h.Vm.ScanCommand.ExecuteAsync(null); // supersedes the first

        h.Library.Complete(1, [Entry("live")]);
        await second;
        Assert.Single(h.Vm.AllSongs);

        // The loser now unwinds, LAST, with a full result of its own.
        h.Library.Complete(0, [Entry("stale"), Entry("stale2")]);
        await first;

        Assert.Single(h.Vm.AllSongs);
        Assert.Equal("live", h.Vm.AllSongs[0].Song.SongKey);
        Assert.False(h.Vm.IsScanning);
        // A successful scan leaves NO resting status line since 0.59 (live
        // audit #12: "Scan complete — N songs" duplicated the header's
        // "N / M" count readout forever) — so the invariant here is that the
        // stale loser wrote nothing either.
        Assert.Equal("", h.Vm.StatusText);
    }

    [Fact]
    public async Task CancelScan_AfterACompletedScan_DoesNotTouchADisposedCts()
    {
        var h = new Harness();
        h.Vm.PsarcLocation = @"C:\lib";

        var scan = h.Vm.ScanCommand.ExecuteAsync(null);
        h.Library.Complete(0, [Entry("a")]);
        await scan;

        h.Vm.CancelScanCommand.Execute(null); // the finished scan disposed its own CTS
        Assert.False(h.Vm.IsScanning);
    }

    // ---- harness -----------------------------------------------------------------

    internal static LibrarySongEntry Entry(string songKey) => new()
    {
        SongKey = songKey,
        Title = songKey,
        PackagePath = $@"C:\lib\{songKey}.psarc"
    };

    /// <summary>Every ScanAsync call parks until the test completes it (or its
    /// token is cancelled), so both scans can be in flight at once.</summary>
    internal sealed class GatedLibraryService : ILibraryService
    {
        private readonly List<TaskCompletionSource<IReadOnlyList<LibrarySongEntry>>> _calls = [];

        /// <summary>How many scans reached the library.</summary>
        public int Calls => _calls.Count;

        /// <summary>Models a scan whose work finished just before the cancel landed:
        /// it returns a full result instead of throwing OperationCanceledException.</summary>
        public bool IgnoreCancellation { get; set; }

        public void Complete(int call, IReadOnlyList<LibrarySongEntry> result) => _calls[call].TrySetResult(result);

        public async Task<IReadOnlyList<LibrarySongEntry>> ScanAsync(
            string rootPath, IProgress<LibraryScanProgress>? progress,
            IProgress<IReadOnlyList<LibrarySongEntry>>? songFound, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<IReadOnlyList<LibrarySongEntry>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _calls.Add(tcs);
            if (IgnoreCancellation) return await tcs.Task.ConfigureAwait(false);

            using var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            return await tcs.Task.ConfigureAwait(false);
        }

        public Task<LibraryLoadedSong> LoadSongAsync(LibrarySongEntry song) => throw new NotSupportedException();
        public Task<IReadOnlyList<LibrarySongEntry>> ScanAsync(string rootPath) => throw new NotSupportedException();

        public Task<IReadOnlyList<LibrarySongEntry>> ScanAsync(
            string rootPath, IProgress<LibraryScanProgress>? progress, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<string> LoadCoverImageAsync(LibrarySongEntry song) => Task.FromResult("");
    }

    private sealed class StubProjectSessionService : IProjectSessionService
    {
        public AppConfig AppConfig { get; } = new();
        public void Initialize() { }
        public void LoadAppConfig() { }
        public void SaveAppConfig() { }
        public void FreeAudio() { }
        public Task LoadFile(string filePath) => Task.CompletedTask;

        public Task LoadAudioFile(string filePath, bool generateBeats = true, bool preferMemoryPlayback = false)
            => Task.CompletedTask;

        public Task<bool> AttachAudioAsync(string sourcePath) => Task.FromResult(true);

        public Task LoadSilentTransportAsync(double projectLengthMs) => Task.CompletedTask;

        public Task OpenProject() => Task.CompletedTask;
        public Task<bool> SaveProjectAsync() => Task.FromResult(true);
        public Task<bool> SaveProjectAsAsync() => Task.FromResult(true);
        public IReadOnlyList<RecentProjectEntry> GetRecentProjects() => [];
        public event Action? RecentProjectsChanged { add { } remove { } }
        public bool IsProjectDirty => false;

        /// <summary>No shell is listening, so a replace is always allowed.</summary>
        public Func<Task<bool>>? ConfirmDiscardAsync { get; set; }

        public Task<bool> MayReplaceOpenProjectAsync() => Task.FromResult(true);
        public void MarkProjectClean() { }
        public void MarkProjectDirty() { }
        public event Action? ProjectDirtyChanged { add { } remove { } }
        public void Dispose() { }
    }

    internal sealed class Harness
    {
        public Harness(DotCharter.Services.Library.Prefetch.ILibraryAudioPrefetcher? audioPrefetcher = null)
        {
            var logger = new FakeAppLogger();
            var state = new ProjectState();
            var transport = new TransportViewModel(new FakeAudioPlaybackService(), state, Config, FakeLocalization.KeysOnly());
            var selection = new SelectionService();
            var editor = new ChartEditorViewModel(
                state, transport, new HistoryService(logger), selection,
                new TempoService(logger), new TrackContextRegistry(state, selection), FakeLocalization.KeysOnly());

            Vm = new SongBrowserViewModel(
                new FakeFileStorageService(), Config, Library, logger,
                new StubProjectSessionService(), state, editor, transport,
                new DotCharter.Presentation.Dialogs.DialogsSlot(), DotCharter.Localization.LocalizationService.Instance,
                audioPrefetcher: audioPrefetcher);
        }

        public FakeConfigurationService Config { get; } = new();
        public GatedLibraryService Library { get; } = new();
        public SongBrowserViewModel Vm { get; }
    }
}
