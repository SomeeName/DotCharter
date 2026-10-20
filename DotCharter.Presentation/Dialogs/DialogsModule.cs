using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Infrastructure.Features;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Presentation.Dialogs;

/// <summary>
///     Registers the app-wide <see cref="IDialogs" /> and the dialog screens
///     (target-architecture.md § 3.2). Each dialog view model is scoped: the host
///     resolves it from a fresh scope per open, so every open starts clean.
/// </summary>
public static class DialogsModule
{
    public static void Register(IServiceCollection services, bool contestedFeatures = ContestedFeatures.Enabled)
    {
        services.AddSingleton<DialogsSlot>();
        services.AddSingleton<IDialogs>(sp => sp.GetRequiredService<DialogsSlot>());
        services.AddScoped<IMessagePromptScreen, MessagePromptViewModel>();

        services.AddScoped<ChartEditDialogServices>();
        services.AddSingleton<DialogBasics>();
        services.AddScoped<IUnsavedChangesScreen, UnsavedChangesViewModel>();
        services.AddScoped<ISetBpmScreen, SetBpmViewModel>();
        services.AddScoped<IAiToneGenerateScreen, AiToneGenerateViewModel>();
        services.AddScoped<ITempoMapOperationScreen, TempoMapOperationViewModel>();
        services.AddScoped<IAlignBeatGridScreen, AlignBeatGridViewModel>();
        services.AddScoped<IAlignToRecordingScreen, AlignToRecordingViewModel>();
        services.AddSingleton<AlignToRecordingOffer>();
        // A factory: its editing services come from the app's graph, which a dialog-only container lacks.
        services.AddSingleton<DotCharter.Services.Editing.Tempo.RecordingAlignment.IRecordingAlignmentService>(sp =>
            ActivatorUtilities.CreateInstance<DotCharter.Services.Editing.Tempo.RecordingAlignment.RecordingAlignmentService>(sp));
        services.AddScoped<IUpdateAvailableScreen, UpdateAvailableViewModel>();
        services.AddScoped<IFhpScreen, FhpViewModel>();
        services.AddScoped<ILevelGenerationScreen, LevelGenerationViewModel>();
        services.AddScoped<IBeatScreen, BeatViewModel>();

        services.AddSingleton<DialogMemory>();
        services.AddScoped<IPasteSpecialScreen, PasteSpecialViewModel>();
        services.AddScoped<IArrangementSyncScreen, ArrangementSyncViewModel>();
        services.AddScoped<IKeyboardShortcutsScreen, KeyboardShortcutsViewModel>();
        services.AddScoped<IVocalTrackSettingsScreen, VocalTrackSettingsViewModel>();
        services.AddScoped<ChartFromAudioStemServices>();
        services.AddScoped<ChartFromAudioMerge>();
        services.AddScoped<IChartFromAudioScreen, ChartFromAudioViewModel>();
        services.AddScoped<IRsXmlImportScreen, RsXmlImportViewModel>();
        services.AddScoped<IMidiImportScreen, MidiImportViewModel>();
        services.AddScoped<IEofImportScreen, EofImportViewModel>();

        services.AddScoped<IHistoryScreen, HistoryViewModel>();
        services.AddScoped<ILibraryDoctorScreen, LibraryDoctorViewModel>();
        services.AddScoped<IChartDiffScreen, ChartDiffViewModel>();
        services.AddScoped<IBatchLintScreen, BatchLintViewModel>();
        services.AddScoped<IShowlightsScreen, ShowlightsViewModel>();
        services.AddScoped<ICheckpointsScreen, CheckpointsViewModel>();
        services.AddScoped<IGpImportScreen, GpImportViewModel>();
        services.AddScoped<IAboutScreen, AboutViewModel>();
        services.AddScoped<ITextDocumentScreen, TextDocumentViewModel>();
        services.AddScoped<ISongMetadataScreen, SongMetadataViewModel>();
        services.AddScoped<IRecoveryScreen, RecoveryViewModel>();
        services.AddScoped<ITunerScreen, TunerViewModel>();
        services.AddScoped<IEventScreen, EventViewModel>();
        services.AddScoped<IArrangementSettingsScreen, ArrangementSettingsViewModel>();
        services.AddScoped<INewProjectScreen, NewProjectViewModel>();
        services.AddScoped<IPatternBuilderScreen, PatternBuilderViewModel>();
        services.AddScoped<ILatencyWizardScreen, LatencyWizardViewModel>();
        services.AddScoped<IToneDesignerScreen, ToneDesignerViewModel>();
        services.AddScoped<TonePreviewSession>();
        // The public Rocksmith launcher, only when the install carries tools/tonelab/ (the DLL and its 32-bit helper).
        DotCharter.Tone.Audio.Tonelab.TonelabGameLauncher.Register(services, DotCharter.Tone.Infrastructure.Storage.InstallLocation.InstallRoot);
        // The in-game Preview target (tonelab): one session per app, attached to a game side already running.
        // A factory: its logger comes from the app's graph, which a dialog-only container lacks.
        services.AddSingleton<DotCharter.Tone.Audio.Tonelab.ITonelabSession>(sp =>
            new DotCharter.Tone.Audio.Tonelab.TonelabSession(sp.GetRequiredService<DotCharter.Tone.Logging.IAppLogger>(),
                sp.GetServices<DotCharter.Tone.Audio.Tonelab.IGameToneLauncher>(),
                // The audio path: seat 0 through the native insert, null without an audio engine (a dialog-only container).
                sp.GetService<DotCharter.Tone.Audio.Abstractions.IAudioPlaybackService>() is { } audio
                    ? DotCharter.Tone.Audio.ExternalInsert.JuceExternalProcessInsert.ForTonelab(audio)
                    : null));
        services.AddScoped<TonePreviewTargets>();
        services.AddScoped<DotCharter.Services.Editing.Tones.ToneGenerationRun>();
        services.AddScoped<ToneDesignerTools>();
        services.AddScoped<IToneCreatorScreen, ToneCreatorViewModel>();
        // A factory: its mapper and stem cache come from the app's graph, which a dialog-only container lacks.
        services.AddSingleton<DotCharter.Services.Ai.Generate.IToneCreator>(sp => DotCharter.Services.Ai.Generate.ToneCreator.CacheOnly(
            sp.GetRequiredService<DotCharter.Services.Ai.Generate.IRsToneMapper>(),
            sp.GetRequiredService<DotCharter.Services.Ai.Match.IStemIsolator>()));
        // The creator's audio check: the real game through tonelab's exchange (offline, only while no live preview
        // routes audio), the clone and metric of the match lane, the stems cache-only. Factories, for the same reason.
        services.AddSingleton<DotCharter.Tone.Audio.Tonelab.IGameToneRenderer>(sp => new DotCharter.Tone.Audio.Tonelab.TonelabOfflineRenderer(
            sp.GetRequiredService<DotCharter.Tone.Audio.Tonelab.ITonelabSession>(), sp.GetService<DotCharter.Tone.Logging.IAppLogger>()));
        services.AddSingleton<DotCharter.Services.Ai.Generate.IRsToneFeedbackLoop>(sp => DotCharter.Services.Ai.Generate.RsToneFeedbackLoop.CacheOnly(
            sp.GetRequiredService<DotCharter.Services.Ai.Generate.IRsToneMapper>(),
            sp.GetRequiredService<DotCharter.Services.Ai.Match.IRigRenderer>(),
            sp.GetRequiredService<DotCharter.Services.Ai.Match.IToneSimilarity>(),
            sp.GetRequiredService<DotCharter.Tone.Audio.Tonelab.IGameToneRenderer>(),
            sp.GetRequiredService<DotCharter.Services.Ai.Match.IStemIsolator>()));
        services.AddScoped<IRocksmithExportScreen, RocksmithExportViewModel>();
        services.AddScoped<IChartQualityReportScreen, ChartQualityReportViewModel>();
        services.AddSingleton<QualityAudioCheck>();
        services.AddSingleton<QualityReportChecks>();
        services.AddSingleton<DotCharter.Services.Export.Rocksmith.IRocksmithGameProbe, DotCharter.Services.Export.Rocksmith.RocksmithGameProbe>();
        services.AddScoped<RocksmithExportServices>();
        services.AddScoped<DcSongExportServices>();
        services.AddScoped<IDcSongExportScreen, DcSongExportViewModel>();
        // The YouTube audio download registers in one place, behind the public-build switch.
        ContestedFeaturesModule.Register(services, contestedFeatures);
    }
}
