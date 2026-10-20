// Composition root, editing slice: selection/history/editing-scope, the tempo and
// beat services, the charting power tools, crash recovery + checkpoints, the
// project session, the dialog services, usage tracking, the song library and theming.

using DotCharter.Services.Diagnostics;
using DotCharter.Services.Editing.Refret;
using DotCharter.Services.Library;
using DotCharter.Services.Parsing;
using DotCharter.Services.Library.Cache;
using DotCharter.UI.Input;
using DotCharter.UI.Services;
using DotCharter.UI.Theming;
using DotCharter.UI.ViewModels.Shell;
using Microsoft.Extensions.DependencyInjection;
using DotCharter.Services.Editing.Maintenance;
using DotCharter.Services.Session;
using DotCharter.Services.Infrastructure.Recovery;

namespace DotCharter.UI;

public partial class App
{
    private static void AddEditingServices(IServiceCollection services)
    {
        services.AddSingleton<IStrictCollisionService, StrictCollisionService>();
        services.AddSingleton<ITrackContextRegistry, TrackContextRegistry>();
        services.AddSingleton<ISelectionService, SelectionService>();
        services.AddSingleton<ISelectLikeService, SelectLikeService>();
        services.AddSingleton<DotCharter.Services.Editing.Structure.IStructureGenerationService,
            DotCharter.Services.Editing.Structure.StructureGenerationService>();

        // User-authored "what does alike mean" patterns. A singleton because the
        // keybind registrar and the builder dialog must see the same list — the
        // registrar's dynamic registration re-reads it on every RefreshBinds.
        services.AddSingleton<DotCharter.Services.Editing.Selection.Patterns.MatchPatternStore>();
        services.AddSingleton<IGhostArrangementsService, GhostArrangementsService>();
        services.AddSingleton<IStackedArrangementsService, StackedArrangementsService>();
        services.AddSingleton<ITempoService, TempoService>();
        services.AddSingleton<IBeatScaleService, BeatScaleService>();
        services.AddSingleton<IBeatResyncService, BeatResyncService>();
        // NOTE: ISnapService is deliberately NOT registered. Nothing resolves it
        // — the editor snaps through ChartEditorViewModel.SnapToGrid (which also
        // magnet-snaps to a nearer existing sound) over TempoService.SnapToGrid.
        // SnapService/ISnapService survive as a pure, unit-tested helper only.
        services.AddSingleton<IClipboardService, ClipboardService>();
        services.AddSingleton<ChartingSessionLog>();
        services.AddSingleton<IHistoryService>(sp => new HistoryService(
            sp.GetRequiredService<IAppLogger>(),
            sp.GetRequiredService<IEditingScope>(),
            session: sp.GetRequiredService<ChartingSessionLog>()));
        services.AddSingleton<IEditingScope, EditingScope>();
        services.AddSingleton<IEventEditingService, EventEditingService>();
        // The ONE target ladder (0.35, editing rework stage 3): selection →
        // hand shape → pen, in front of every keyboard editing verb. Its two
        // implied rungs are gated on AppConfig.UseFastChartingInput, and its
        // pen rung is wired to IChartPenService below.
        services.AddSingleton<DotCharter.Services.Editing.Targets.IEditTargetService,
            DotCharter.Services.Editing.Targets.EditTargetService>();
        services.AddSingleton<IHighwayEditingService, HighwayEditingService>();
        // The editor's A/B loop region + per-selection audition (0.35, editing
        // rework stage 3). It shares LivePlayLoop's normalization and wrap test
        // with Live Play's practice repeater; AudioPlaybackCoordinator drives it
        // from the RAW playhead.
        services.AddSingleton<DotCharter.Services.Editing.Playback.IEditorLoopService,
            DotCharter.Services.Editing.Playback.EditorLoopService>();
        // The keyboard note-entry pen (0.35, editing rework stage 2). Inert
        // until AppConfig.UseFastChartingInput is on, so it ships alongside the
        // pointer placement path rather than replacing it.
        services.AddSingleton<DotCharter.Services.Editing.Pen.IChartPenService,
            DotCharter.Services.Editing.Pen.ChartPenService>();
        // The chord-shape palette: place a whole template at the pen, or
        // reshape the selection onto it; the Chord shapes pane and the Repeat
        // previous chord keybind drive it.
        services.AddSingleton<DotCharter.Services.Editing.ChordShapes.IChordShapePlacementService,
            DotCharter.Services.Editing.ChordShapes.ChordShapePlacementService>();
        // Named phrase catalog: a per-user folder of one-arrangement .rscp
        // fragments under <ConfigRoot>/phrases. It takes the rscp serializer for
        // the same reason BatchChartLintService does — a catalog entry IS a
        // project fragment, so the project serializer owns its field knowledge.
        services.AddSingleton<DotCharter.Services.Editing.Catalog.IPhraseCatalogService>(sp =>
            new DotCharter.Services.Editing.Catalog.PhraseCatalogService(
                sp.GetRequiredKeyedService<IChartSerializer>("rscp"),
                sp.GetService<IAppLogger>()));
        services.AddSingleton<IGlobalEditingCoordinator, GlobalEditingCoordinator>();
        services.AddSingleton<IChartImprovementService, ChartImprovementService>();
        services.AddSingleton<ILevelSquashService, LevelSquashService>();
        // Charting power tools (0.22): level generation is the inverse of the
        // squash (self-authored charts otherwise export as ONE difficulty), and
        // phrase propagation replays one iteration's content into every repeat.
        services.AddSingleton<ILevelGenerationService, LevelGenerationService>();
        services.AddSingleton<DotCharter.Services.Editing.Phrases.IPhraseIterationPropagationService,
            DotCharter.Services.Editing.Phrases.PhraseIterationPropagationService>();
        services.AddSingleton<IFretAnchorGenerationService, FretAnchorGenerationService>();
        // Chart from audio's merge: the ONE draft review the dialog holds and the
        // merge card over the timeline decides, phrase by phrase.
        services.AddSingleton<DotCharter.Services.Editing.DraftMerge.DraftMergeReviewHost>();
        services.AddSingleton<IFingeringGenerationService, FingeringGenerationService>();
        // Refret engine (v0.24 charting power tool): DP fretting optimizer —
        // same pitches, new string/fret positions, minimal hand movement.
        services.AddSingleton<IRefretService, RefretService>();
        // Add silence as a real splice: padded copies of the song and its stems, cached.
        services.AddSingleton<DotCharter.Services.Import.Audio.IAudioSilenceSplicer>(sp =>
            new DotCharter.Services.Import.Audio.AudioSilenceSplicer(
                sp.GetService<IConfigurationService>(),
                sp.GetService<DotCharter.Services.Audio.StemSeparation.Cache.StemCacheStore>(),
                sp.GetService<IAppLogger>()));
        services.AddSingleton<IChartMaintenanceService, ChartMaintenanceService>();
        // Crash recovery (0.12.0): snapshot store under ConfigRoot/recovery +
        // the coordinator that drives the autosave cadence and lifecycle.
        services.AddSingleton<DotCharter.Services.Infrastructure.Recovery.IRecoverySnapshotStore>(_ =>
            new DotCharter.Services.Infrastructure.Recovery.RecoverySnapshotStore());
        services.AddSingleton<ICrashRecoveryService, CrashRecoveryService>();
        // Named checkpoints (project history): restart-surviving whole-project
        // snapshots under ConfigRoot/checkpoints, keyed by project identity.
        services.AddSingleton<DotCharter.Services.State.Checkpoints.ICheckpointService>(_ =>
            new DotCharter.Services.State.Checkpoints.CheckpointService());
        services.AddSingleton<DotCharter.Services.Export.ISaveCompanionService,
            DotCharter.Services.Export.SaveCompanionService>();
        // File ▸ Export ▸ DotCharter song: the .dcsong writer's door, driven by the export dialog.
        services.AddSingleton<DotCharter.Services.Package.DcSong.IDcSongExporter>(sp =>
            new DotCharter.Services.Package.DcSong.DcSongExporter(sp.GetRequiredService<IAppLogger>(),
                () => sp.GetService<IConfigurationService>()?.LoadAppConfig().FfmpegPath));
        services.AddSingleton<IProjectSessionService, ProjectSessionService>();
        services.AddSingleton<ITimelineInteractionFactory, TimelineInteractionFactory>();
        services.AddSingleton<IDiagnosticsDialogService>(sp =>
            new DiagnosticsDialogService(() => sp.GetRequiredService<ViewModels.Diagnostics.DiagnosticsViewModel>()));
        services.AddSingleton<IAppDialogService, AppDialogService>();

        // Which commands, dialogs and shortcuts actually get used while charting —
        // local aggregate counts, never uploaded. See UsageStatistics for why this
        // counts rather than logs.
        services.AddSingleton<IUsageTracker>(sp =>
            new UsageTrackingService(logger: sp.GetRequiredService<IAppLogger>()));
        services.AddSingleton<DotCharter.Services.Library.Index.ILibraryIndex, DotCharter.Services.Library.Index.SqliteLibraryIndex>();
        // ONE store for the decoded-audio cache: the library service writes
        // through it and the Settings row measures/clears it, so both have to be
        // the same instance (a second one over the same root would sweep behind
        // the first's back and report numbers nobody could reconcile).
        // enableAutomaticSweep is what turns the cap on, and ONLY here: a store
        // constructed anywhere else (a package source's own fallback, a
        // test that news the parser) measures and clears but never prunes on its
        // own. A destructive default cost the owner 119 GB from one test run —
        // see LibraryAudioCacheStore's constructor remarks.
        services.AddSingleton<LibraryAudioCacheStore>(sp =>
            new LibraryAudioCacheStore(sp.GetService<IAppLogger>(), enableAutomaticSweep: true));
        // Each cached song converted ONCE to the device's rate, in place, so a
        // repeat open skips the load-time upsample. ProjectSessionService drives it.
        // Not before the stem cache's WEM-key adoption has finished: that one-off pass
        // proves a stem archive from before WEM-key adoption by the original WAV's exact bytes.
        services.AddSingleton(sp =>
            new LibraryDeviceRateAudio(sp.GetRequiredService<LibraryAudioCacheStore>(), sp.GetService<IAppLogger>(),
                () => DotCharter.Services.Audio.StemSeparation.Cache.WemKeyAdoption.HasCompleted(
                    sp.GetRequiredService<DotCharter.Services.Audio.StemSeparation.Cache.StemCacheStore>())));
        // The song library: ONE neutral LibraryService (walk, index, parallel
        // parse) over every registered ILibraryPackageSource, in scan order — an
        // plugin's package source first (App.Plugins.cs registers the plugins ahead of
        // this block), then .dcsong.
        // Each source reads, opens, prepares, covers, blocks and probes its own files;
        // the callers only ever see the service's interfaces.
        services.AddSingleton<ILibraryPackageSource>(sp =>
            new DotCharter.Services.Package.DcSong.DcSongLibrarySource(
                sp.GetRequiredService<IAppLogger>(),
                sp.GetRequiredService<LibraryAudioCacheStore>(),
                () => sp.GetService<IConfigurationService>()?.LoadAppConfig().FfmpegPath));
        services.AddSingleton<ILibraryService>(sp =>
            new LibraryService(
                sp.GetRequiredService<IAppLogger>(),
                sp.GetServices<ILibraryPackageSource>(),
                sp.GetService<DotCharter.Services.Library.Index.ILibraryIndex>()));
        // The Library asks the SAME service whether a song can play, and Library Doctor
        // probes through it, so each question reaches the source that owns the file.
        services.AddSingleton<ILibrarySongPlayability>(sp =>
            (ILibrarySongPlayability)sp.GetRequiredService<ILibraryService>());
        services.AddSingleton<DotCharter.Services.Infrastructure.LibraryDoctor.ILibraryPackageProbe>(sp =>
            (DotCharter.Services.Infrastructure.LibraryDoctor.ILibraryPackageProbe)sp.GetRequiredService<ILibraryService>());
        // Convert-on-select: prepares the selected song's audio through the
        // SAME service instance, so its per-folder gate is the one Open waits on.
        services.AddSingleton<DotCharter.Services.Library.Prefetch.ILibraryAudioPrefetcher>(sp =>
            new DotCharter.Services.Library.Prefetch.LibraryAudioPrefetcher(
                (DotCharter.Services.Library.Prefetch.ILibraryAudioPreparer)sp.GetRequiredService<ILibraryService>(),
                sp.GetRequiredService<IAppLogger>()));
        services.AddSingleton<DotCharter.Services.Infrastructure.LibraryDoctor.ILibraryDoctorService,
            DotCharter.Services.Infrastructure.LibraryDoctor.LibraryDoctorService>();
        services.AddSingleton<IThemeService, ThemeService>();
    }
}
