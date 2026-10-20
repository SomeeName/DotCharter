using DotCharter.Tone.Diagnostics;
using DotCharter.UI.ViewModels.Application;
using DotCharter.UI.ViewModels.Shell;

namespace DotCharter.Tests.UI.ViewModels.Application;

/// <summary>
///     What the status rail and the window caption call the open song. A song Play Mode
///     loaded (or any import not yet saved) has no project path, and both said "Untitled"
///     and "DotCharter - Combo" over a chart that knows its own artist and title.
/// </summary>
public sealed class StatusBarProjectNameTests
{
    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;
        public void Post(Action action) => action();
        public void PostNormal(Action action) => action();
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
        public Task<T> InvokeAsync<T>(Func<T> func) => Task.FromResult(func());
        public Task<T> InvokeAsync<T>(Func<Task<T>> func) => func();
    }

    private static (StatusBarViewModel Vm, ProjectState State) Create()
    {
        var dispatcher = new ImmediateDispatcher();
        var state = new ProjectState();
        var vm = new StatusBarViewModel(new StatusBarService(dispatcher), state,
            new FakeAudioPlaybackService(), dispatcher, new NullProfiles(),
            new FakeFileStorageService(), FakeLocalization.English());
        return (vm, state);
    }

    [Fact]
    public void AnUnsavedSong_IsNamedByItsChart()
    {
        var (vm, state) = Create();
        var project = new ChartProject();
        project.Chart.Artist = "3 Doors Down";
        project.Chart.SongName = "When I'm Gone";

        state.ActiveProject = project;

        Assert.Equal("3 Doors Down - When I'm Gone", vm.ProjectName);
    }

    [Fact]
    public void AnUnsavedChartWithNoSongName_IsUntitled_InTheUsersLanguage()
    {
        var (vm, state) = Create();
        var project = new ChartProject();
        project.Chart.SongName = "";

        state.ActiveProject = project;

        Assert.Equal(FakeLocalization.English()["Project_Untitled"], vm.ProjectName);
    }

    [Theory]
    [InlineData("3 Doors Down - When I'm Gone", false, "3 Doors Down - When I'm Gone — DotCharter")]
    [InlineData("3 Doors Down - When I'm Gone", true, "3 Doors Down - When I'm Gone* — DotCharter")]
    [InlineData("", false, "DotCharter")]
    [InlineData("", true, "DotCharter*")]
    public void TheWindowCaption_NamesTheSong_NotTheArrangement(string project, bool dirty, string expected) => Assert.Equal(expected, MainWindowViewModel.ComposeWindowTitle(project, dirty));

    private sealed class NullProfiles : DotCharter.Services.Gameplay.Profiles.IPlayerProfileService
    {
        private readonly DotCharter.Services.Gameplay.Profiles.PlayerProfile _active = new() { Id = Guid.NewGuid(), Name = "Test" };
        public IReadOnlyList<DotCharter.Services.Gameplay.Profiles.PlayerProfile> Profiles => new[] { _active };
        public DotCharter.Services.Gameplay.Profiles.PlayerProfile ActiveProfile => _active;
        public event Action<DotCharter.Services.Gameplay.Profiles.PlayerProfile>? ActiveProfileChanged { add { } remove { } }
        public string GetDataDirectory(Guid id) => Path.GetTempPath();
        public void InvalidateCache() { }
        public DotCharter.Services.Gameplay.Profiles.PlayerProfile Create(string name, string? accentColor = null) => _active;
        public void Rename(Guid id, string newName) { }
        public void SetLeftyFlip(Guid id, bool lefty) { }
        public void SetTuningMatch(Guid id, string myTuningId, bool playInMyTuning) { }
        public bool SwitchTo(Guid id) => false;
        public bool Delete(Guid id) => false;
        public bool SetAvatar(Guid id, string sourceImagePath) => false;
        public string? GetAvatarPath(Guid id) => null;
        public bool ExportProfile(Guid id, string zipPath) => false;
        public DotCharter.Services.Gameplay.Profiles.PlayerProfile? ImportProfile(string zipPath) => null;
    }
}
