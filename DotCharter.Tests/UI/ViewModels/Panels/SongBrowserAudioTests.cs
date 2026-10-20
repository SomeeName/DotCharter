using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Library;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Panels;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Services.Session;

namespace DotCharter.Tests.UI.ViewModels.Panels;

/// <summary>
///     Regression cover for the audio side of <see cref="SongBrowserViewModel.LoadSongItem" />.
///
///     A PSARC that carries no audio (Rocksmith 1 import charts keep theirs in
///     the base game's main package) used to clear only the *state* describing the
///     loaded audio while leaving the previous song's source alive in the engine.
///     Playing then produced the previous song's audio under the new song's chart
///     — and Live Play scored the run against it.
/// </summary>
public sealed class SongBrowserAudioTests
{
    [Fact]
    public async Task LoadSongItem_SongWithoutAudio_FreesThePreviousSongsAudio()
    {
        var h = new Harness(playableAudioPath: "");

        var loaded = await h.Vm.LoadSongItem(Item("ClashShould", "Should I Stay or Should I Go"));

        Assert.True(loaded);
        Assert.Equal(1, h.Session.FreeAudioCalls);
        Assert.Empty(h.Session.LoadedAudioFiles);
        Assert.Equal("", h.State.CurrentAudioFileName);
    }

    /// <summary>#399: Settings ▸ Files ▸ "Hardest difficulty level only" opens a song with one level,
    /// the hardest, every note kept; off, the official ladder stays.</summary>
    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 3)]
    public async Task LoadSongItem_KeepsOnlyTheHardestLevel_WhenTheSettingIsOn(bool hardestOnly, int levels)
    {
        var h = new Harness(playableAudioPath: "");
        h.Library.Levels = 3;
        h.Config.AppConfig.PsarcOpenHardestLevelOnly = hardestOnly;

        Assert.True(await h.Vm.LoadSongItem(Item("Ladder", "A laddered song")));

        var arrangement = Assert.Single(h.State.ActiveProject!.Arrangements);
        Assert.Equal(levels, arrangement.Levels.Count);
        Assert.Equal(3, ChartLevels.Hardest(arrangement)!.Notes.Count);
    }

    [Fact]
    public async Task LoadSongItem_SongWithAudio_LoadsItAndDoesNotFree()
    {
        var h = new Harness(playableAudioPath: @"C:\cache\120613838.wav");

        var loaded = await h.Vm.LoadSongItem(Item("BatCountry", "Bat Country"));

        Assert.True(loaded);
        Assert.Equal(0, h.Session.FreeAudioCalls);
        Assert.Equal([@"C:\cache\120613838.wav"], h.Session.LoadedAudioFiles);
        Assert.Equal(@"C:\cache\120613838.wav", h.State.CurrentAudioFileName);
    }

    /// <summary>The real sequence from the bug report: a song with audio, then one
    /// without. The second load must not leave the first one's audio playable.</summary>
    [Fact]
    public async Task LoadSongItem_AudioSongThenSilentSong_DoesNotLeaveStaleAudioLoaded()
    {
        var h = new Harness(playableAudioPath: @"C:\cache\120613838.wav");
        await h.Vm.LoadSongItem(Item("BatCountry", "Bat Country"));
        Assert.Equal(0, h.Session.FreeAudioCalls);

        h.Library.PlayableAudioPath = ""; // Should I Stay or Should I Go — no audio in its psarc
        await h.Vm.LoadSongItem(Item("ClashShould", "Should I Stay or Should I Go"));

        Assert.Equal(1, h.Session.FreeAudioCalls);
        Assert.Equal("", h.State.CurrentAudioFileName);
        Assert.Single(h.Session.LoadedAudioFiles); // only Bat Country's, and it has been freed
    }

    /// <summary>
    ///     A song loaded straight out of the library has no unsaved changes, and
    ///     the load has to SAY so.
    ///     <para>
    ///         <c>ApplyProject(resetHistory: true)</c> calls
    ///         <c>HistoryService.Clear</c>, which mints a NEW root node id, and the
    ///         dirty flag is "current node id ≠ the baseline id" — so a replace path
    ///         that stays silent leaves a baseline no node can ever match again and
    ///         the session reads dirty for the rest of its life. That shipped: the
    ///         owner and a friend both hit "The project has unsaved changes" on
    ///         closing after merely browsing a library song, and because the
    ///         recovery snapshotter watches the same flag, the next launch offered
    ///         to restore a chart nobody had edited.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task LoadSongItem_LeavesTheSessionClean()
    {
        var h = new Harness(playableAudioPath: @"C:\cache\120613838.wav");

        await h.Vm.LoadSongItem(Item("BatCountry", "Bat Country"));

        Assert.Equal(1, h.Session.CleanMarks);
        Assert.Equal(0, h.Session.DirtyMarks);
    }

    /// <summary>A load that FAILED must not claim the session is clean — the
    /// previous project is still open and may well hold real edits.</summary>
    [Fact]
    public async Task LoadSongItem_ThatFails_LeavesTheDirtyStateAlone()
    {
        var h = new Harness(playableAudioPath: "");
        h.Library.Throw = true;

        var loaded = await h.Vm.LoadSongItem(Item("BatCountry", "Bat Country"));

        Assert.False(loaded);
        Assert.Equal(0, h.Session.CleanMarks);
    }

    [Fact]
    public async Task HomeSongRequest_LoadsTheRequestedPsarcEvenWhenItsLibraryRowIsFilteredOut()
    {
        var h = new Harness(playableAudioPath: "");
        var dir = Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));
        try
        {
            var profiles = new DotCharter.Services.Gameplay.Profiles.PlayerProfileService(FakeLocalization.English(),
                null, Path.Combine(dir, "profiles.json"), "Tester");
            var store = new DotCharter.Services.Audio.LivePlay.PracticeProfileService(
                profiles, null, Path.Combine(dir, "legacy.json"));
            var play = TestRigBuilders.PlayMode(profiles,
                new DotCharter.Services.Gameplay.Stats.GameplayStatsService(store),
                new DotCharter.Services.Gameplay.Leaderboards.LeaderboardService(profiles),
                h.State, songBrowser: h.Vm, projectSession: h.Session, localization: FakeLocalization.English());
            play.SongSelect.SetLibraryForTest([
                DotCharter.UI.ViewModels.PlayMode.PlaySongItem.FromPsarc(Item("first", "Other song")),
                DotCharter.UI.ViewModels.PlayMode.PlaySongItem.FromPsarc(Item("second", "Requested song"))]);
            play.SongSelect.SearchText = "Other song";
            play.SongSelect.FilterPass.RunImmediately();
            Assert.Single(play.SongSelect.SongList);

            await play.OpenSongAsync("lib:second", "Lead:Combo");

            Assert.Equal(DotCharter.UI.ViewModels.PlayMode.PlayModeScreen.TuningCheck, play.CurrentScreen);
            Assert.Equal("Requested song", h.State.ActiveProject!.Chart.SongName);
            Assert.Equal("lib:second", play.SelectedSong!.SongIdentity);
            Assert.Equal("Combo", play.SelectedFlowArrangement!.Name);
            Assert.Empty(play.FlowError);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task HomeSongRequest_WaitsForTheLibraryScanAfterReturningHome()
    {
        var h = new Harness(playableAudioPath: "");
        h.Config.AppConfig.PsarcLibraryPath = "test-library";
        var scanResult = new TaskCompletionSource<IReadOnlyList<LibrarySongEntry>>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Library.ScanResult = scanResult.Task;
        var dir = Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));
        try
        {
            var profiles = new DotCharter.Services.Gameplay.Profiles.PlayerProfileService(FakeLocalization.English(),
                null, Path.Combine(dir, "profiles.json"), "Tester");
            var store = new DotCharter.Services.Audio.LivePlay.PracticeProfileService(
                profiles, null, Path.Combine(dir, "legacy.json"));
            var play = TestRigBuilders.PlayMode(profiles,
                new DotCharter.Services.Gameplay.Stats.GameplayStatsService(store),
                new DotCharter.Services.Gameplay.Leaderboards.LeaderboardService(profiles),
                h.State, songBrowser: h.Vm, projectSession: h.Session, localization: FakeLocalization.English());
            var scan = play.OpenSongSelectCommand.ExecuteAsync(null);
            Assert.True(play.SongSelect.IsLibraryLoading);
            Assert.True(play.NavigateBack());
            var request = play.OpenSongAsync("lib:second", "Lead:Combo");
            var waited = !request.IsCompleted;
            scanResult.SetResult([Item("second", "Requested song").Song]);
            await Task.WhenAll(scan, request);

            Assert.True(waited, "The Home request was discarded while scanning.");
            Assert.Equal(DotCharter.UI.ViewModels.PlayMode.PlayModeScreen.TuningCheck, play.CurrentScreen);
            Assert.Equal("Requested song", h.State.ActiveProject!.Chart.SongName);
            Assert.Equal(1, h.Library.ScanCalls);
        }
        finally
        {
            scanResult.TrySetResult([]);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    // ---- harness -----------------------------------------------------------------

    private static PsarcSongListItem Item(string songKey, string title) => new(new LibrarySongEntry
    {
        SongKey = songKey,
        Title = title,
        PackagePath = @"E:\Rocksmith2014\dlc\compat_pack_p.psarc",
        Arrangements =
            [new LibraryArrangementEntry { ArrangementName = "Combo", SngPath = $"songs/bin/generic/{songKey}_combo.sng" }]
    }, FakeLocalization.English());

    private sealed class FakeLibraryService : ILibraryService
    {
        public Task<IReadOnlyList<LibrarySongEntry>>? ScanResult { get; set; }
        public int ScanCalls { get; private set; }
        public string PlayableAudioPath { get; set; } = "";

        /// <summary>Difficulty levels the loaded arrangement carries; level N holds N + 1 notes.</summary>
        public int Levels { get; set; } = 1;

        /// <summary>Makes the load fail the way a corrupt/renamed psarc does.</summary>
        public bool Throw { get; set; }

        public Task<LibraryLoadedSong> LoadSongAsync(LibrarySongEntry song)
        {
            if (Throw) throw new InvalidDataException("psarc header is not a psarc");

            var project = new ChartProject();
            var arr = new Arrangement { Name = "Combo" };
            for (var i = 0; i < 6; i++) arr.TuningPitches.Add(new TuningOffset(0));
            for (var d = 0; d < Levels; d++)
            {
                var level = new ChartLevel { DifficultyIndex = d };
                for (var n = 0; n <= d; n++)
                    level.Notes.Add(new GuitarNote { Position = new FractionalPosition(n), StringIndex = 1, Fret = 3 + n });
                arr.Levels.Add(level);
            }

            project.Arrangements.Add(arr);
            project.Chart = new ChartData { SongName = song.Title, Artist = song.Artist };

            return Task.FromResult(new LibraryLoadedSong
            {
                Project = project,
                PlayableAudioPath = PlayableAudioPath,
                ExtractedAudioPath = "" // nothing extracted ⇒ "no audio", not "converter needed"
            });
        }

        public Task<IReadOnlyList<LibrarySongEntry>> ScanAsync(string rootPath) => throw new NotSupportedException();

        public Task<IReadOnlyList<LibrarySongEntry>> ScanAsync(
            string rootPath, IProgress<LibraryScanProgress>? progress, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<LibrarySongEntry>> ScanAsync(
            string rootPath, IProgress<LibraryScanProgress>? progress,
            IProgress<IReadOnlyList<LibrarySongEntry>>? songFound, CancellationToken cancellationToken)
        {
            ScanCalls++;
            return ScanResult ?? throw new NotSupportedException();
        }

        public Task<string> LoadCoverImageAsync(LibrarySongEntry song) => Task.FromResult("");
    }

    private sealed class FakeProjectSessionService : IProjectSessionService
    {
        public int FreeAudioCalls { get; private set; }
        public List<string> LoadedAudioFiles { get; } = [];

        public AppConfig AppConfig { get; } = new();
        public void Initialize() { }
        public void LoadAppConfig() { }
        public void SaveAppConfig() { }
        public void FreeAudio() => FreeAudioCalls++;
        public Task LoadFile(string filePath) => Task.CompletedTask;

        public Task LoadAudioFile(string filePath, bool generateBeats = true, bool preferMemoryPlayback = false)
        {
            LoadedAudioFiles.Add(filePath);
            return Task.CompletedTask;
        }

        public Task<bool> AttachAudioAsync(string sourcePath) => Task.FromResult(true);

        public List<double> SilentTransportLengths { get; } = [];
        public Task LoadSilentTransportAsync(double projectLengthMs)
        {
            SilentTransportLengths.Add(projectLengthMs);
            return Task.CompletedTask;
        }

        public Task OpenProject() => Task.CompletedTask;
        public Task<bool> SaveProjectAsync() => Task.FromResult(true);
        public Task<bool> SaveProjectAsAsync() => Task.FromResult(true);
        public IReadOnlyList<RecentProjectEntry> GetRecentProjects() => [];
        public event Action? RecentProjectsChanged { add { } remove { } }
        public bool IsProjectDirty => false;

        /// <summary>No shell is listening, so a replace is always allowed.</summary>
        public Func<Task<bool>>? ConfirmDiscardAsync { get; set; }

        public Task<bool> MayReplaceOpenProjectAsync() => Task.FromResult(true);

        public int CleanMarks { get; private set; }
        public int DirtyMarks { get; private set; }
        public void MarkProjectClean() => CleanMarks++;
        public void MarkProjectDirty() => DirtyMarks++;
        public event Action? ProjectDirtyChanged { add { } remove { } }
        public void Dispose() { }
    }

    private sealed class Harness
    {
        public Harness(string playableAudioPath)
        {
            Library.PlayableAudioPath = playableAudioPath;

            var logger = new FakeAppLogger();
            var transport = new TransportViewModel(new FakeAudioPlaybackService(), State, Config, FakeLocalization.KeysOnly());
            var selection = new SelectionService();
            var editor = new ChartEditorViewModel(
                State, transport, new HistoryService(logger), selection,
                new TempoService(logger), new TrackContextRegistry(State, selection), FakeLocalization.KeysOnly());

            Vm = new SongBrowserViewModel(
                new FakeFileStorageService(), Config, Library, logger, Session, State, editor, transport,
                new DotCharter.Presentation.Dialogs.DialogsSlot(), DotCharter.Localization.LocalizationService.Instance);
        }

        public ProjectState State { get; } = new();
        public FakeConfigurationService Config { get; } = new();
        public FakeLibraryService Library { get; } = new();
        public FakeProjectSessionService Session { get; } = new();
        public SongBrowserViewModel Vm { get; }
    }
}
