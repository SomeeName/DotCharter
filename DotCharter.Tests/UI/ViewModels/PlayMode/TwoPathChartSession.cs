using DotCharter.Services.Session;

namespace DotCharter.Tests.UI.ViewModels.PlayMode;

/// <summary>
///     A project session that "loads" any path as a fresh two-path chart (Lead,
///     Bass) and records what it was asked to load. Play Mode's own-chart loader
///     runs through it unchanged, so a test can drive the real song flow — Home's
///     remembered song, a setlist — without a disk project or an audio device.
/// </summary>
internal sealed class TwoPathChartSession(ProjectState state) : IProjectSessionService
{
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

    public Task LoadFile(string path)
    {
        Loaded.Add(path);
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
        return Task.CompletedTask;
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
