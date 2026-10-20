using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Tone.Diagnostics;
using DotCharter.UI.ViewModels.Application;

namespace DotCharter.Tests.UI.ViewModels.Application;

/// <summary>
///     The unsaved-changes marker. Until 2026-08 NOTHING on screen showed dirty
///     state: <c>MainWindowViewModel.WindowTitle</c> built the standard trailing
///     asterisk and was bound by nothing, the window's <c>Title</c> was a
///     hard-coded literal (and BorderOnly decorations draw no caption anyway),
///     and <c>StatusBarViewModel.IsProjectDirty</c> was hard-assigned
///     <c>false</c> on every refresh — the user learned about unsaved work from
///     the close prompt, the one moment the indicator exists to prevent.
///     These pin the three repaired pieces: the delegate-driven flag, the
///     re-read (not reset) on a project switch, and the bindings that finally
///     consume both properties.
/// </summary>
public sealed class StatusBarDirtyStateTests
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

    private sealed class StubProfiles : IPlayerProfileService
    {
        private readonly PlayerProfile _active = new() { Id = Guid.NewGuid(), Name = "Test" };
        public IReadOnlyList<PlayerProfile> Profiles => new[] { _active };
        public PlayerProfile ActiveProfile => _active;
        public event Action<PlayerProfile>? ActiveProfileChanged { add { } remove { } }
        public string GetDataDirectory(Guid id) => Path.GetTempPath();
        public void InvalidateCache() { }
        public PlayerProfile Create(string name, string? accentColor = null) => _active;
        public void Rename(Guid id, string newName) { }
        public void SetLeftyFlip(Guid id, bool lefty) { }
        public void SetTuningMatch(Guid id, string myTuningId, bool playInMyTuning) { }
        public bool SwitchTo(Guid id) => false;
        public bool Delete(Guid id) => false;
        public bool SetAvatar(Guid id, string sourceImagePath) => false;
        public string? GetAvatarPath(Guid id) => null;
        public bool ExportProfile(Guid id, string zipPath) => false;
        public PlayerProfile? ImportProfile(string zipPath) => null;
    }

    private static (StatusBarViewModel Vm, ProjectState State) Create()
    {
        var dispatcher = new ImmediateDispatcher();
        var state = new ProjectState();
        var vm = new StatusBarViewModel(new StatusBarService(dispatcher), state,
            new FakeAudioPlaybackService(), dispatcher, new StubProfiles(),
            new FakeFileStorageService(), FakeLocalization.English());
        return (vm, state);
    }

    [Fact]
    public void TheMarker_FollowsTheSessionDirtySource()
    {
        var (vm, _) = Create();
        var dirty = false;
        vm.DirtyStateSource = () => dirty;

        vm.RefreshDirtyState();
        Assert.False(vm.IsProjectDirty);

        dirty = true;
        vm.RefreshDirtyState();
        Assert.True(vm.IsProjectDirty);

        dirty = false;
        vm.RefreshDirtyState();
        Assert.False(vm.IsProjectDirty);
    }

    [Fact]
    public void AProjectSwitch_ReReadsTheSource_InsteadOfResettingIt()
    {
        // The attach-audio path changes CurrentProjectPath while the session
        // stays dirty; the old RefreshProjectStatus hard-assigned false here.
        var (vm, state) = Create();
        vm.DirtyStateSource = () => true;

        state.ActiveProject = new ChartProject();

        Assert.True(vm.IsProjectDirty);
    }

    [Fact]
    public void WithNoShellListening_TheMarkerStaysOff()
    {
        var (vm, state) = Create();
        state.ActiveProject = new ChartProject();
        vm.RefreshDirtyState();
        Assert.False(vm.IsProjectDirty);
    }

    /// <summary>The two consumers that make the flag VISIBLE: the window caption and
    /// the status rail's dot (the title bar's copy went on 2026-09-23 — it repeated the
    /// status rail). Each was the missing half of a correct-looking property once, so each
    /// binding is pinned as text: remove one and dirty state goes dark there with no
    /// compile error.</summary>
    [Fact]
    public void TheDirtyState_IsActuallyBound_OnEverySurfaceThatClaimsIt()
    {
        var mainWindow = MainWindowSource.ReadAllText();
        // Taskbar / alt-tab caption: the asterisk WindowTitle always built.
        Assert.Contains("Title=\"{Binding WindowTitle", mainWindow);

        var statusBar = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Shell", "StatusBar.axaml")));
        Assert.Contains("IsVisible=\"{Binding IsProjectDirty}\"", statusBar);

        // The shell must FEED the delegate, or every marker above is bound to
        // a flag nothing writes.
        var shell = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.UI", "ViewModels", "Shell", "MainWindowViewModel.cs")));
        Assert.Contains("StatusBar.DirtyStateSource", shell);
        Assert.Contains("StatusBar.RefreshDirtyState()", shell);
    }
}
