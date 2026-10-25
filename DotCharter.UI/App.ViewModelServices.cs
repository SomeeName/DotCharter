// Composition root, view-model slice: every shell/panel/editor/dialog view model, the
// update services and the yt-dlp downloader they drive, and the keybind handler that
// fronts IKeybindRegistry.

using DotCharter.Presentation.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Diagnostics;
using DotCharter.Tone.Diagnostics;
using DotCharter.Tone.Infrastructure.Shell;
using DotCharter.UI.Input;
using DotCharter.UI.ViewModels.Application;
using DotCharter.UI.ViewModels.Dialogs;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.Tone.UI.ViewModels.Settings;
using DotCharter.UI.ViewModels.Shell;
using Microsoft.Extensions.DependencyInjection;
using DotCharter.Services.Audio.Coordination;
using DotCharter.Services.Infrastructure.Input;
using DotCharter.Services.Infrastructure.Shell;
using DotCharter.Tone.Online.Tone3000;
using SongBrowserViewModel = DotCharter.UI.ViewModels.Panels.SongBrowserViewModel;

namespace DotCharter.UI;

public partial class App
{
    private static void AddViewModelServices(IServiceCollection services)
    {
        // Main UI Panels (Singletons are fine here because they exist permanently)
        services.AddSingleton<StemSeparationViewModel>();
        // The same run as a seam a screen can drive: chart from audio's Separate stems.
        services.AddSingleton<DotCharter.Services.Audio.StemSeparation.ISongStemSeparation>(sp =>
            sp.GetRequiredService<StemSeparationViewModel>());
        services.AddSingleton<HelpMenuViewModel>();
        services.AddSingleton<ImportMenuViewModel>();
        // The Tone designer's live edit goes to the shell's tone rig, which owns
        // the chart's tone preview.
        services.AddSingleton<IChartToneLiveEdit>(sp => sp.GetRequiredService<ToneRigViewModel>());
        AddToneRigViewModel(services);
        services.AddSingleton<ExportMenuViewModel>();
        services.AddSingleton<ProjectMenuViewModel>();
        services.AddSingleton<ShellDialogsViewModel>();
        services.AddSingleton<AiChartReviewViewModel>();
        services.AddSingleton<AiChartViewModel>();
        services.AddSingleton<DraftMergeReviewViewModel>();
        services.AddSingleton<ViewModels.Editor.AiChatDockViewModel>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<StatusBarViewModel>();
        services.AddSingleton<TransportViewModel>();
        // The transport and editor seams the Services-side coordinators take
        // (audio playback, chart maintenance, crash recovery, project session):
        // forwarding registrations onto the one view-model instance.
        services.AddSingleton<IEditorTransport>(sp => sp.GetRequiredService<TransportViewModel>());
        services.AddSingleton<DotCharter.Services.Audio.LivePlay.LiveTuningDriftMonitor>();
        services.AddSingleton<LivePlayMetersViewModel>();
        services.AddSingleton<LivePlayRunArchive>();
        services.AddSingleton<LivePlayViewModel>();
        services.AddSingleton<ViewModels.PlayMode.PlayModeToneRigViewModel>(sp =>
            new ViewModels.PlayMode.PlayModeToneRigViewModel(
                sp.GetRequiredService<ProjectState>(),
                sp.GetService<TransportViewModel>(),
                sp.GetService<LivePlayViewModel>(),
                sp.GetService<IAudioPlaybackService>(),
                sp.GetService<IHistoryService>(),
                () => sp.GetService<Tone.UI.ViewModels.PlayMode.PedalboardViewModel>(),
                () => sp.GetService<INamCaptureLibrary>(),
                () => sp.GetService<LiveInputChannelViewModel>(),
                () => sp.GetService<Tone.UI.ViewModels.PlayMode.RigReampViewModel>(),
                () => sp.GetService<DotCharter.Services.Ai.Match.IToneMatcher>(),
                () => sp.GetService<DotCharter.Services.Ai.Match.IStemIsolator>(),
                () => sp.GetService<DotCharter.Services.Ai.Match.IRigEstimator>(),
                () => sp.GetService<DotCharter.Services.Ai.Match.Model.IRigEstimatorModelDownloadService>(),
                () => sp.GetService<DotCharter.Services.Ai.Match.IToneSectionProposer>(),
                () => sp.GetService<DotCharter.Services.Ai.Match.IToneSetMatcher>(),
                () => sp.GetService<DotCharter.Services.Ai.Match.IToneNarrator>()));
        // The Play Mode settings modal: one per process, like Play Mode,
        // which takes it as `Settings` and attaches itself as its host.
        services.AddSingleton<ViewModels.PlayMode.PlayModeSettingsViewModel>();
        // Play Mode setlists: Play Mode takes it as Setlists and attaches itself.
        services.AddSingleton<ViewModels.PlayMode.PlaySetlistViewModel>();
        services.AddSingleton<ViewModels.PlayMode.PlayModeViewModel>(sp =>
        {
            Func<DotCharter.Tone.Audio.Abstractions.ILatencyCalibrationPrompt?> latencyPrompt =
                () => sp.GetService<DotCharter.Tone.Audio.Abstractions.ILatencyCalibrationPrompt>();
            return ActivatorUtilities.CreateInstance<ViewModels.PlayMode.PlayModeViewModel>(sp, latencyPrompt);
        });
        // Tone-rig pedalboard overlay VM (built lazily by PlayModeToneRigViewModel).
        // The TONE3000 services light up the browser's "NAM Captures" section.
        services.AddSingleton<Tone.UI.ViewModels.PlayMode.PedalboardViewModel>(sp =>
            new Tone.UI.ViewModels.PlayMode.PedalboardViewModel(
                sp.GetRequiredService<DotCharter.Tone.Audio.Dsp.IDspChainService>(),
                sp.GetRequiredService<DotCharter.Tone.Audio.Dsp.Presets.IDspTonePresetService>(),
                sp.GetRequiredService<ILocalizationService>(),
                sp.GetRequiredService<IRigConfigService>(),
                sp.GetRequiredService<ITone3000Service>(),
                sp.GetRequiredService<ITone3000AuthService>(),
                sp.GetRequiredService<INamCaptureLibrary>(),
                sp.GetRequiredService<IAudioPlaybackService>(),
                sp.GetRequiredService<IFileStorageService>(),
                // Every seat's chain, so the rig can be pointed at another PLAYER
                // (ABI v28+). A build with no native per-player surface registers
                // the null chains, so this always resolves.
                sp.GetRequiredService<DotCharter.Tone.Audio.Dsp.IPlayerDspChains>(),
                sp.GetRequiredService<IShellLauncher>(),
                // Marshals the bounded neural-amp status poll back onto the UI thread
                // (the native capture load is async — see ScheduleNeuralAmpStatusRefresh).
                sp.GetRequiredService<IUiDispatcher>(),
                // …and the profile switch, so the board rebuilds after a profile
                // switch reloads the chain underneath it (PedalboardProfileResyncTests).
                subscribeProfileSwitch: sp.GetService<DotCharter.Services.Gameplay.Profiles.IPlayerProfileService>()
                    is { } profiles
                    ? resync => profiles.ActiveProfileChanged += _ => resync()
                    : null,
                // The AI "Design a tone" panel. The rig assembly cannot name it
                // (it needs Services.Ai), so the host builds it from the seams.
                toneDesignPanel: seams => new ViewModels.PlayMode.ToneDesignViewModel(
                    () => sp.GetService<DotCharter.Tone.Ai.IToneDesignService>(),
                    seams.Apply, seams.SnapshotLive, seams.SaveUser, seams.Localization, seams.ModuleName,
                    post: seams.Post, logger: seams.Logger,
                    // "Copy prompt" for the ChatGPT copy & paste route.
                    clipboard: sp.GetService<ISystemClipboard>()))
            { SpectrumRenderer = sp.GetService<DotCharter.Services.Ai.Match.IOfflineRigHost>() });
        // The rig's DI recorder + re-amp transport (ABI v37): ONE per process, so a
        // take keeps playing whichever rig surface is open. Both AmpFxRigViewModel
        // builders hand it to their screen. The engine seam is the audio service
        // itself when it has one (the null transport does not: the panel then shows
        // itself disabled with its reason).
        services.AddSingleton<Tone.UI.ViewModels.PlayMode.RigReampViewModel>(sp =>
            new Tone.UI.ViewModels.PlayMode.RigReampViewModel(
                sp.GetService<IAudioPlaybackService>() as DotCharter.Tone.Audio.Reamp.IReampEngine,
                new DotCharter.Tone.Audio.Reamp.DiTakeLibrary(),
                sp.GetRequiredService<ILocalizationService>(),
                sp.GetService<IShellLauncher>(),
                sp.GetService<IFileStorageService>(),
                sp.GetService<IAppLogger>()));
        // The rig footer's guitar-input jack picker: one per rig screen (Play
        // Mode and the chart editor each build their own AmpFxRigViewModel),
        // not IDisposable, so a root-resolved transient is not rooted.
        services.AddTransient<LiveInputChannelViewModel>(sp => new LiveInputChannelViewModel(
            sp.GetService<IAudioDeviceControl>(),
            sp.GetService<IConfigurationService>(),
            sp.GetRequiredService<ILocalizationService>(),
            sp.GetService<IUiDispatcher>()));
        // The first-run wizard. Resolved by AppDialogService (its Onboarding
        // property), opened by the shell at startup and by Settings ▸ General.
        // Its tone page hands the pick to the pedalboard's preset browser so an
        // open Amp & FX board re-syncs (resolved at pick time: the wizard must not
        // construct the rig at startup just to hold a reference to it).
        services.AddSingleton<DotCharter.UI.ViewModels.Onboarding.OnboardingWizardViewModel>(sp =>
        {
            Func<Tone.UI.ViewModels.PlayMode.PedalboardViewModel?> pedalboard =
                () => sp.GetService<Tone.UI.ViewModels.PlayMode.PedalboardViewModel>();
            return ActivatorUtilities.CreateInstance<DotCharter.UI.ViewModels.Onboarding.OnboardingWizardViewModel>(sp, pedalboard);
        });
        services.AddSingleton<ChartEditorViewModel>();
        services.AddSingleton<IChartEditorSurface>(sp => sp.GetRequiredService<ChartEditorViewModel>());
        services.AddSingleton<DotCharter.UI.ViewModels.Editor.Linking.LinkedMeasuresViewModel>();
        services.AddSingleton<SheetMusicPaneViewModel>();
        services.AddSingleton<MultiTrackOverlayViewModel>();
        ViewModels.Panels.ChordShapeEditingModule.Register(services);
        services.AddSingleton<ViewModels.Editor.Inspector.AnnotationsInspectorViewModel>();
        services.AddSingleton<ViewModels.Editor.Inspector.ExportReadinessViewModel>();
        services.AddSingleton<ViewModels.Editor.Inspector.InspectorIssuesViewModel>();
        services.AddSingleton<ViewModels.Editor.Inspector.HandShapeEditorViewModel>();
        services.AddSingleton<ViewModels.Editor.Inspector.StringMaskViewModel>();
        services.AddSingleton<InspectorViewModel>();
        services.AddSingleton<SongOverviewViewModel>();
        services.AddSingleton<SongBrowserViewModel>();

        // Dialogs are singleton overlay view models so shell bindings and keybind guards share the same instances.
        // File > New project: the hosted dialog's tag + name sources, the silence bake
        // (a delegate so tests fake it without the CLI tools), the creator that runs on
        // the dialog's answer, and the launcher that chains the two.
        services.AddSingleton<DotCharter.Services.Session.INewProjectSources, DotCharter.Services.Session.NewProjectSources>();
        services.AddSingleton<DotCharter.Services.Import.Audio.PrepareImportAudio>(sp =>
        {
            var config = sp.GetRequiredService<IConfigurationService>();
            var logger = sp.GetRequiredService<IAppLogger>();
            return (src, dir, lead, trail) =>
            {
                var ffmpeg = config.LoadAppConfig().FfmpegPath;
                return DotCharter.Services.Import.Audio.ImportAudioPreparer.Prepare(
                    src, dir, lead, trail,
                    string.IsNullOrWhiteSpace(ffmpeg) ? null : ffmpeg,
                    logWarning: msg => logger.Warning(AppLogCategory.Project, msg));
            };
        });
        services.AddSingleton<DotCharter.Services.Session.NewProjectCreator>();
        services.AddSingleton<NewProjectLauncher>();
        services.AddSingleton<StemMixerViewModel>();
        services.AddSingleton<PhraseCatalogViewModel>(sp => new PhraseCatalogViewModel(
            sp.GetRequiredService<DotCharter.Services.Editing.Catalog.IPhraseCatalogService>(),
            sp.GetRequiredService<IGlobalEditingCoordinator>(),
            sp.GetRequiredService<ChartEditorViewModel>(),
            sp.GetRequiredService<TransportViewModel>(),
            sp.GetRequiredService<ProjectState>(),
            sp.GetRequiredService<ILocalizationService>(),
            action => Avalonia.Threading.Dispatcher.UIThread.Post(
                action, Avalonia.Threading.DispatcherPriority.Background)));
        // The latency wizard is hosted; its callers reach it through the one verb they need.
        services.AddSingleton<DotCharter.Services.Audio.Calibration.ICalibrationAudio>(sp =>
            new DotCharter.Services.Audio.Calibration.CalibrationAudio(sp.GetRequiredService<IAudioPlaybackService>()));
        services.AddSingleton<DotCharter.Tone.Audio.Abstractions.ILatencyCalibrationPrompt,
            LatencyCalibrationLauncher>();
        // Likewise "run setup again", so Settings need not name the wizard.
        services.AddSingleton<IFirstRunSetupPrompt>(sp =>
            sp.GetRequiredService<ViewModels.Onboarding.OnboardingWizardViewModel>());
        // Session Health dashboard — transient so each open takes a fresh snapshot.
        services.AddTransient<ViewModels.Diagnostics.DiagnosticsViewModel>();
        // The YouTube audio download registers in ContestedFeaturesModule (via DialogsModule), behind the public-build switch.
        services.AddSingleton<LyricsInspectorViewModel>();
        // The live tuner the Tuner dialog and the Tools popover's readout share.
        services.AddSingleton<DotCharter.Services.Audio.Tuning.LiveTuner>();
        services.AddSingleton<DotCharter.Presentation.Shared.TunerReadout>();
        services.AddSingleton<RecoveryLauncher>();
        // The update dialog as the update flow and Settings > Updates see it,
        // and the flow itself (manual check, Help-menu door, startup check).
        services.AddSingleton<DotCharter.Services.Update.IUpdatePrompt,
            UpdatePrompt>();
        services.AddSingleton<DotCharter.Services.Update.UpdateCheckFlow>();
        // The four file importers' doors (picker → read off the UI thread →
        // hosted dialog), the EOF project creation that follows its dialog, the
        // Guitar Pro apply half, and the chart-from-audio analysis the
        // Presentation layer cannot reference.
        services.AddSingleton<EofImportLauncher>();
        services.AddSingleton<DotCharter.Presentation.Screens.Dialogs.MidiImportLauncher>();
        services.AddSingleton<RsXmlImportLauncher>();
        services.AddSingleton<GpImportLauncher>();
        services.AddSingleton<DotCharter.Services.Import.Eof.EofProjectCreator>();
        services.AddSingleton<DotCharter.Services.Import.GuitarPro.GpImportApplier>();
        // ONE locator: the analyzer listens to the stem it finds, and the dialog asks it
        // whether to offer a separation. It reads the app's own stem cache.
        services.AddSingleton<DotCharter.Services.Import.Audio.IChartFromAudioStemLocator>(_ =>
            new DotCharter.Services.Import.Audio.ChartFromAudioStemLocator());
        services.AddSingleton<DotCharter.Services.Import.Audio.IChartFromAudioAnalyzer,
            DotCharter.Services.Import.Audio.ChartFromAudioAnalyzer>();
        services.AddSingleton<MessageDialogViewModel>();
        // The app-wide message box as the Services-side coordinators see it
        // (the project session's failed-save and replace-audio prompts).
        services.AddSingleton<IMessagePrompt>(sp => sp.GetRequiredService<MessageDialogViewModel>());

        // Keybinds
        services.AddSingleton<ChartKeybindHandler>(sp =>
        {
            return new ChartKeybindHandler(
                () => { return sp.GetRequiredService<IAppDialogService>().HasOpenDialog; },
                sp.GetRequiredService<IAppLogger>(),
                sp.GetRequiredService<IUsageTracker>());
        });
        services.AddSingleton<IKeybindRegistry>(sp => sp.GetRequiredService<ChartKeybindHandler>());
        // The dispatch-table refresh as Settings > Keybinds sees it.
        services.AddSingleton<DotCharter.Services.Infrastructure.Input.IKeybindDispatch>(sp =>
            sp.GetRequiredService<ChartKeybindHandler>());

        // The MIDI controller's editor binds. The presses come off the rig's
        // MIDI input (the one device the app opens); Settings ▸ Keybinds learns from
        // them, and the dispatch runs the bound verb where a key would run it.
        services.AddSingleton<IMidiTriggerInput>(sp => new MidiTriggerInput(
            sp.GetRequiredService<IAudioPlaybackService>() as DotCharter.Tone.Audio.Midi.IRigMidiInputSource,
            sp.GetRequiredService<IRigConfigService>(),
            sp.GetRequiredService<IUiDispatcher>()));
        services.AddSingleton(sp => new MidiBindingDispatch(
            sp.GetRequiredService<IMidiTriggerInput>(),
            () => sp.GetRequiredService<ProjectState>().Keybinds,
            sp.GetRequiredService<ChartKeybindHandler>(),
            () => sp.GetRequiredService<MainWindowViewModel>() is { IsChartEditorVisible: true, CommandPalette.IsOpen: false }));
    }

    /// <summary>The chart editor's tone surfaces: the rig overlay, Preview tones (and its Rocksmith target), the tuner readout.</summary>
    private static void AddToneRigViewModel(IServiceCollection services) =>
        services.AddSingleton<ToneRigViewModel>(sp => new ToneRigViewModel(
            sp.GetRequiredService<ProjectState>(),
            sp.GetRequiredService<TransportViewModel>(),
            sp.GetRequiredService<LivePlayViewModel>(),
            sp.GetRequiredService<StatusBarViewModel>(),
            sp.GetService<IStatusBarService>(),
            sp.GetRequiredService<DotCharter.Services.Editing.Maintenance.IChartMaintenanceService>(),
            sp.GetRequiredService<IHistoryService>(),
            sp.GetRequiredService<IAppLogger>(),
            sp.GetRequiredService<ILocalizationService>(),
            sp.GetRequiredService<IDialogs>(),
            () => sp.GetService<Tone.UI.ViewModels.PlayMode.PedalboardViewModel>(),
            () => sp.GetService<INamCaptureLibrary>(),
            () => sp.GetService<LiveInputChannelViewModel>(),
            () => sp.GetService<Tone.UI.ViewModels.PlayMode.RigReampViewModel>(),
            () => sp.GetService<RsTonePlaybackCoordinator>(),
            () => sp.GetService<DotCharter.Services.Ai.Generate.IRsToneGenerator>(),
            sp.GetRequiredService<IUiDispatcher>(),
            sp.GetService<DotCharter.Presentation.Shared.TunerReadout>(),
            () => sp.GetService<DotCharter.Tone.Audio.Tonelab.ITonelabSession>()));
}
