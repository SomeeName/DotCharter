using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Core.Graphics;
using DotCharter.Presentation.Screens.Settings;
using DotCharter.Rendering.Vulkan;
using DotCharter.UI.Theming;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.UI.ViewModels.PlayMode;

/// <summary>
///     The Play Mode settings modal (§6.5) — a curated, game-styled settings menu
///     (centred scrimmed card) that opens from the launch screen and the pause
///     menu, so settings can be changed mid-song WITHOUT leaving the paused run
///     and WITHOUT dropping into the editor's Settings tab. Exposed as
/// <c>PlayModeViewModel.Settings</c>, as the rig is as
///     <c>PlayModeViewModel.ToneRig</c>; the modal's views bind
///     <c>{Binding Settings.X}</c>.
///     <para>
///         It is a front-end over the EXISTING settings state and never duplicates
///         the full Settings persistence:
///         audio volumes bind the shared <c>TransportViewModel</c>; hit window and
///         chord strictness bind <c>LivePlayViewModel</c> (which persists them);
///         speed and the two tone rows bind Play Mode itself; the graphics
///         quick-toggles bind the shared <see cref="ProjectState" /> (the renderer
///         reads them per frame). What this type OWNS is the gameplay metronome,
///         claps, count-in, rock meter and resume rewind, the perf knobs, the
///         appearance rows, the search, the meter, the latency chip, the Devices
///         and Keybinds sections and the per-section reset — written to the SAME
///         <c>AppConfig</c> fields Settings ▸ Play / Graphics / Appearance save.
///     </para>
///     <para>
///         <b>What it reads <see cref="ProjectState" /> for</b> (the ~38 uses the
///         old partials made of it): the sixteen graphics/appearance quick-toggles
///         it mirrors into <c>AppConfig</c> on close and puts back per section on
///         reset; <c>Preview3DCameraFill</c>, which the camera slider live-applies;
///         and the live <c>Keybinds</c> table the Keybinds section edits. All of
///         it is the shared singleton state, never Play Mode's.
///     </para>
///     <para>
///         <b>What it reads from Play Mode</b> goes through
///         <see cref="IPlayModeSettingsHost" />: whether a run's side effects are
///         live, the practice speed, the two tone rows, and the input tap. Split
///         over two files: this root (fields, constructor, open/close) and
///         <c>PlayModeSettingsViewModel.Sections.cs</c> (the values and the
///         modal's surface).
///     </para>
/// </summary>
public sealed partial class PlayModeSettingsViewModel : ObservableObject, IDisposable
{
    private readonly ProjectState _projectState;
    private readonly ILocalizationService _localization;
    private readonly IConfigurationService? _configService;
    private readonly TransportViewModel? _transport;
    private readonly IAudioPlaybackService? _audio;
    private readonly IThemeService? _themeService;
    private readonly IVulkanRenderService? _vulkanRenderer;
    private readonly Input.IKeybindRegistry? _keybindRegistry;

    private IPlayModeSettingsHost? _host;

    // Guard: loading the persisted values on open must not re-fire the live-apply
    // side effects (they are already applied app-wide at startup / by the editor).
    private bool _loadingGameplaySettings;

    private bool _keybindsTouched;

    /// <summary>The live <c>Keybinds</c> table this modal is watching, so the
    /// unsubscribe reaches the instance it subscribed to even after the editor
    /// replaced <c>ProjectState.Keybinds</c>.</summary>
    private KeybindConfig? _watchedKeybinds;

    private Tone.UI.ViewModels.Shell.CoalescedFilterPass? _gameplaySettingsSearchPass;

    [ObservableProperty] private bool _isGameplaySettingsOpen;

    /// <summary>Shell collaborators are optional so the modal stays unit-testable
    /// without the editor; DI supplies all of them in the app.</summary>
    public PlayModeSettingsViewModel(
        ProjectState projectState,
        ILocalizationService localization,
        IConfigurationService? configService = null,
        TransportViewModel? transport = null,
        IAudioPlaybackService? audio = null,
        IThemeService? themeService = null,
        IVulkanRenderService? vulkanRenderer = null,
        AudioSettingsViewModel? audioDevices = null,
        Input.IKeybindRegistry? keybindRegistry = null)
    {
        _projectState = projectState;
        _localization = localization;
        _configService = configService;
        _transport = transport;
        _audio = audio;
        _themeService = themeService;
        _vulkanRenderer = vulkanRenderer;
        _keybindRegistry = keybindRegistry;
        AudioDevices = audioDevices;
        GameplayFpsOptions = [localization["Settings_Graphics_MaxFps_Unlimited"], "30", "60", "120", "144", "240"];
    }

    /// <summary>Ties the modal to the Play Mode that hosts it. Called once, by
    /// <c>PlayModeViewModel</c>'s constructor.</summary>
    internal void AttachHost(IPlayModeSettingsHost host) => _host = host;

    /// <summary>"Full settings…": the shell owns the tab swap, so Play Mode
    /// forwards this as its own <c>OpenSettingsRequested</c>.</summary>
    public event Action? OpenFullSettingsRequested;

    [RelayCommand]
    private void OpenGameplaySettings()
    {
        // Load the AppConfig-backed values this overlay owns so its controls show
        // the persisted settings (the shared instances the rest of the overlay
        // binds — Transport, LivePlay, ProjectState — already carry live state).
        // The guard stops the perf/appearance live-apply side effects re-firing
        // for values that are already applied app-wide at startup.
        _loadingGameplaySettings = true;
        try
        {
            var config = _configService?.LoadAppConfig();
            if (config != null)
            {
                GameplayMetronomeEnabled = config.GameplayMetronomeEnabled;
                GameplayClapsEnabled = config.GameplayClapsEnabled;
                GameplayCountInIndex = GameplayCountInBeatsToIndex(config.GameplayCountInBeats);
                GameplayRockMeterEnabled = config.GameplayRockMeterEnabled;
                GameplayResumeRewindIndex = GameplayResumeRewindValueToIndex(config.GameplayResumeRewindMs);
                GameplayMaxFpsIndex = GameplayFpsValueToIndex(config.MaxFps);
                GameplayRenderScalePercent = GraphicsSettingsPolicy.ClampRenderScalePercent(config.RenderScalePercent);
                GameplayCameraDistancePercent =
                    200 - GraphicsSettingsPolicy.ClampCameraFillPercent(config.Preview3DCameraFill);
                // The APPLIED factor, not config.UiScale: under Auto the shell
                // is running a derived number and the persisted one is the
                // manual arm's keepsake, so seeding from config would show a
                // slider that disagrees with the screen behind it.
                GameplayUiScale = Services.UiScaleController.Scale;
                SelectedStringColorPreset = StringColorPresets.GetById(config.StringColorPresetId);
            }
        }
        finally
        {
            _loadingGameplaySettings = false;
        }

        // The Devices section shows the REAL enumerated devices. `Open()` fills
        // the cheap fields synchronously and probes WASAPI/ASIO on the
        // threadpool (a USB enumeration is a 200 ms+ round trip), marshalling
        // the lists back itself — so this must not be awaited and must not run
        // on the modal's first layout.
        AudioDevices?.Open();

        // A modal opens un-searched, on the section the user left it on.
        GameplaySettingsQuery = string.Empty;
        WatchKeybinds(true);

        IsGameplaySettingsOpen = true;

        // One synchronous tick so the meter and the latency chip are populated
        // in the FIRST frame; the view's timer takes over from there.
        PollGameplaySettingsMeters();
    }

    [RelayCommand]
    private void CloseGameplaySettings() => IsGameplaySettingsOpen = false;

    /// <summary>The deep escape hatch: leave the curated overlay for the full
    /// editor Settings surface (for anything this menu doesn't expose). The shell
    /// owns the tab swap via <see cref="OpenFullSettingsRequested" />.</summary>
    [RelayCommand]
    private void OpenFullSettings()
    {
        IsGameplaySettingsOpen = false;
        OpenFullSettingsRequested?.Invoke();
    }

    partial void OnIsGameplaySettingsOpenChanged(bool value)
    {
        // Persist the overlay-owned AppConfig settings when it closes — covers the
        // ✕ / Close button AND the Esc → TryCloseOverlay path (both flip this
        // false), so a change is never lost.
        if (value) return;
        PersistGameplaySettings();
        ApplyGameplaySettingsToLiveRun();
        PersistGameplayKeybinds();
        WatchKeybinds(false);
    }

    /// <summary>
    ///     Re-arms the two per-RUN audio toggles this overlay owns onto the run
    ///     that is already going.
    ///     <para>
    ///         The overlay opens from the PAUSE menu precisely so settings can be
    ///         changed mid-song without leaving the run — but the metronome and
    ///         the claps are armed once, in <c>BeginGameplay</c>, from AppConfig.
    ///         Persisting alone therefore left a mid-run flip doing nothing at all
    ///         until the NEXT run: the player turns the metronome on where they
    ///         need it, resumes, and hears silence.
    ///     </para>
    ///     <para>
    ///         Gated on <see cref="IPlayModeSettingsHost.RunSideEffectsApplied" /> —
    ///         the run chain's flag that says a run's side effects are live.
    ///         Writing these outside a run would arm the EDITOR's metronome
    ///         (<c>AudioPlaybackCoordinator</c> ORs the two toggles), which is not
    ///         this overlay's to touch, and <c>EndRunSideEffects</c> clears both on
    ///         the way out either way.
    ///     </para>
    /// </summary>
    private void ApplyGameplaySettingsToLiveRun()
    {
        if (_host is not { RunSideEffectsApplied: true } host || _transport == null) return;
        _transport.GameplayMetronomeActive = GameplayMetronomeEnabled;
        _transport.GameplayClapsActive = GameplayClapsEnabled;
        // …and the PRACTICE SPEED row, which is the same shape: the overlay's
        // Speed picker binds PracticeSpeedPercent, and the only place that
        // reaches the transport is BeginGameplay. Slowing the song down from the
        // pause menu — the reason a player opens this overlay mid-run at all —
        // therefore did nothing until the NEXT run. Clamped exactly as
        // BeginGameplay clamps it, and the run's own _preRunSpeed baseline is
        // untouched, so the unwind still restores the editor's speed.
        _transport.PlaybackSpeed = Math.Clamp(host.PracticeSpeedPercent, 25, 100) / 100.0;
    }

    private void PersistGameplaySettings()
    {
        if (_configService == null) return;
        var config = _configService.LoadAppConfig();
        config.GameplayMetronomeEnabled = GameplayMetronomeEnabled;
        config.GameplayClapsEnabled = GameplayClapsEnabled;
        config.GameplayCountInBeats = GameplayCountInIndexToBeats(GameplayCountInIndex);
        config.GameplayRockMeterEnabled = GameplayRockMeterEnabled;
        config.GameplayResumeRewindMs = GameplayResumeRewindIndexToValue(GameplayResumeRewindIndex);

        // Graphics quick-toggles live-bind at ProjectState (the renderer reads
        // them per frame); mirror the current values into the SAME AppConfig
        // fields Settings → Graphics persists so a flip survives a restart.
        config.EnableNoteHitExplosions = _projectState.EnableNoteHitExplosions;
        config.EnableEnhancedHitFeedback = _projectState.EnableEnhancedHitFeedback;
        config.EnableBeatPulse = _projectState.EnableBeatPulse;
        config.EnableVignette = _projectState.EnableVignette;
        config.EnableDepthFog = _projectState.EnableDepthFog;
        config.EnableHighwayNearFade = _projectState.EnableHighwayNearFade;
        config.EnableShowlightBeams = _projectState.EnableShowlightBeams;
        config.EnableShowlightFog = _projectState.EnableShowlightFog;
        config.EnableCrowdBackground = _projectState.EnableCrowdBackground;
        config.EnableHighwayLighting = _projectState.EnableHighwayLighting;
        config.EnableBloom = _projectState.EnableBloom;
        config.EnableStringShimmer = _projectState.EnableStringShimmer;
        config.EnableHitSparks = _projectState.EnableHitSparks;
        config.EnableFilmicTonemap = _projectState.EnableFilmicTonemap;
        config.EnableAnchorPreview = _projectState.EnableAnchorPreview;
        config.EnableAnchorSweep = _projectState.EnableAnchorSweep;

        // Perf knobs (fps cap + render scale) + UI scale live-apply via the same
        // statics/service Settings → Graphics/Appearance drive; mirror on close.
        config.MaxFps = GraphicsSettingsPolicy.ClampMaxFps(GameplayFpsIndexToValue(GameplayMaxFpsIndex));
        config.RenderScalePercent =
            GraphicsSettingsPolicy.ClampRenderScalePercent((int)Math.Round(GameplayRenderScalePercent));
        config.Preview3DCameraFill =
            GraphicsSettingsPolicy.ClampCameraFillPercent(200 - (int)Math.Round(GameplayCameraDistancePercent));
        // Mirror the MODE as well as the number. A drag through this slider
        // already left Auto (OnGameplayUiScaleChanged → SetManual); persisting
        // only the number would bring Auto back on the next launch and silently
        // undo it.
        config.UiScaleAuto = Services.UiScaleController.IsAuto;
        if (!config.UiScaleAuto)
            config.UiScale = Services.UiScaleController.Clamp(GameplayUiScale);
        // String-colour preset is applied + persisted immediately on change;
        // mirror the id here too so a durable save always matches the visible choice.
        config.StringColorPresetId = SelectedStringColorPreset?.Id ?? StringColorPresets.DefaultId;

        _configService.SaveAppConfig(config);
    }

    // ── Keybinds watch ───────────────────────────────────────────────────────

    private void OnGameplayKeybindChanged(object? sender, PropertyChangedEventArgs e) => _keybindsTouched = true;

    private void WatchKeybinds(bool watch)
    {
        // Re-announce the TABLE, not just its rows. GameplayKeybinds is a plain
        // computed property over an object that gets REPLACED — the editor's
        // Keybinds tab assigns a fresh KeybindConfig onto ProjectState on
        // Cancel, and so does a config-backup import — so a realized overlay
        // would go on binding the discarded one. Announcing it here, at the
        // moment the section opens, is enough: the overlay is the only reader.
        if (watch) OnPropertyChanged(nameof(GameplayKeybinds));

        // Unsubscribe from the table this modal SUBSCRIBED to, which is not
        // necessarily the one ProjectState holds now (see above).
        _watchedKeybinds?.PropertyChanged -= OnGameplayKeybindChanged;
        _watchedKeybinds = null;
        if (!watch) return;

        _watchedKeybinds = _projectState.Keybinds;
        _watchedKeybinds.PropertyChanged += OnGameplayKeybindChanged;
        _keybindsTouched = false;
    }

    /// <summary>Persists + re-registers the keybinds, but ONLY when one actually
    /// changed. <c>RefreshBinds</c> re-registers every chord in the app, which is
    /// not something a modal close should do for a user who only moved a
    /// volume slider.</summary>
    private void PersistGameplayKeybinds()
    {
        if (!_keybindsTouched) return;
        _keybindsTouched = false;
        _configService?.SaveKeybinds(_projectState.Keybinds);
        _keybindRegistry?.RefreshBinds();
    }

    /// <summary>Drops the one subscription this modal makes on shared state (the
    /// live keybind table, held only while the modal is open) and the host.</summary>
    public void Dispose()
    {
        WatchKeybinds(false);
        _host = null;
    }

    // ── Audio + Gameplay values (loaded on open, persisted on close) ─────────

    /// <summary>Tick every beat during gameplay (persisted as
    /// <c>AppConfig.GameplayMetronomeEnabled</c>; armed for the run in
    /// <c>BeginGameplay</c>). Loaded on open, persisted on close.</summary>
    [ObservableProperty] private bool _gameplayMetronomeEnabled;

    /// <summary>Click on every charted NOTE during gameplay (persisted as
    /// <c>AppConfig.GameplayClapsEnabled</c>; armed for the run in
    /// <c>BeginGameplay</c>). The metronome above clicks every BEAT — this clicks
    /// what you are supposed to PLAY, which is what makes it a timing reference.</summary>
    [ObservableProperty] private bool _gameplayClapsEnabled;

    /// <summary>Count-in length row: OFF / 2 / 4 / 8 beats — the index mapping
    /// <c>AppConfig.GameplayCountInBeats</c> uses. Loaded on open, persisted on
    /// close.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCountInOff))]
    [NotifyPropertyChangedFor(nameof(IsCountIn2))]
    [NotifyPropertyChangedFor(nameof(IsCountIn4))]
    [NotifyPropertyChangedFor(nameof(IsCountIn8))]
    private int _gameplayCountInIndex;

    /// <summary>Performance stakes: the rock meter + fail-out (persisted as
    /// <c>AppConfig.GameplayRockMeterEnabled</c>; armed per run in
    /// <c>BeginGameplay</c>, where the Riff-Repeater exclusion lives). Loaded on
    /// open, persisted on close.</summary>
    [ObservableProperty] private bool _gameplayRockMeterEnabled;

    /// <summary>Resume-rewind lead-in: 0 = Off, 1 = 1 s, 2 = 1.5 s, 3 = 2 s — the
    /// index mapping <c>AppConfig.GameplayResumeRewindMs</c> uses. Loaded on open,
    /// persisted on close; read per resume by <c>ResumeGameplay</c>.</summary>
    [ObservableProperty] private int _gameplayResumeRewindIndex;

    /// <summary>Pure resume-rewind index → ms map (0 = off). Unit-tested.</summary>
    internal static int GameplayResumeRewindIndexToValue(int index) => index switch
    {
        1 => 1000,
        2 => 1500,
        3 => 2000,
        _ => 0
    };

    /// <summary>Pure resume-rewind ms → index map. Anything unknown but positive
    /// rounds to the nearest offered step, so a config-file value still shows.</summary>
    internal static int GameplayResumeRewindValueToIndex(int ms) => ms switch
    {
        <= 0 => 0,
        < 1250 => 1,
        < 1750 => 2,
        _ => 3
    };

    // ── Count-in ─────────────────────────────────────────────────────────────

    /// <summary>Count-in picker rows: OFF / 2 / 4 / 8 beats — the ONE map
    /// Settings ▸ Play also uses (<see cref="GameplayCountInRows" />). The two
    /// surfaces write the same <c>AppConfig.GameplayCountInBeats</c>, so a
    /// mapping that existed twice with different row counts silently rewrote a
    /// 2-beat count-in to OFF the next time the editor's Settings page was saved.</summary>
    internal static int GameplayCountInIndexToBeats(int index) => GameplayCountInRows.IndexToBeats(index);

    /// <summary>Pure beats → row, through the same shared map.</summary>
    internal static int GameplayCountInBeatsToIndex(int beats) => GameplayCountInRows.BeatsToIndex(beats);

    /// <summary>The segmented control's four lit states. Same string-parameter
    /// rule as the rail.</summary>
    [RelayCommand]
    private void SetGameplayCountIn(string? index)
    {
        if (int.TryParse(index, out var row)) GameplayCountInIndex = Math.Clamp(row, 0, 3);
    }

    public bool IsCountInOff => GameplayCountInIndex == 0;
    public bool IsCountIn2 => GameplayCountInIndex == 1;
    public bool IsCountIn4 => GameplayCountInIndex == 2;
    public bool IsCountIn8 => GameplayCountInIndex == 3;

    // ── Section rail (tabbed layout) ─────────────────────────────────────────
    // The rail order IS `GameplaySettingsSectionId`: Audio · Devices · Gameplay ·
    // Graphics · Appearance · Keybinds. Drives which panel is visible in the
    // modal; not persisted (transient UI state).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGameSettingsAudio))]
    [NotifyPropertyChangedFor(nameof(IsGameSettingsDevices))]
    [NotifyPropertyChangedFor(nameof(IsGameSettingsGameplay))]
    [NotifyPropertyChangedFor(nameof(IsGameSettingsGraphics))]
    [NotifyPropertyChangedFor(nameof(IsGameSettingsAppearance))]
    [NotifyPropertyChangedFor(nameof(IsGameSettingsKeybinds))]
    [NotifyPropertyChangedFor(nameof(GameplaySettingsSectionTitle))]
    [NotifyPropertyChangedFor(nameof(GameplaySettingsSectionSubtitle))]
    [NotifyPropertyChangedFor(nameof(GameplaySettingsResetCaption))]
    private int _gameplaySettingsSection;

    public bool IsGameSettingsAudio => CurrentGameplaySettingsSection == GameplaySettingsSectionId.Audio;
    public bool IsGameSettingsDevices => CurrentGameplaySettingsSection == GameplaySettingsSectionId.Devices;
    public bool IsGameSettingsGameplay => CurrentGameplaySettingsSection == GameplaySettingsSectionId.Gameplay;
    public bool IsGameSettingsGraphics => CurrentGameplaySettingsSection == GameplaySettingsSectionId.Graphics;
    public bool IsGameSettingsAppearance => CurrentGameplaySettingsSection == GameplaySettingsSectionId.Appearance;
    public bool IsGameSettingsKeybinds => CurrentGameplaySettingsSection == GameplaySettingsSectionId.Keybinds;

    internal GameplaySettingsSectionId CurrentGameplaySettingsSection =>
        (GameplaySettingsSectionId)Math.Clamp(
            GameplaySettingsSection, 0, GameplaySettingsCatalog.Sections.Count - 1);

    /// <summary>
    /// Switches the rail. The parameter is a STRING on purpose: a
    /// <c>RelayCommand&lt;int&gt;</c> fed a literal <c>CommandParameter="1"</c>
    /// throws while the view lays out (one of the four XAML faults that build
    /// perfectly clean in this repo), and an <c>{x:Static}</c> per row is six
    /// more things to keep in step with the enum.
    /// </summary>
    [RelayCommand]
    private void ShowGameplaySettingsSection(string? section)
    {
        if (!int.TryParse(section, out var index)) return;
        GameplaySettingsSection = Math.Clamp(index, 0, GameplaySettingsCatalog.Sections.Count - 1);
        GameplaySettingsQuery = string.Empty;
    }

    // ── The section header (title + subtitle follow the rail) ────────────────

    public string GameplaySettingsSectionTitle =>
        _localization[GameplaySettingsCatalog.SectionTitleKey(CurrentGameplaySettingsSection)];

    public string GameplaySettingsSectionSubtitle =>
        _localization[GameplaySettingsCatalog.SectionDescriptionKey(CurrentGameplaySettingsSection)];

    /// <summary>"Reset audio to defaults" — the footer's tertiary action names
    /// the section it will actually reset, because a bare "Reset to defaults"
    /// on a six-section modal does not say what it is about to destroy.</summary>
    public string GameplaySettingsResetCaption =>
        _localization.Format("PlayMode_GS_ResetSection", GameplaySettingsSectionTitle);

    // ── Graphics perf (fps cap + render scale + camera) ──────────────────────
    // Mirror Settings → Graphics: fps cap live-applies via the Preview3DPacing
    // static (the render loop reads it), render scale via the Vulkan service
    // (coalesced per frame). Both persist to the SAME AppConfig fields on close.

    /// <summary>FPS-cap picker rows: Unlimited / 30 / 60 / 120 / 144 / 240.</summary>
    public IReadOnlyList<string> GameplayFpsOptions { get; }

    /// <summary>Index into <see cref="GameplayFpsOptions" /> (0 = unlimited). Maps
    /// to <c>AppConfig.MaxFps</c>; live-applied via <c>Preview3DPacing</c>.</summary>
    [ObservableProperty] private int _gameplayMaxFpsIndex;

    partial void OnGameplayMaxFpsIndexChanged(int value)
    {
        if (_loadingGameplaySettings) return;
        Controls.Preview3DPacing.MaxFps =
            GraphicsSettingsPolicy.ClampMaxFps(GameplayFpsIndexToValue(value));
    }

    /// <summary>3D render scale, % of the panel size. Live-applied via the Vulkan
    /// render service; persisted to <c>AppConfig.RenderScalePercent</c>.</summary>
    [ObservableProperty] private double _gameplayRenderScalePercent = GraphicsSettingsPolicy.DefaultRenderScalePercent;

    partial void OnGameplayRenderScalePercentChanged(double value)
    {
        if (_loadingGameplaySettings) return;
        _vulkanRenderer?.SetRenderScalePercent(
            GraphicsSettingsPolicy.ClampRenderScalePercent((int)Math.Round(value)));
    }

    /// <summary>Camera distance, % (higher = board pulled away). The engine scales
    /// by "fill" (higher = closer), so this slider is the INVERSE: fill = 200 −
    /// distance. Live-applied through <c>ProjectState.Preview3DCameraFill</c> (the
    /// render driver wakes on it); persisted to <c>AppConfig.Preview3DCameraFill</c>.</summary>
    [ObservableProperty]
    private double _gameplayCameraDistancePercent =
        200 - GraphicsSettingsPolicy.DefaultCameraFillPercent;

    partial void OnGameplayCameraDistancePercentChanged(double value)
    {
        if (_loadingGameplaySettings) return;
        _projectState.Preview3DCameraFill =
            GraphicsSettingsPolicy.ClampCameraFillPercent(200 - (int)Math.Round(value));
    }

    /// <summary>Pure fps-cap index → value map (0 = unlimited). Unit-tested.</summary>
    internal static int GameplayFpsIndexToValue(int index) => index switch
    {
        1 => 30,
        2 => 60,
        3 => 120,
        4 => 144,
        5 => 240,
        _ => 0
    };

    /// <summary>Pure fps-cap value → index map (unknown = unlimited). Unit-tested.</summary>
    internal static int GameplayFpsValueToIndex(int fps) => fps switch
    {
        30 => 1,
        60 => 2,
        120 => 3,
        144 => 4,
        240 => 5,
        _ => 0
    };

    // ── Appearance ───────────────────────────────────────────────────────────

    /// <summary>Global UI scale (accessibility). Live-previewed via
    /// <c>UiScaleController</c> (the shell reflows); persisted to
    /// <c>AppConfig.UiScale</c> on close.
    /// <para>This is the SECOND door onto Settings ▸ Appearance ▸ Interface
    /// scale. It is seeded from the APPLIED factor rather than from
    /// <c>AppConfig.UiScale</c>, so under Auto the slider shows what Auto
    /// resolved to instead of a stale manual number the shell is not using.
    /// </para></summary>
    [ObservableProperty] private double _gameplayUiScale = 1.0;

    partial void OnGameplayUiScaleChanged(double value)
    {
        if (_loadingGameplaySettings) return;
        // SetManual, not Set. This partial only runs for a real slider drag —
        // the load path is fenced by _loadingGameplaySettings — and dragging the
        // scale is the clearest possible statement that the user does not want
        // Auto deciding it. Without this, Auto would silently overwrite the drag
        // on the next window resize.
        Services.UiScaleController.SetManual(value);
    }

    /// <summary>Colour-blind-safe string-colour presets (highway readability) —
    /// the same list Settings → Appearance offers.</summary>
    public IReadOnlyList<StringColorPreset> StringColorPresetOptions { get; } = StringColorPresets.All;

    /// <summary>Applied + persisted immediately on change (mirrors Settings →
    /// Appearance): re-applies the live theme with the preset's <c>String*</c>
    /// overrides layered over the persisted theme, then saves
    /// <c>AppConfig.StringColorPresetId</c>. Custom theme colours are preserved.</summary>
    [ObservableProperty] private StringColorPreset? _selectedStringColorPreset;

    partial void OnSelectedStringColorPresetChanged(StringColorPreset? value)
    {
        if (_loadingGameplaySettings || _themeService == null || value == null) return;
        var config = _configService?.LoadAppConfig();
        var overrides = new Dictionary<string, string>(
            config?.ThemeColorOverrides ?? new Dictionary<string, string>());
        foreach (var kv in StringColorPresets.GetOverrides(value.Id)) overrides[kv.Key] = kv.Value;
        _themeService.ApplyPreset(_themeService.CurrentPreset, overrides);
        if (config != null)
        {
            config.StringColorPresetId = value.Id;
            _configService?.SaveAppConfig(config);
        }
    }

    // ── Search ───────────────────────────────────────────────────────────────

    /// <summary>The modal's search box. Empty is the un-searched screen (all six
    /// sections behave normally); non-empty REPLACES the section body with the
    /// results list, because a settings modal is 1020 px wide and there is no
    /// room to show a dropdown over a pane that is still live.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearchingGameplaySettings))]
    [NotifyPropertyChangedFor(nameof(HasGameplaySettingsResults))]
    [NotifyPropertyChangedFor(nameof(HasNoGameplaySettingsResults))]
    private string _gameplaySettingsQuery = string.Empty;

    /// <summary>Hits from <see cref="GameplaySettingsCatalog.Search" /> over the
    /// REAL catalogue — the same rows the panes render, cross-linted in both
    /// directions by <c>GameplaySettingsCatalogTests</c>.</summary>
    public ObservableCollection<GameplaySettingsHit> GameplaySettingsResults { get; } = [];

    public bool IsSearchingGameplaySettings => !string.IsNullOrWhiteSpace(GameplaySettingsQuery);
    public bool HasGameplaySettingsResults => IsSearchingGameplaySettings && GameplaySettingsResults.Count > 0;
    public bool HasNoGameplaySettingsResults => IsSearchingGameplaySettings && GameplaySettingsResults.Count == 0;

    /// <summary>Typing coalesces into ONE catalogue search per dispatcher turn —
    /// the same rule and type as Song Select's <c>FilterPass</c> (refilling the bound
    /// results per keystroke would lag, while Song Select already coalesces).</summary>
    public Tone.UI.ViewModels.Shell.CoalescedFilterPass GameplaySettingsSearchPass =>
        _gameplaySettingsSearchPass ??= new Tone.UI.ViewModels.Shell.CoalescedFilterPass(ApplyGameplaySettingsQuery);

    partial void OnGameplaySettingsQueryChanged(string value)
    {
        // The "is a search active" flags flip at once so the panes hide on the
        // first keystroke; only the RESULTS wait for the coalesced pass.
        OnPropertyChanged(nameof(IsSearchingGameplaySettings));
        GameplaySettingsSearchPass.Request();
    }

    private void ApplyGameplaySettingsQuery()
    {
        GameplaySettingsResults.Clear();
        foreach (var hit in GameplaySettingsCatalog.Search(GameplaySettingsQuery, key => _localization[key]))
            GameplaySettingsResults.Add(hit);

        OnPropertyChanged(nameof(HasGameplaySettingsResults));
        OnPropertyChanged(nameof(HasNoGameplaySettingsResults));
    }

    [RelayCommand]
    private void ClearGameplaySettingsSearch() => GameplaySettingsQuery = string.Empty;

    /// <summary>Jump from a result to the section that owns it, and drop the
    /// query — the point of the result is to put the user in front of the
    /// control, not to leave them reading a list.</summary>
    [RelayCommand]
    private void GoToGameplaySetting(GameplaySettingsHit? hit)
    {
        if (hit == null) return;
        GameplaySettingsSection = hit.SectionIndex;
        GameplaySettingsQuery = string.Empty;
    }

    // ── The live input meter ─────────────────────────────────────────────────

    /// <summary>Capture peak of the guitar input, in dBFS.
    /// <see cref="double.NegativeInfinity" /> is silence. Fed by
    /// <see cref="SampleInputLevel" /> from <c>PlayModeView</c>'s own timer while
    /// the modal or the Get Ready screen is up — the SAME capture tap the menu
    /// backdrop's EQ bars read, so there is no second audio pump. Get Ready's
    /// input row binds this same value.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GameplayInputLevelText))]
    private double _gameplayInputLevelDb = double.NegativeInfinity;

    /// <summary>The mono readout beside the meter. Silence prints an em dash
    /// rather than "-∞ dB", which is machine noise, not information.</summary>
    public string GameplayInputLevelText =>
        double.IsNegativeInfinity(GameplayInputLevelDb) || GameplayInputLevelDb <= InputMeterFloorDb
            ? _localization["PlayMode_GS_NoSignal"]
            : _localization.Format("PlayMode_GS_InputLevelValue", GameplayInputLevelDb);

    /// <summary>Below this the meter reads "no signal". −60 dBFS is far under
    /// any real noise floor a DI or an interface produces, so it separates
    /// "quiet" from "nothing is arriving".</summary>
    internal const double InputMeterFloorDb = -60.0;

    /// <summary>Linear capture peak (0..1) → dBFS. Pure, so the mapping is
    /// pinned without an audio device: a zero peak is silence, not 0 dB, and
    /// getting that backwards lights every bar red on a dead input.</summary>
    internal static double PeakToDb(float peak) =>
        peak <= 0f ? double.NegativeInfinity : 20.0 * Math.Log10(peak);

    /// <summary>One read of the input tap into <see cref="GameplayInputLevelDb" />.
    /// The modal's poll and Get Ready's poll both call it.</summary>
    internal void SampleInputLevel() => GameplayInputLevelDb = PeakToDb(_host?.ReadBackdropInputPeak() ?? 0f);

    // ── The latency chip (nav footer) ────────────────────────────────────────

    /// <summary>The engine's real output latency, in ms. Zero means no engine /
    /// no device — never a fabricated "12 ms".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GameplayLatencyText))]
    [NotifyPropertyChangedFor(nameof(IsGameplayLatencyGood))]
    [NotifyPropertyChangedFor(nameof(IsGameplayLatencyHigh))]
    [NotifyPropertyChangedFor(nameof(IsGameplayLatencyUnknown))]
    private double _gameplayLatencyMs;

    /// <summary>Over this the chip turns amber: it is not a failure, it is a
    /// number that costs you something and you should know about it. Under it,
    /// green.</summary>
    internal const double LatencyWarnMs = 15.0;

    public bool IsGameplayLatencyUnknown => GameplayLatencyMs <= 0;
    public bool IsGameplayLatencyGood => GameplayLatencyMs is > 0 and <= LatencyWarnMs;
    public bool IsGameplayLatencyHigh => GameplayLatencyMs > LatencyWarnMs;

    public string GameplayLatencyText =>
        IsGameplayLatencyUnknown
            ? _localization["PlayMode_GS_NoDevice"]
            : _localization.Format("PlayMode_GS_LatencyChip", GameplayLatencyMs);

    /// <summary>What the audio engine is actually running right now — negotiated
    /// rate, negotiated buffer and the latency that follows from them. Read back
    /// from the engine, never from the config, because the two disagree exactly
    /// when the user needs to know.</summary>
    public string GameplayEngineSummary
    {
        get
        {
            if (_audio == null) return _localization["PlayMode_GS_NoDevice"];
            var rate = _audio.ActiveSampleRate;
            var frames = _audio.ActualBufferFrames;
            if (rate <= 0 || frames <= 0) return _localization["PlayMode_GS_NoDevice"];
            return _localization.Format(
                "PlayMode_GS_EngineSummary", rate / 1000.0, frames, _audio.MeasuredOutputLatencyMs);
        }
    }

    /// <summary>Counts how many times the view has driven the meters. The poll
    /// is the one thing here with a clock in it, so this is the seam that lets a
    /// test assert it runs while the modal is open and NOT while it is
    /// closed.</summary>
    internal int GameplaySettingsMeterPolls { get; private set; }

    /// <summary>
    /// One tick of the modal's meters, driven by <c>PlayModeView</c>'s own
    /// timer. It is a no-op while the modal is closed, which is what makes the
    /// timer's own gating a belt-and-braces rather than the only guard.
    /// </summary>
    public void PollGameplaySettingsMeters()
    {
        if (!IsGameplaySettingsOpen) return;
        GameplaySettingsMeterPolls++;

        SampleInputLevel();
        GameplayLatencyMs = _audio?.MeasuredOutputLatencyMs ?? 0;
        OnPropertyChanged(nameof(GameplayEngineSummary));
    }

    // ── Audio devices ────────────────────────────────────────────────────────

    /// <summary>The app's ONE audio-device view model (a DI singleton, shared
    /// with the editor's Settings → Audio tab). The Devices section binds it
    /// directly rather than mirroring its state: the device lists are the real
    /// enumerated devices, and the apply is the real
    /// <c>ApplyAudioConfiguration</c> path with its routing-changed guard.</summary>
    public AudioSettingsViewModel? AudioDevices { get; }

    public bool HasAudioDevices => AudioDevices != null;

    /// <summary>
    /// Commits the Devices section. This is the ONE setting in the modal that is
    /// not applied on change, and the row says so: a driver/device/rate/buffer
    /// change tears the engine down and rebuilds it, so applying per keystroke
    /// would restart the device once per combo-box row the user passes through.
    /// <c>AudioSettingsViewModel.Save</c> already refuses to touch the engine
    /// when nothing routing-related actually changed.
    /// </summary>
    [RelayCommand]
    private void ApplyAudioDevices()
    {
        AudioDevices?.SaveCommand.Execute(null);
        GameplayLatencyMs = _audio?.MeasuredOutputLatencyMs ?? 0;
        OnPropertyChanged(nameof(GameplayEngineSummary));
    }

    // ── Keybinds ─────────────────────────────────────────────────────────────

    /// <summary>The live keybind table (<c>ProjectState.Keybinds</c>) — the same
    /// instance the editor's Keybinds tab edits and the registrar reads.</summary>
    public KeybindConfig GameplayKeybinds => _projectState.Keybinds;

    // ── Reset ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Puts the CURRENT section back to the shipped defaults. Scoped to the
    /// section on purpose — a modal-wide reset would take the user's audio
    /// devices with it when all they wanted was their highway back.
    /// </summary>
    [RelayCommand]
    private void ResetGameplaySettingsSection()
    {
        var defaults = new AppConfig();
        switch (CurrentGameplaySettingsSection)
        {
            case GameplaySettingsSectionId.Audio:
                if (_transport != null)
                {
                    _transport.Volume = 1.0;
                    _transport.SongVolume = 1.0;
                    _transport.InputVolume = 1.0;
                    _transport.MonitorVolume = 1.0;
                }

                GameplayMetronomeEnabled = defaults.GameplayMetronomeEnabled;
                GameplayClapsEnabled = defaults.GameplayClapsEnabled;
                GameplayCountInIndex = GameplayCountInBeatsToIndex(defaults.GameplayCountInBeats);
                break;

            case GameplaySettingsSectionId.Devices:
                // The device section's own reset is "stop overriding the driver"
                // — the one default that is a real decision rather than a value.
                AudioDevices?.AutoFollowDeviceSettings = true;
                break;

            case GameplaySettingsSectionId.Gameplay:
                GameplayRockMeterEnabled = defaults.GameplayRockMeterEnabled;
                GameplayResumeRewindIndex = GameplayResumeRewindValueToIndex(defaults.GameplayResumeRewindMs);
                // The two tone rows are Play Mode's; each setter persists itself.
                if (_host != null)
                {
                    _host.UseInGameTones = defaults.UseInGameTones;
                    _host.ShowGameplayToneHud = defaults.ShowGameplayToneHud;
                }

                break;

            case GameplaySettingsSectionId.Graphics:
                GameplayMaxFpsIndex = GameplayFpsValueToIndex(defaults.MaxFps);
                GameplayRenderScalePercent =
                    GraphicsSettingsPolicy.ClampRenderScalePercent(defaults.RenderScalePercent);
                GameplayCameraDistancePercent =
                    200 - GraphicsSettingsPolicy.ClampCameraFillPercent(defaults.Preview3DCameraFill);
                _projectState.EnableNoteHitExplosions = defaults.EnableNoteHitExplosions;
                _projectState.EnableEnhancedHitFeedback = defaults.EnableEnhancedHitFeedback;
                _projectState.EnableHitSparks = defaults.EnableHitSparks;
                _projectState.EnableBeatPulse = defaults.EnableBeatPulse;
                _projectState.EnableBloom = defaults.EnableBloom;
                _projectState.EnableVignette = defaults.EnableVignette;
                _projectState.EnableFilmicTonemap = defaults.EnableFilmicTonemap;
                _projectState.EnableDepthFog = defaults.EnableDepthFog;
                _projectState.EnableHighwayLighting = defaults.EnableHighwayLighting;
                _projectState.EnableStringShimmer = defaults.EnableStringShimmer;
                break;

            case GameplaySettingsSectionId.Appearance:
                _projectState.EnableHighwayNearFade = defaults.EnableHighwayNearFade;
                _projectState.EnableAnchorPreview = defaults.EnableAnchorPreview;
                _projectState.EnableAnchorSweep = defaults.EnableAnchorSweep;
                _projectState.EnableShowlightBeams = defaults.EnableShowlightBeams;
                _projectState.EnableShowlightFog = defaults.EnableShowlightFog;
                _projectState.EnableCrowdBackground = defaults.EnableCrowdBackground;
                SelectedStringColorPreset = StringColorPresets.GetById(StringColorPresets.DefaultId);
                // Reset-to-defaults means the DEFAULT MODE too, and that is Auto.
                // Resetting to a bare 1.0 would put a 1440p user back on exactly
                // the small UI this setting exists to fix. SetAutoMode makes the
                // shell re-derive; the seed below then shows what it chose.
                Services.UiScaleController.SetAutoMode(defaults.UiScaleAuto);
                if (!defaults.UiScaleAuto)
                    Services.UiScaleController.SetManual(defaults.UiScale);
                GameplayUiScale = Services.UiScaleController.Scale;
                break;

            case GameplaySettingsSectionId.Keybinds:
                _projectState.Keybinds.ResetToDefault();
                _keybindsTouched = true;
                break;
        }
    }
}
