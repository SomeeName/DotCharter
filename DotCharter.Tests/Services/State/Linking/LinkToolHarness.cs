using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Fixing;
using DotCharter.Services.Editing.FretAnchors;
using DotCharter.Services.Editing.Maintenance;
using DotCharter.Services.Editing.Phrases;
using DotCharter.Services.Editing.Refret;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Services.Session;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.Services.State.Linking;

/// <summary>
///     <see cref="LinkHarness" /> behind a real <see cref="ChartMaintenanceService" />, so the
///     whole-chart tools run through the app's own commands. The FHP generator is a no-op unless
///     the test hands one in.
/// </summary>
internal sealed class LinkToolHarness
{
    public LinkToolHarness(IFretAnchorGenerationService? anchors = null)
    {
        H = LinkHarness.WithRhythm();
        var logger = new FakeAppLogger();
        var config = new FakeConfigurationService();
        var selection = new SelectionService();
        var transport = new TransportViewModel(new FakeAudioPlaybackService(), H.State, config, FakeLocalization.KeysOnly());
        var trackContexts = new TrackContextRegistry(H.State, selection);
        Editor = new ChartEditorViewModel(H.State, transport, H.History, selection, new TempoService(logger),
            trackContexts, FakeLocalization.KeysOnly());
        Maintenance = new ChartMaintenanceService(
            config,
            new NoOpImprovementService(),
            new NoOpSquashService(),
            new LevelGenerationService(),
            new PhraseIterationPropagationService(H.History, new EditingScope(H.State), logger),
            anchors ?? new NoOpAnchorGenerationService(),
            new NoOpProjectSessionService(),
            H.History,
            logger,
            H.State,
            Editor,
            new NoOpAutoFixService(),
            new RefretService(new FretAnchorGenerationService(), new FingeringGenerationService()),
            FakeLocalization.English());
    }

    public LinkHarness H { get; }
    public ChartEditorViewModel Editor { get; }
    public ChartMaintenanceService Maintenance { get; }
}

internal sealed class NoOpImprovementService : IChartImprovementService
{
    public ChartImprovementResult ImproveProject(ChartProject? project, AppConfig config,
        double totalAudioLengthMs) => new();
}

internal sealed class NoOpSquashService : ILevelSquashService
{
    public bool TrySquash(Arrangement arrangement) => false;
}

internal sealed class NoOpAnchorGenerationService : IFretAnchorGenerationService
{
    public IReadOnlyList<FretAnchor> GenerateAnchors(ChartLevel level,
        FractionalPosition? rangeStart = null, FractionalPosition? rangeEnd = null,
        IReadOnlyList<ChordTemplate>? chordTemplates = null, int capo = 0,
        bool bassArrangement = false, Func<ChordTemplate, ChordTemplate?>? freeGrip = null,
        Arrangement? tuning = null) =>
        Array.Empty<FretAnchor>();
}

internal sealed class NoOpAutoFixService : IArrangementAutoFixService
{
    public void Fix(Arrangement arrangement, ArrangementFixResult result) { }
}

internal sealed class NoOpProjectSessionService : IProjectSessionService
{
    public AppConfig AppConfig { get; } = new();
    public bool IsProjectDirty => false;

    /// <summary>No shell is listening, so a replace is always allowed.</summary>
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
