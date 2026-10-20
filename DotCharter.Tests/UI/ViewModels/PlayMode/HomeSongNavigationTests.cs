using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Gameplay.Leaderboards;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Services.Gameplay.Stats;
using DotCharter.Services.Session;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI.ViewModels.PlayMode;

public sealed class HomeSongNavigationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("Bass", "Bass")]
    [InlineData("Bass:Bass", "Bass")]
    [InlineData("removed path", "Lead")]
    public async Task ARememberedProject_LoadsAndOpensGetReadyOnItsPath(string requested, string expected)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "remembered.rscp");
        File.WriteAllText(path, "");
        var state = new ProjectState();
        var session = new Session(state);
        var vm = CreateVm(state, session);

        await vm.OpenSongAsync(path, requested);

        Assert.Equal(path, Assert.Single(session.Loaded));
        Assert.Equal(PlayModeScreen.TuningCheck, vm.CurrentScreen);
        Assert.Equal(expected, vm.SelectedFlowArrangement!.Name);
        Assert.Equal(2, vm.FlowArrangements.Count);
        Assert.Equal(expected == "Bass" ? 4 : 6, vm.GuidedTuning.Strings.Count);
        Assert.Empty(vm.FlowError);
        Assert.False(vm.SongSelect.IsLibraryLoading);
    }

    [Fact]
    public async Task AnUnavailableSong_ShowsTheLibraryWithAnErrorInsteadOfAnotherSongsSetup()
    {
        var state = new ProjectState();
        var session = new Session(state);
        var vm = CreateVm(state, session);

        await vm.OpenSongAsync("lib:removed", "Lead");

        Assert.Equal(PlayModeScreen.SongSelect, vm.CurrentScreen);
        Assert.NotEmpty(vm.FlowError);
        Assert.Empty(session.Loaded);
        Assert.False(vm.SongSelect.IsLibraryLoading);
    }

    /// <summary>
    ///     Play Mode asks its injected file seam whether a remembered chart exists —
    ///     never the disk itself. A seam that says "gone" turns a file that IS on
    ///     disk into the library with an error…
    /// </summary>
    [Fact]
    public async Task ARememberedProject_TheFileSeamCallsGone_ShowsTheLibraryWithAnError()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "remembered.rscp");
        File.WriteAllText(path, "");
        var state = new ProjectState();
        var session = new Session(state);
        var vm = CreateVm(state, session, new SeamFiles());

        await vm.OpenSongAsync(path, "Bass:Bass");

        Assert.Equal(PlayModeScreen.SongSelect, vm.CurrentScreen);
        Assert.NotEmpty(vm.FlowError);
        Assert.Empty(session.Loaded);
    }

    /// <summary>…and a seam that says "there" opens a path that is not on disk.</summary>
    [Fact]
    public async Task ARememberedProject_TheFileSeamCallsPresent_LoadsWithoutTouchingTheDisk()
    {
        var path = Path.Combine(_dir, "only-the-seam-knows.rscp");
        var state = new ProjectState();
        var session = new Session(state);
        var vm = CreateVm(state, session, new SeamFiles(path));

        await vm.OpenSongAsync(path, "Bass:Bass");

        Assert.Equal(path, Assert.Single(session.Loaded));
        Assert.Equal(PlayModeScreen.TuningCheck, vm.CurrentScreen);
    }

    /// <summary>A scan's own-chart rows: one per path the seam says exists, each
    /// playable; the rest are left out.</summary>
    [Fact]
    public async Task AnOwnChartScan_KeepsOnlyThePathsTheFileSeamFinds()
    {
        var present = Path.Combine(_dir, "present.rscp");
        var gone = Path.Combine(_dir, "gone.rscp");
        var state = new ProjectState();
        var vm = CreateVm(state, new Session(state), new SeamFiles(present));

        var rows = await vm.SongSelect.ScanOwnChartsForTest([present, gone]);

        var row = Assert.Single(rows);
        Assert.Equal(present, row.ProjectPath);
        Assert.True(row.CanPlay);
    }

    private PlayModeViewModel CreateVm(ProjectState state, Session session, IExportTargetWriter? files = null)
    {
        var profiles = new PlayerProfileService(FakeLocalization.English(), null, Path.Combine(_dir, "profiles.json"), "Tester");
        var store = new PracticeProfileService(profiles, null, Path.Combine(_dir, "legacy.json"));
        return TestRigBuilders.PlayMode(profiles, new GameplayStatsService(store),
            new LeaderboardService(profiles), state, projectSession: session, files: files, localization: FakeLocalization.English());
    }

    /// <summary>Existence answered from a list; Play Mode needs nothing else of the seam.</summary>
    private sealed class SeamFiles(params string[] existing) : IExportTargetWriter
    {
        public bool FileExists(string path) => existing.Contains(path, StringComparer.OrdinalIgnoreCase);
        public bool DirectoryExists(string path) => throw new NotSupportedException();
        public void CopyFile(string source, string destination) => throw new NotSupportedException();
        public void WriteAllBytes(string path, byte[] bytes) => throw new NotSupportedException();
        public byte[] ReadAllBytes(string path) => throw new NotSupportedException();
        public Task<string> ReadAllTextAsync(string path) => throw new NotSupportedException();
        public Task WriteAllTextAsync(string path, string text) => throw new NotSupportedException();
        public Stream Create(string path) => throw new NotSupportedException();
        public void DeleteIfExists(string path) => throw new NotSupportedException();
        public string[] FindFiles(string folder, string pattern) => throw new NotSupportedException();
        public string DocumentsFolder => throw new NotSupportedException();
    }

    [Fact]
    public async Task ACancelledOrFailedProjectLoad_DoesNotOpenThePreviousChartsSetup()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "remembered.rscp");
        File.WriteAllText(path, "");
        var previous = new ChartProject();
        var state = new ProjectState { ActiveProject = previous };
        var session = new Session(state) { ReplaceProject = false };
        var vm = CreateVm(state, session);

        await vm.OpenSongAsync(path, "Bass:Bass");

        Assert.Same(previous, state.ActiveProject);
        Assert.Equal(PlayModeScreen.SongSelect, vm.CurrentScreen);
        Assert.Empty(vm.FlowArrangements);
        Assert.NotEmpty(vm.FlowError);
        Assert.False(vm.SongSelect.IsLibraryLoading);
    }

    [Fact]
    public async Task ASecondHomeRequest_WaitsForTheFirstLoadThenOpensTheNewerChoice()
    {
        Directory.CreateDirectory(_dir);
        var first = Path.Combine(_dir, "first.rscp");
        var second = Path.Combine(_dir, "second.rscp");
        File.WriteAllText(first, "");
        File.WriteAllText(second, "");
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = new ProjectState();
        var session = new Session(state) { BeforeLoad = path => path == first ? barrier.Task : Task.CompletedTask };
        var vm = CreateVm(state, session);

        var requestA = vm.OpenSongAsync(first, "Lead:Lead");
        Assert.Equal(first, Assert.Single(session.Loaded));
        var requestB = vm.OpenSongAsync(second, "Bass:Bass");
        var startedTogether = session.Loaded.Count > 1;
        barrier.SetResult();
        await Task.WhenAll(requestA, requestB);

        Assert.False(startedTogether, "Home requests must not load two charts concurrently.");
        Assert.Equal(new[] { first, second }, session.Loaded);
        Assert.Equal(PlayModeScreen.TuningCheck, vm.CurrentScreen);
        Assert.Equal("second", vm.FlowSongTitle);
        Assert.Equal("Bass", vm.SelectedFlowArrangement!.Name);
    }

    [Fact]
    public async Task AHomeRequest_DoesNotShowTheLibraryWhilePreparingTheSong()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "remembered.rscp");
        File.WriteAllText(path, "");
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = new ProjectState();
        var vm = CreateVm(state, new Session(state) { BeforeLoad = _ => barrier.Task });
        var screens = new List<PlayModeScreen>();
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(vm.CurrentScreen)) screens.Add(vm.CurrentScreen);
        };

        var request = vm.OpenSongAsync(path, "Bass:Bass");
        var screenWhileLoading = vm.CurrentScreen;
        barrier.SetResult();
        await request;

        Assert.Equal(PlayModeScreen.Start, screenWhileLoading);
        Assert.Equal(new[] { PlayModeScreen.TuningCheck }, screens);
    }

    private sealed class Session(ProjectState state) : IProjectSessionService
    {
        public Func<string, Task>? BeforeLoad { get; init; }
        public bool ReplaceProject { get; init; } = true;
        public List<string> Loaded { get; } = [];
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
        public async Task LoadFile(string path)
        {
            Loaded.Add(path);
            if (BeforeLoad != null) await BeforeLoad(path);
            if (!ReplaceProject) return;
            var project = new ChartProject();
            foreach (var type in new[] { ArrangementType.Lead, ArrangementType.Bass })
            {
                var arrangement = new Arrangement
                {
                    Name = type.ToString(), ArrangementType = type,
                    StringCount = type == ArrangementType.Bass ? 4 : 6
                };
                arrangement.Levels.Add(new ChartLevel());
                project.Arrangements.Add(arrangement);
            }
            state.ActiveProject = project;
            state.ActiveArrangement = project.Arrangements[0];
        }
        public Task LoadAudioFile(string path, bool generateBeats = true, bool preferMemoryPlayback = false) => Task.CompletedTask;
        public Task<bool> AttachAudioAsync(string path) => Task.FromResult(true);
        public Task LoadSilentTransportAsync(double length) => Task.CompletedTask;
        public Task OpenProject() => Task.CompletedTask;
        public Task<bool> SaveProjectAsync() => Task.FromResult(true);
        public Task<bool> SaveProjectAsAsync() => Task.FromResult(true);
        public IReadOnlyList<RecentProjectEntry> GetRecentProjects() => [];
        public void Dispose() { }
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }
}
