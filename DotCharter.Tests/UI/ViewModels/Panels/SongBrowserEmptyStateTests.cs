using DotCharter.Localization;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Library;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Panels;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Services.Session;

namespace DotCharter.Tests.UI.ViewModels.Panels;

/// <summary>
///     The song browser's empty-state consolidation (live audit #12): LOAD
///     CHART is gated on a loadable selection and says WHY while it is off,
///     and a successful scan leaves NO resting status line (the header's
///     "N / M" count readout is the fact; "Scan complete — N songs" beside it
///     was a self-referential duplicate crammed against the frame).
/// </summary>
public sealed class SongBrowserEmptyStateTests
{
    [Fact]
    public void WithNoSelection_LoadIsOff_AndTheTipSaysWhy()
    {
        var h = new Harness();

        Assert.False(h.Vm.IsSelectionLoadable);
        Assert.Equal(LocalizationService.Instance["SongBrowser_LoadChart_NoSelection_Tip"],
            h.Vm.LoadChartTip);
    }

    [Fact]
    public void WithALoadableSelection_LoadIsOn_AndTheTipNamesTheAction()
    {
        var h = new Harness();

        h.Vm.SelectedSong = new PsarcSongListItem(new LibrarySongEntry
        {
            SongKey = "BatCountry",
            Title = "Bat Country",
            PackagePath = @"F:\dlc\a7x_p.psarc",
            Arrangements =
                [new LibraryArrangementEntry { ArrangementName = "Lead", SngPath = "songs/bin/generic/bat_lead.sng" }]
        }, FakeLocalization.English());

        Assert.True(h.Vm.IsSelectionLoadable);
        Assert.Equal(LocalizationService.Instance["SongBrowser_LoadChart_Tip"], h.Vm.LoadChartTip);
    }

    // ---- harness (the SongBrowserAudioTests shape, minus the load fakes) ----

    private sealed class NoOpLibraryService : ILibraryService
    {
        public Task<LibraryLoadedSong> LoadSongAsync(LibrarySongEntry song) => throw new NotSupportedException();
        public Task<IReadOnlyList<LibrarySongEntry>> ScanAsync(string rootPath) => throw new NotSupportedException();

        public Task<IReadOnlyList<LibrarySongEntry>> ScanAsync(
            string rootPath, IProgress<LibraryScanProgress>? progress, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<LibrarySongEntry>> ScanAsync(
            string rootPath, IProgress<LibraryScanProgress>? progress,
            IProgress<IReadOnlyList<LibrarySongEntry>>? songFound, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<string> LoadCoverImageAsync(LibrarySongEntry song) => Task.FromResult("");
    }

    private sealed class Harness
    {
        public Harness()
        {
            var logger = new FakeAppLogger();
            var state = new ProjectState();
            var config = new FakeConfigurationService();
            var transport = new TransportViewModel(new FakeAudioPlaybackService(), state, config, FakeLocalization.KeysOnly());
            var selection = new SelectionService();
            var editor = new ChartEditorViewModel(
                state, transport, new HistoryService(logger), selection,
                new TempoService(logger), new TrackContextRegistry(state, selection), FakeLocalization.KeysOnly());

            Vm = new SongBrowserViewModel(
                new FakeFileStorageService(), config, new NoOpLibraryService(), logger,
                new StubSession(), state, editor, transport,
                new DotCharter.Presentation.Dialogs.DialogsSlot(), DotCharter.Localization.LocalizationService.Instance);
        }

        public SongBrowserViewModel Vm { get; }
    }

    private sealed class StubSession : IProjectSessionService
    {
        public AppConfig AppConfig { get; } = new();
        public bool IsProjectDirty => false;
        public Func<Task<bool>>? ConfirmDiscardAsync { get; set; }
        public Task<bool> MayReplaceOpenProjectAsync() => Task.FromResult(true);
        public void MarkProjectClean() { }
        public void MarkProjectDirty() { }
        public event Action? ProjectDirtyChanged { add { } remove { } }
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
        public void Dispose() { }
    }
}
