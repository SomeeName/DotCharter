using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Tone.Diagnostics;
using DotCharter.UI.ViewModels.Application;

namespace DotCharter.Tests.UI.ViewModels.Application;

/// <summary>
///     The ⚠/⛔ status-bar chips show the CURRENT chart's lint issues, not the
///     accumulated log tail (live audit #7: the badge climbed 8→21 during pure
///     library browsing because every browsed PSARC's parse warnings appended
///     to RecentMessages and the counters counted THAT). These pin the split:
///     log messages move only the neutral session-log chip; the counters move
///     only through <see cref="StatusBarViewModel.SetChartIssueCounts" />.
/// </summary>
public sealed class StatusBarLintBadgeTests
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

    private static (StatusBarViewModel Vm, StatusBarService Service) Create()
    {
        var dispatcher = new ImmediateDispatcher();
        var service = new StatusBarService(dispatcher);
        var vm = new StatusBarViewModel(service, new ProjectState(),
            new FakeAudioPlaybackService(), dispatcher, new StubProfiles(),
            new FakeFileStorageService(), FakeLocalization.English());
        return (vm, service);
    }

    [Fact]
    public void AFreshSession_ShowsZeroIssues()
    {
        var (vm, _) = Create();
        Assert.Equal(0, vm.WarningCount);
        Assert.Equal(0, vm.ErrorCount);
        Assert.False(vm.HasWarnings);
        Assert.False(vm.HasErrors);
        Assert.False(vm.HasRecentLogMessages);
    }

    [Fact]
    public void LogMessages_MoveTheSessionLogChip_NeverTheIssueCounters()
    {
        var (vm, service) = Create();

        // What browsing the library does: warning log lines accumulate.
        service.RecentMessages.Insert(0, new StatusBarMessage
        {
            Timestamp = DateTimeOffset.Now, Level = "WARN",
            Category = AppLogCategory.Import, Message = "RS1 psarc has no audio"
        });
        service.RecentMessages.Insert(0, new StatusBarMessage
        {
            Timestamp = DateTimeOffset.Now, Level = "ERROR",
            Category = AppLogCategory.Import, Message = "manifest parse failed"
        });

        Assert.True(vm.HasRecentLogMessages);
        Assert.Equal(0, vm.WarningCount);
        Assert.Equal(0, vm.ErrorCount);
    }

    [Fact]
    public void ChartIssueCounts_DriveTheCounters()
    {
        var (vm, _) = Create();

        vm.SetChartIssueCounts(important: 2, warnings: 7);

        Assert.Equal(2, vm.ErrorCount);
        Assert.Equal(7, vm.WarningCount);
        Assert.True(vm.HasErrors);
        Assert.True(vm.HasWarnings);

        vm.SetChartIssueCounts(0, 0);
        Assert.False(vm.HasErrors);
        Assert.False(vm.HasWarnings);
    }

    [Fact]
    public void TheChips_ClickThroughToTheIssuesTab()
    {
        var (vm, _) = Create();
        var opened = 0;
        vm.OpenChartIssues = () => opened++;

        vm.ShowChartIssuesCommand.Execute(null);

        Assert.Equal(1, opened);
    }
}
