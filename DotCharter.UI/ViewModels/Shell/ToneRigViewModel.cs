// The chart editor's guitar-tone surfaces: the Amp & FX rig overlay (over the
// DI-singleton chain Play Mode also drives), the "Preview tones" toggle that plays
// the live guitar through the tone the playhead sits in, the Tools popover's tuner
// readout, and Tools > Generate tones with AI.
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Presentation.Shared;
using DotCharter.Services.Ai.Generate;
using DotCharter.Services.Audio.Coordination;
using DotCharter.Services.Editing.Maintenance;
using DotCharter.Tone.Diagnostics;
using DotCharter.Tone.Online.Tone3000;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using DotCharter.Tone.UI.ViewModels.Shell;
using DotCharter.UI.ViewModels.Application;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.PlayMode;
using DotCharter.Tone.UI.ViewModels.Settings;

namespace DotCharter.UI.ViewModels.Shell;

/// <summary>
///     The chart editor's guitar-tone surfaces, exposed as
///     <c>MainWindowViewModel.ToneRig</c>: the Amp &amp; FX rig overlay, the
///     "Preview tones" toggle, the Tools popover's tuner readout and Tools ▸
///     Generate tones with AI.
///     <para>
///         <b>Everything that reaches the tone rig is a factory, not an
///         instance.</b> This view model is a constructor dependency of the
///         shell, which is built before the window exists, and nothing on that
///         path may construct the DSP chain, the tone coordinator or the AI
///         lane. Each factory runs on first use: the overlay opening, the
///         prewarm asking for the rig screen, the toolbar reading the preview
///         toggle, or the menu verb running.
///     </para>
/// </summary>
public sealed partial class ToneRigViewModel : ViewModelBase, IDisposable, IChartToneLiveEdit
{
    private readonly ProjectState _projectState;
    private readonly TransportViewModel _transport;
    private readonly LivePlayViewModel _livePlay;
    private readonly StatusBarViewModel _statusBar;
    private readonly IHistoryService _history;
    private readonly IAppLogger _logger;
    private readonly ILocalizationService _localization;
    private readonly Func<PedalboardViewModel?> _pedalboardFactory;
    private readonly Func<INamCaptureLibrary?> _namCaptures;
    private readonly Func<LiveInputChannelViewModel?> _inputChannel;
    private readonly Func<RigReampViewModel?> _reamp;
    private readonly Func<RsTonePlaybackCoordinator?> _tones;
    private readonly Func<DotCharter.Tone.Audio.Tonelab.ITonelabSession?> _tonelab;
    private readonly IUiDispatcher _ui;
    private readonly AiToneGenerationRun _aiToneGeneration;
    private readonly IDialogs _dialogs;

    public ToneRigViewModel(
        ProjectState projectState,
        TransportViewModel transport,
        LivePlayViewModel livePlay,
        StatusBarViewModel statusBar,
        IStatusBarService? statusBarService,
        IChartMaintenanceService chartMaintenance,
        IHistoryService history,
        IAppLogger logger,
        ILocalizationService localization,
        IDialogs dialogs,
        Func<PedalboardViewModel?> pedalboard,
        Func<INamCaptureLibrary?> namCaptures,
        Func<LiveInputChannelViewModel?> inputChannel,
        Func<RigReampViewModel?> reamp,
        Func<RsTonePlaybackCoordinator?> tones,
        Func<IRsToneGenerator?> toneGenerator,
        IUiDispatcher ui,
        TunerReadout? tunerReadout = null,
        Func<DotCharter.Tone.Audio.Tonelab.ITonelabSession?>? tonelab = null)
    {
        _tonelab = tonelab ?? (() => null);
        _ui = ui;
        _projectState = projectState;
        _transport = transport;
        _livePlay = livePlay;
        _statusBar = statusBar;
        _history = history;
        _logger = logger;
        _localization = localization;
        _dialogs = dialogs;
        _pedalboardFactory = pedalboard;
        _namCaptures = namCaptures;
        _inputChannel = inputChannel;
        _reamp = reamp;
        _tones = tones;
        _aiToneGeneration = new AiToneGenerationRun(
            projectState, chartMaintenance, statusBarService, localization, logger, toneGenerator,
            (title, message) => dialogs.MessageAsync(title, message, isConfirmation: true));
        TunerReadout = tunerReadout;

        // The built-in chart synth always plays through the chart's own tones:
        // its switch reaches the tone preview, which holds the rig for it.
        _transport.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TransportViewModel.ChartSynthEnabled)) SyncChartSynthTones();
            // the player chose their own rig (or the chart's tones) for the synth.
            else if (e.PropertyName == nameof(TransportViewModel.ChartSynthPlaysOwnRig))
                _tonePreview?.SetChartSynthUsesOwnRig(_transport.ChartSynthPlaysOwnRig);
        };
    }

    /// <summary>Hands the synth's on/off to the tone preview. Turning the synth ON
    /// builds the preview (a user action, after first paint); turning it off only
    /// talks to one that exists.</summary>
    private void SyncChartSynthTones()
    {
        if (_transport.ChartSynthEnabled) TonePreview?.SetChartSynthPlaying(true);
        else _tonePreview?.SetChartSynthPlaying(false);
    }

    /// <summary>The Tools popover's live tuner readout (a lease on the shared
    /// <c>LiveTuner</c>, taken while the popover is open).</summary>
    public TunerReadout? TunerReadout { get; }

    /// <summary>Whether the shell currently shows its chrome (the activity rail).
    /// Set by the shell; unset reads as "no rail", which keeps the rig's ✕.</summary>
    internal Func<bool>? ShellChromeVisibleSource { get; set; }

    /// <summary>The tone drawer's AI panel asks for Settings ▸ AI; only the shell
    /// knows where that is.</summary>
    public event Action? OpenSettingsRequested;

    private bool IsShellChromeVisible => ShellChromeVisibleSource?.Invoke() ?? false;

    // ---- Amp & FX rig (in-house tone rig) editor overlay ------------------------
    //
    // The chart editor's entry point to the SAME tone rig Play Mode uses: a
    // scrimmed overlay hosting the SAME AmpFxRigView. The chain VM is the DI
    // singleton, so the editor and Play Mode drive one IDspChainService chain —
    // a tone dialled here is the tone in a run and vice versa. Resolved lazily
    // so a build without the native tone-rig surface still works
    // (IDspChainService falls back to NullDspChainService and the rig shows its
    // empty state).
    //
    // The overlay hosts the Fire rig view, never the retired PedalboardView (that gave the app
    // two pedalboards). The rig is its own view, so this overlay can name it, the ViewLocator
    // resolves it from its own VM, and both hosts point at it.
    [ObservableProperty] private bool _isPedalboardOpen;

    private PedalboardViewModel? _pedalboard;
    private readonly DeferredRigScreen _rigScreen = new();

    /// <summary>The shared tone-rig chain VM (the DI singleton Play Mode also
    /// drives), built by its factory on first use. Null on a build without
    /// the native tone-rig surface only if DI itself is unavailable.</summary>
    public PedalboardViewModel? Pedalboard =>
        _pedalboard ??= _pedalboardFactory();

    /// <summary>
    ///     The rig SCREEN over that chain — the same view model Play Mode's
    ///     overlay uses, built here because the two hosts have independent
    ///     screen state (which unit the dock shows, what the gear panel is
    ///     filtered to) over one shared chain.
    /// </summary>
    ///     <para>
    ///         Gated, not lazy: <c>MainWindow.axaml</c> binds this under the
    ///         hidden <c>PedalboardOverlay</c>, and a binding reads its source at
    ///         bind — before the first frame — so a <c>??=</c> getter built the
    ///         chain VM and this screen on every boot for an overlay nothing
    ///         shows (<see cref="DeferredRigScreen" />). Null until the overlay
    ///         opens or the prewarm asks (<see cref="EnsureRigScreen" />).
    ///     </para>
    public AmpFxRigViewModel? AmpFxRig => _rigScreen.Get(BuildRigScreenIfAny);

    /// <summary>
    ///     Builds the rig screen if it does not exist and tells the binding.
    ///     Called when the overlay opens and by <c>MainWindow.HiddenShellSurfaces</c>
    ///     from the post-first-paint prewarm drain.
    /// </summary>
    internal void EnsureRigScreen()
    {
        if (_rigScreen.Want(BuildRigScreenIfAny))
        {
            OnPropertyChanged(nameof(AmpFxRig));
        }
    }

    partial void OnIsPedalboardOpenChanged(bool value)
    {
        // Runs BEFORE the generated setter raises IsPedalboardOpen, so the
        // overlay turns visible over a bound screen, not over null — and over
        // cards that reflect chain state built elsewhere (Play Mode, a tone
        // convert). Here rather than in one command, because the rail's Rig row
        // opens it by writing this property: that door runs the sync too.
        if (value)
        {
            EnsureRigScreen();
            Pedalboard?.Sync();
        }

        SyncRigCloseButton();
    }

    /// <summary>The rig draws its own ✕ exactly when there is no rail to close it
    /// from. The shell calls this whenever its context changes.</summary>
    internal void SyncRigCloseButton()
    {
        if (_rigScreen.Current is { } rig)
        {
            rig.ShowCloseButton = !IsShellChromeVisible;
        }
    }

    private AmpFxRigViewModel? BuildRigScreenIfAny() => Pedalboard is { } rig ? BuildRigScreen(rig) : null;

    private AmpFxRigViewModel BuildRigScreen(PedalboardViewModel rig)
    {
        var screen = new AmpFxRigViewModel(rig, _namCaptures(), _inputChannel())
        {
            // The app's ONE capture tap — the same reading the pre-play signal
            // meter and Play Mode's rig show.
            InputPeakProvider = _livePlay.ReadInputPeak,
            // the transport's own drop-tune pedal, not a second one
            DropTune = RigDropTune.Over(_transport),
            // the footer's CPU readout: the engine's own callback load (ABI v46)
            CallbackLoadProvider = _transport.ReadCallbackLoad,
            // No ✕ where the rail is: its rig row is a two-way toggle over
            // `IsPedalboardOpen`, so it is the door in BOTH directions. Over a
            // full-bleed surface (Home, a player's Library) there is no rail.
            ShowCloseButton = !IsShellChromeVisible,
            // The DI recorder / re-amp transport is one per process (a take keeps
            // playing whichever rig surface is up), so every screen shares it.
            Reamp = _reamp()
        };
        screen.CloseRequested += (_, _) => IsPedalboardOpen = false;
        // The tone drawer's AI panel needs a door to Settings ▸ AI, and only a
        // host knows where that is: here it is the shell's own tab swap, in
        // Play Mode it is `OpenSettingsRequested`. (The drawer hangs off the
        // SHARED PedalboardViewModel, so whichever host builds last wins — both
        // mean the same thing by it.)
        if (rig.Browser.ToneDesign is ToneDesignViewModel design)
        {
            design.OpenSettings = () => OpenSettingsRequested?.Invoke();
        }

        return screen;
    }

    [RelayCommand]
    private void OpenPedalboard() => IsPedalboardOpen = true;

    // No close or toggle command: the rail's Rig row is a two-way toggle over
    // IsPedalboardOpen, the rig's own header ✕ raises `CloseRequested` (handled
    // above), and Esc goes through `PlayModeViewModel.TryCloseOverlay`. The
    // toolbar has no toggle of its own (the rail's row sits beside it); a
    // second door onto one boolean is how two of them end up disagreeing.

    // ── Tone preview (guitar input through the tone at the playhead) ────────

    private ChartTonePreviewController? _tonePreview;

    /// <summary>
    ///     "Preview tones" — the toolbar toggle that makes the live guitar
    ///     audible through the tone the PLAYHEAD sits in while charting, tone
    ///     changes included. Play Mode's in-game-tone path with the project's own
    ///     authored tones as the source; see
    ///     <see cref="ChartTonePreviewController" />.
    ///     <para>
    ///         Built on first read, like <see cref="Pedalboard" /> — the shell's
    ///         constructor runs before the window exists and nothing on it may
    ///         reach the audio engine. Null only when the tone coordinator's
    ///         factory yields nothing (DI itself unavailable).
    ///     </para>
    /// </summary>
    public ChartTonePreviewController? TonePreview => _tonePreview ??= BuildTonePreview();

    private ChartTonePreviewController? BuildTonePreview()
    {
        var tones = _tones();
        if (tones is null)
        {
            return null;
        }

        // The history service is how a MOVED tone change and a tempo edit under
        // one reach the preview: a marker's position is set on the object, so no
        // collection event fires, and the schedule the rig is driven from would
        // go on describing where the marker was.
        // The Rocksmith target (tonelab), when the app has a session: the controller owns it.
        var rocksmith = _tonelab() is { } session
            ? new EditorRocksmithTonePreview(tones, session, _localization, _logger, _ui)
            : null;
        var controller = new ChartTonePreviewController(
            tones, _projectState, _localization, _transport, _logger, _history, rocksmith);
        // The synth's tone source before anything asks it to play.
        controller.SetChartSynthUsesOwnRig(_transport.ChartSynthPlaysOwnRig);
        controller.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsTonePreviewOn));
            OnPropertyChanged(nameof(IsTonePreviewAvailable));
            OnPropertyChanged(nameof(TonePreviewTip));
        };
        if (rocksmith is not null)
        {
            rocksmith.PropertyChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(IsTonePreviewInRocksmith));
                OnPropertyChanged(nameof(IsTonePreviewRocksmithAvailable));
                OnPropertyChanged(nameof(TonePreviewRocksmithTip));
            };
            // A game already running with tonelab makes the switch choosable without a click.
            _ = rocksmith.ProbeAsync();
        }

        // The live tone read-out on the status rail. Wired HERE rather than by
        // injecting the coordinator into StatusBarViewModel: that view model is
        // built during shell DI, so taking the coordinator there would construct
        // the whole DSP chain on the startup path, for a chip that only means
        // anything once something drives the rig. This runs when the toolbar
        // first asks for the toggle instead.
        tones.ActiveToneChanged += info => _statusBar.SetActiveTone(info.ToneName, info.WasApproximated);
        tones.DrivingStopped += () => _statusBar.SetActiveTone(null);

        return controller;
    }

    /// <summary>Flat, never-null projections of <see cref="TonePreview" /> for the
    /// toolbar: a binding through a null controller would leave the toggle looking
    /// enabled.</summary>
    public bool IsTonePreviewOn => TonePreview?.IsOn == true;

    /// <inheritdoc cref="IsTonePreviewOn" />
    public bool IsTonePreviewAvailable => TonePreview?.IsAvailable == true;

    /// <inheritdoc cref="IsTonePreviewOn" />
    public string TonePreviewTip =>
        TonePreview?.Tip ?? _localization["Toolbar_TonePreview_NoProject"];

    [RelayCommand]
    private void ToggleTonePreview() => TonePreview?.Toggle();

    /// <summary>The preview plays through Rocksmith (tonelab) rather than the DotCharter rig.</summary>
    public bool IsTonePreviewInRocksmith => TonePreview?.Rocksmith?.IsChosen == true;

    /// <summary>Rocksmith can be chosen (a ready session), or started (a launcher, no game yet), or is chosen and can be switched back.</summary>
    public bool IsTonePreviewRocksmithAvailable => TonePreview?.Rocksmith is { } r && (r.IsAvailable || r.IsChosen || r.CanStart);

    /// <summary>What the Rocksmith switch does, or why it is disabled.</summary>
    public string TonePreviewRocksmithTip =>
        TonePreview?.Rocksmith?.Tip ?? _localization["Toolbar_TonePreviewRocksmith_NotConnected"];

    [RelayCommand]
    private void ToggleTonePreviewRocksmith()
    {
        if (TonePreview?.Rocksmith is not { } rocksmith) return;
        // No game yet but a launcher: start Rocksmith; the target picks itself once the game is ready.
        if (rocksmith.CanStart)
        {
            _ = rocksmith.StartAsync();
            OnPropertyChanged(nameof(IsTonePreviewInRocksmith));
            return;
        }

        rocksmith.Choose(!rocksmith.IsChosen);
        OnPropertyChanged(nameof(IsTonePreviewInRocksmith));
    }

    /// <summary>Leaving the chart editor stops the preview: the rig has ONE owner,
    /// and Play Mode (the tab most likely to be next) takes it over for its own
    /// run. A no-op — and free — when nothing is previewing. Reads the BACKING
    /// field, never the lazy property: the shell calls this on the pre-first-paint
    /// path when the boot tab is set, and building the controller there would
    /// resolve the tone coordinator before the window exists.</summary>
    internal void StopTonePreviewOutsideTheEditor() => _tonePreview?.StopOutsideTheEditor();

    /// <summary>Back in the chart editor: a chart synth that is still on picks the
    /// chart's tones up again. The backing field, for the same reason as above.</summary>
    internal void ResumeTonePreviewInTheEditor() => _tonePreview?.ResumeInTheEditor();

    // ── The Tone designer's live edit ────────────────────────────────
    //
    // A knob drag raises an edit per pointer move; each one converts the project's
    // tones and re-applies the tone at the playhead. Only the NEWEST pending edit is
    // kept, and it is flushed at Background priority: input outranks it, so a drag
    // coalesces by itself and the value the knob comes to rest on always lands.
    private (string Key, string Json)? _pendingToneEdit;

    void IChartToneLiveEdit.PreviewToneEdit(string key, string json)
    {
        // No controller: nothing drives the chart's tones, so there is nothing to update.
        if (_tonePreview is null) return;
        var flushQueued = _pendingToneEdit is not null;
        _pendingToneEdit = (key, json);
        if (!flushQueued) Avalonia.Threading.Dispatcher.UIThread.Post(FlushToneEdit, Avalonia.Threading.DispatcherPriority.Background);
    }

    void IChartToneLiveEdit.EndToneEditPreview()
    {
        FlushToneEdit();
        _tonePreview?.EndToneEditPreview();
    }

    private void FlushToneEdit()
    {
        if (_pendingToneEdit is not { } edit) return;
        _pendingToneEdit = null;
        _tonePreview?.PreviewToneEdit(edit.Key, edit.Json);
    }

    // ── Generate tones with AI (W6) ────────────────────────────────────────
    //
    // One Rocksmith tone per placed tone change, from the AI lane in
    // DotCharter.Services/Ai/Generate. The run owns the rail task, the progress, the
    // outcome line and the one-undo-step write; this is the menu verb over it.

    /// <summary>
    ///     Tools ▸ Tone &amp; sound ▸ Generate tones with AI. An async command, so the
    ///     menu item and the palette entry are disabled while a run is in flight; the
    ///     run itself reports on the status rail (<see cref="AiToneGenerationRun" />).
    ///     <para>
    /// It first asks for the charter's own words (<see cref="IAiToneGenerateScreen" />);
    ///         Cancel, or a dialog closed from outside, starts nothing.
    ///     </para>
    /// </summary>
    [RelayCommand]
    private async Task GenerateRocksmithTonesAsync()
    {
        if (await _dialogs.ShowAsync<IAiToneGenerateScreen, string?>() is not { } notes) return;
        await _aiToneGeneration.RunAsync(notes);
    }

    /// <summary>Puts the charter's own rig back if the app is torn down
    /// mid-preview, and drops the controller's subscriptions. Backing field:
    /// never build one on the way out. Idempotent: the shell disposes this
    /// before the session, and the container disposes it again.</summary>
    public void Dispose() => _tonePreview?.Dispose();
}
