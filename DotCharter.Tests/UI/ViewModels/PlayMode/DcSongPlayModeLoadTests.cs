using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Services.Gameplay.Leaderboards;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Services.Gameplay.Stats;
using DotCharter.Services.Library;
using DotCharter.Services.Library.Cache;
using DotCharter.Services.Session;
using DotCharter.Tests.Services.Package.DcSong;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Panels;
using DotCharter.UI.ViewModels.PlayMode;
using DotCharter.Tests.Services.Library;
using static DotCharter.Tests.Services.Package.DcSong.DcSongTestKit;

namespace DotCharter.Tests.UI.ViewModels.PlayMode;

/// <summary>
///     Play Mode plays a <c>.dcsong</c> straight from the Library (#708): the real scan and the
///     real load path (<see cref="SongBrowserViewModel.LoadSongItem" />, the one Play Mode reuses)
///     over a hand-built package, a fake decoder and a temp cache — the chart with its sections,
///     the cached audio handed to the engine, the stems in the project for the mixer.
/// </summary>
public sealed class DcSongPlayModeLoadTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter_dcsong_playmode_").FullName;

    public void Dispose() => BestEffortDelete.Directory(_dir);

    [Fact]
    public async Task Play_mode_opens_a_package_from_the_library_with_its_chart_audio_and_stems()
    {
        var library = Directory.CreateDirectory(Path.Combine(_dir, "library")).FullName;
        var package = DcSongLibraryKit.Write(Path.Combine(library, "Deftones - My Own Summer.dcsong"),
            DcSongLibraryKit.Song("My Own Summer", "Deftones"), "MyOwnSummer", lossless: false, stems: true);
        var logger = new FakeAppLogger();
        var cache = Path.Combine(_dir, "cache");
        var packages = DcSongLibraryKit.Source(cache, new FakeDecoder(available: true), logger);
        var service = TestLibraries.Create(logger, null, new LibraryAudioCacheStore(logger, cache), packages);
        var scanned = Assert.Single(await service.ScanAsync(library));

        var state = new ProjectState();
        var session = new RecordingSession();
        var browser = Browser(state, service, session, service, logger);
        var item = new PsarcSongListItem(scanned, FakeLocalization.English(), packages.BlockFor(scanned));
        var play = PlayMode(state, browser, session);
        play.SongSelect.SetLibraryForTest([PlaySongItem.FromPsarc(item)]);

        Assert.True(await play.OpenSongAsync("lib:MyOwnSummer", "Lead"));

        Assert.Equal(PlayModeScreen.TuningCheck, play.CurrentScreen);
        Assert.Empty(play.FlowError);
        var project = state.ActiveProject!;
        Assert.Equal("My Own Summer", project.Chart.SongName);
        Assert.Equal(DcSongLibraryKit.Sections,
            project.Arrangements.Single(a => a.Name == "Lead").EventPoints.Select(e => e.Section));
        // No extraction step: the decoded song sits in the bounded cache, keyed by the package.
        var playable = Path.Combine(cache, LibraryAudioCacheStore.DirectoryNameFor(package), "song.wav");
        Assert.Equal([playable], session.LoadedAudio);
        Assert.Equal(playable, state.CurrentAudioFileName);
        Assert.Equal(FakeDecoder.Decoded(PackagedSong), File.ReadAllBytes(playable));
        // The stems reach the mixer through the project, as a separated song's do.
        var stem = Assert.Single(project.Stems);
        Assert.True(Path.IsPathRooted(stem.Path) && File.Exists(stem.Path));
        Assert.Equal("lib:MyOwnSummer", state.CurrentSongIdentity);
    }

    [Fact]
    public async Task An_opus_package_without_ffmpeg_is_disabled_with_its_reason_and_hidden_from_play_mode()
    {
        var library = Directory.CreateDirectory(Path.Combine(_dir, "library")).FullName;
        DcSongLibraryKit.Write(Path.Combine(library, "song.dcsong"), DcSongLibraryKit.Song("Silvera", "Gojira"), "Silvera", lossless: false);
        var logger = new FakeAppLogger();
        var cache = Path.Combine(_dir, "cache");
        var packages = DcSongLibraryKit.Source(cache, new FakeDecoder(available: false), logger);
        var service = TestLibraries.Create(logger, null, new LibraryAudioCacheStore(logger, cache), packages);
        var scanned = Assert.Single(await service.ScanAsync(library));
        var localization = FakeLocalization.English();

        var item = new PsarcSongListItem(scanned, localization, packages.BlockFor(scanned));
        var state = new ProjectState();
        var browser = Browser(state, service, new RecordingSession(), service, logger);
        browser.SelectedSong = item;

        Assert.False(item.CanLoad);
        Assert.Equal(localization["SongBrowser_Status_NeedsDecoder"], item.StatusLabel);
        Assert.Equal(localization["Library_Row_SourceDcSong"], item.SourceTag);
        Assert.False(browser.IsSelectionLoadable);
        Assert.Equal(localization["SongBrowser_LoadChart_NeedsDecoder_Tip"], browser.LoadChartTip);
        Assert.False(PlaySongItem.FromPsarc(item).CanPlay);
        Assert.False(await browser.LoadSongItem(item));
    }

    /// <summary>
    ///     The library with ONLY the <c>.dcsong</c> source registered (#886: the public build
    ///     once the PSARC source moves out): the Library's own scan lists the package and never
    ///     touches the <c>.psarc</c> beside it, and Play Mode opens the package from that list.
    /// </summary>
    [Fact]
    public async Task The_library_and_play_mode_work_with_only_the_dcsong_source_registered()
    {
        var library = Directory.CreateDirectory(Path.Combine(_dir, "library")).FullName;
        DcSongLibraryKit.Write(Path.Combine(library, "Deftones - My Own Summer.dcsong"),
            DcSongLibraryKit.Song("My Own Summer", "Deftones"), "MyOwnSummer", lossless: true);
        var archive = Path.Combine(library, "other_p.psarc");
        File.WriteAllText(archive, "a package no registered source reads");
        var logger = new FakeAppLogger();
        var packages = DcSongLibraryKit.Source(Path.Combine(_dir, "cache"), new FakeDecoder(available: true), logger);
        var service = new LibraryService(logger, [packages]);

        var state = new ProjectState();
        var session = new RecordingSession();
        var browser = Browser(state, service, session, service, logger);
        browser.PsarcLocation = library;
        var previous = SynchronizationContext.Current;
        // Inline: the scan's progress and song batches land before the scan returns.
        SynchronizationContext.SetSynchronizationContext(new InlineContext());
        try
        {
            await browser.ScanCommand.ExecuteAsync(null);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        var item = Assert.Single(browser.AllSongs);
        Assert.Equal("MyOwnSummer", item.Song.SongKey);
        Assert.True(item.CanLoad);
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains(archive, StringComparison.OrdinalIgnoreCase));

        var play = PlayMode(state, browser, session);
        play.SongSelect.SetLibraryForTest([PlaySongItem.FromPsarc(item)]);
        Assert.True(await play.OpenSongAsync("lib:MyOwnSummer", "Lead"));
        Assert.Equal(PlayModeScreen.TuningCheck, play.CurrentScreen);
        Assert.Empty(play.FlowError);
        Assert.Equal("My Own Summer", state.ActiveProject!.Chart.SongName);
        Assert.Single(session.LoadedAudio);
    }

    private static SongBrowserViewModel Browser(
        ProjectState state, ILibraryService library, IProjectSessionService session, ILibrarySongPlayability playability, IAppLogger logger)
    {
        var config = new FakeConfigurationService();
        var transport = new TransportViewModel(new FakeAudioPlaybackService(), state, config, FakeLocalization.KeysOnly());
        var selection = new SelectionService();
        var editor = new ChartEditorViewModel(
            state, transport, new HistoryService(logger), selection, new TempoService(logger), new TrackContextRegistry(state, selection), FakeLocalization.KeysOnly());
        return new SongBrowserViewModel(
            new FakeFileStorageService(), config, library, logger, session, state, editor, transport,
            new DotCharter.Presentation.Dialogs.DialogsSlot(), FakeLocalization.English(), playability: playability);
    }

    private PlayModeViewModel PlayMode(ProjectState state, SongBrowserViewModel browser, IProjectSessionService session)
    {
        var profiles = new PlayerProfileService(FakeLocalization.English(), null, Path.Combine(_dir, "profiles.json"), "Tester");
        var store = new PracticeProfileService(profiles, null, Path.Combine(_dir, "legacy.json"));
        return TestRigBuilders.PlayMode(profiles, new GameplayStatsService(store), new LeaderboardService(profiles),
            state, songBrowser: browser, projectSession: session, localization: FakeLocalization.English());
    }

    private sealed class InlineContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);
    }

    private sealed class RecordingSession : IProjectSessionService
    {
        public List<string> LoadedAudio { get; } = [];
        public AppConfig AppConfig { get; } = new();
        public bool IsProjectDirty => false;
        public Func<Task<bool>>? ConfirmDiscardAsync { get; set; }
        public Task<bool> MayReplaceOpenProjectAsync() => Task.FromResult(true);
        public event Action? ProjectDirtyChanged { add { } remove { } }
        public event Action? RecentProjectsChanged { add { } remove { } }
        public void MarkProjectClean() { }
        public void MarkProjectDirty() { }
        public void Initialize() { }
        public void LoadAppConfig() { }
        public void SaveAppConfig() { }
        public void FreeAudio() { }
        public Task LoadFile(string filePath) => Task.CompletedTask;

        public Task LoadAudioFile(string filePath, bool generateBeats = true, bool preferMemoryPlayback = false)
        {
            LoadedAudio.Add(filePath);
            return Task.CompletedTask;
        }

        public Task<bool> AttachAudioAsync(string sourcePath) => Task.FromResult(true);
        public Task LoadSilentTransportAsync(double projectLengthMs) => Task.CompletedTask;
        public Task OpenProject() => Task.CompletedTask;
        public Task<bool> SaveProjectAsync() => Task.FromResult(true);
        public Task<bool> SaveProjectAsAsync() => Task.FromResult(true);
        public IReadOnlyList<RecentProjectEntry> GetRecentProjects() => [];
        public void Dispose() { }
    }
}
