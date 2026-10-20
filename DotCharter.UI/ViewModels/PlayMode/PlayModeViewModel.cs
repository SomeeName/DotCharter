// Root partial of PlayModeViewModel: every injected dependency, every plain private field, the constructor, and the two AppConfig-backed shell toggles.

using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Gameplay.Leaderboards;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Services.Gameplay.Stats;
using DotCharter.Services.Library;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Panels;
using DotCharter.Services.Audio.Coordination;
using DotCharter.Services.Session;

namespace DotCharter.UI.ViewModels.PlayMode;

/// <summary>
///     Navigation + gameplay-flow brain for the Play Mode shell surface:
///     Start → Menu → Song select → (Tuning check) → Countdown → Gameplay
///     (in the editor surface, driven by Live Play) → Results → Retry /
///     Song select / editor bridge. The editor remains untouched; gameplay
///     reuses its transport, Live Play session and 3D highway.
///     <para>
///         <b>This is the ROOT of the type's partials.</b> It keeps the
///         constructor and <b>every injected dependency and plain private
///         field</b>, plus the two AppConfig-backed shell toggles. Everything
///         else lives in a responsibility-named sibling
///         <c>PlayModeViewModel.*.cs</c> in this directory, each carrying a
///         one-line header saying what it holds — read that instead of opening
///         them all.
///     </para>
///     <para>
///         <b>The field rule, stated once here and referenced by the
///         partials:</b> every injected dependency and every plain private field —
///         mutable state, caches, timers, lazily-resolved sub-view-models —
///         lives in this ROOT file, in one block, so "where does this come from"
///         stays a look rather than a search. Backing fields carrying
///         <c>[ObservableProperty]</c> travel WITH their responsibility instead:
///         the generator turns each into a public property, so it declares that
///         screen's bound surface, not shared state. <c>const</c>s stay with the
///         algorithm that reads them. A screen whose state stands on its own
///         leaves as a collaborator instead (<see cref="SongSelect" />,
///         <see cref="ToneRig" />, <see cref="Settings" />, <see cref="RunResults" />,
///         <see cref="ListenBack" />).
///     </para>
///     <para>
///         <b>Do not name a fixed sibling list here.</b> The 0.41 split said
///         "five partials" and named them; the 0.44 token-efficiency pass took
///         the root from 155 KB to 21 KB and the count well past five, leaving
///         this comment confidently wrong. <c>ls PlayModeViewModel*.cs</c> is
///         the only answer that cannot go stale.
///     </para>
/// </summary>
public sealed partial class PlayModeViewModel : ObservableObject, IPlayModeSettingsHost, IPlaySetlistHost
{
    private bool _runAppliedPitchShift;

    // Tuning-match transpose (.GetReady, .Gameplay, .Results): the song's pitch shift
    // before the run chain applied one, whether it did, and the shift the guided check is aimed at.
    private double _preRunSongPitchShift;
    private bool _runAppliedSongTranspose;
    private int _guidedTuningTranspose;

    private const int SongEndGraceMs = 150;

    private readonly IPlayerProfileService _profiles;
    private readonly IGameplayStatsService _stats;

    private readonly ProjectState _projectState;
    private readonly ILocalizationService _localization;

    private PlayModeScreen _announcedFrom = PlayModeScreen.Start;

    /// <summary>What a screen reader says about the run: the song on start,
    /// paused, resumed, the count-in, failed, finished. Empty on every other
    /// screen. The highway is a Vulkan surface UIA cannot see, so this text,
    /// shown in a polite live region, is the only announcement a run makes.</summary>
    [ObservableProperty] private string _screenAnnouncement = "";
    private readonly IConfigurationService _configService;
    private readonly SongBrowserViewModel? _songBrowser;
    private readonly LivePlayViewModel? _livePlay;
    private readonly TransportViewModel? _transport;
    private readonly IProjectSessionService _projectSession;
    private readonly StemMixerViewModel? _stemMixer;
    private readonly IAppLogger _logger;
    private readonly DispatcherTimer _countdownTimer;
    private readonly DispatcherTimer _endWatchTimer;

    private readonly SemaphoreSlim _homeSongGate = new(1, 1);

    // Menu input-signal probe (PlayModeViewModel.Screens.cs).
    private DispatcherTimer? _signalTimer;
    private int _signalSilentTicks;

    /// <summary>Pre-play guided tuning check (0.21), shown on the Tuning-check
    /// screen and driven off the input tap while that screen is up.</summary>
    public GuidedTuningCheckViewModel GuidedTuning { get; }

    /// <summary>Get Ready's "Play in my tuning" row: the player's own tuning, the
    /// per-profile opt-in, and the semitones a run shifts the song by.</summary>
    public TuningMatchViewModel TuningMatch { get; }

    /// <summary>Get Ready's per-song drop tuner: the guitar stays in standard and the
    /// run shifts the input down to the song. Wins over <see cref="TuningMatch" />.</summary>
    public DropTunerViewModel DropTuner { get; }

    /// <summary>Dynamic backing mix (0.21): ducks the player's stem on a miss and
    /// swells it back on a streak. Run-scoped engine gain only — started on entering
    /// the Playing screen, restored on every way off it (never persists).</summary>
    private readonly StemDuckFollower _stemDuck;
    private readonly RsTonePlaybackCoordinator? _rsToneCoordinator;

    /// <summary>Editing one of the song's tones on the rig and saving it into the
    /// package. Null without a tone coordinator (headless hosts).</summary>
    public SongToneEditViewModel? SongToneEdit { get; }

    /// <summary>The Amp &amp; FX rig overlay: its open flag, the shared chain, the
    /// gated rig screen and the AI tone panel's host half. Injected (one per
    /// process, like this view model); built without DI as a rig whose factories
    /// yield nothing.</summary>
    public PlayModeToneRigViewModel ToneRig { get; }

    /// <summary>The settings modal (§6.5): its open flag, the AppConfig-backed
    /// values, the search, the meters and the per-section reset. Injected (one per
    /// process, like this view model); built without DI over the collaborators
    /// this view model was given. Reads the run's state back through
    /// <see cref="IPlayModeSettingsHost" />.</summary>
    public PlayModeSettingsViewModel Settings { get; }

    /// <summary>Setlists: Song Select's picker, Results' Next, Pause's Skip/End.</summary>
    public PlaySetlistViewModel Setlists { get; }

    private readonly DotCharter.Tone.Audio.Dsp.Presets.IDspTonePresetService _tonePresets;

    /// <summary>Song Select's list: the library scan, facets, filters, search,
    /// sort and chips. Its view binds <c>SongSelect.…</c>; the chosen song stays
    /// here as <see cref="SelectedSong" />.</summary>
    public SongSelectViewModel SongSelect { get; }

    /// <summary>The Results screen's board for the run just finished: score,
    /// bests, sections, timing and leaderboard. Its view binds <c>RunResults.…</c>.</summary>
    public RunResultsViewModel RunResults { get; }

    /// <summary>The Results screen's listen-back to the run just played. Its view
    /// binds <c>ListenBack.…</c>.</summary>
    public RunListenBackViewModel ListenBack { get; }

    private readonly IAudioPlaybackService _audio;

    /// <summary>Home's own-chart check: does the remembered project file still exist.</summary>
    private readonly IExportTargetWriter _files;

    /// <summary>Opens the latency wizard over Song Select for a new guest
    /// (PlayModeViewModel.Flow.cs). A factory: the dialog launcher is not built
    /// on the startup path for a door most sessions never use.</summary>
    private readonly Func<ILatencyCalibrationPrompt?>? _latencyPrompt;

    // Results screen (PlayModeViewModel.Results.cs).
    private LivePlaySessionReport? _lastReport;

    // Shell collaborators (browser, live play, transport, sessions) are
    // optional so navigation/flow logic stays unit-testable without
    // standing up the whole editor; DI supplies all of them in the app.
    public PlayModeViewModel(
        IPlayerProfileService profiles,
        IGameplayStatsService stats,
        ILeaderboardService leaderboards,
        ProjectState projectState,
        ILocalizationService localization,
        IConfigurationService configService,
        IProjectSessionService projectSession,
        ILibraryService library,
        IAppLogger logger,
        DotCharter.Tone.Audio.Dsp.Presets.IDspTonePresetService tonePresets,
        IAudioPlaybackService audio,
        DotCharter.Services.Audio.LivePlay.Takes.ILiveTakeStore takeStore,
        IChartGenreIndex chartGenres,
        IExportTargetWriter files,
        SongBrowserViewModel? songBrowser = null,
        LivePlayViewModel? livePlay = null,
        TransportViewModel? transport = null,
        StemMixerViewModel? stemMixer = null,
        RsTonePlaybackCoordinator? rsToneCoordinator = null,
        PlayModeToneRigViewModel? toneRig = null,
        PlayModeSettingsViewModel? settings = null,
        PlaySetlistViewModel? setlists = null,
        Func<ILatencyCalibrationPrompt?>? latencyPrompt = null)
    {
        _localization = localization;
        _files = files;
        _audio = audio;
        GuidedTuning = new GuidedTuningCheckViewModel(audio, localization);
        _stemDuck = new StemDuckFollower(audio);
        _profiles = profiles;
        TuningMatch = new TuningMatchViewModel(profiles, localization);
        TuningMatch.Changed += OnTuningMatchChanged;
        DropTuner = new DropTunerViewModel(configService, localization);
        DropTuner.Changed += OnDropTunerChanged;
        _stats = stats;
        RunResults = new RunResultsViewModel(localization, leaderboards);
        _projectState = projectState;
        _configService = configService;
        _songBrowser = songBrowser;
        SongSelect = new SongSelectViewModel(localization, library, stats, configService, files, chartGenres, songBrowser);
        SongSelect.LoadingChanged += () =>
        {
            if (IsStartScreen) RefreshLaunchScreen();
        };
        _livePlay = livePlay;
        _transport = transport;
        _projectSession = projectSession;
        _stemMixer = stemMixer;
        _logger = logger;
        ListenBack = new RunListenBackViewModel(audio, takeStore, files, stemMixer, localization, logger);
        _rsToneCoordinator = rsToneCoordinator;
        _tonePresets = tonePresets;
        ToneRig = toneRig ?? PlayModeToneRigViewModel.Detached(projectState, transport, livePlay, audio);
        _latencyPrompt = latencyPrompt;
        ToneRig.SongContextSource = () => (FlowSongTitle, FlowSongArtist);
        ToneRig.OpenSettingsRequested += () => OpenSettingsRequested?.Invoke();
        ToneRig.Closed += () => SongToneEdit?.End();
        Settings = settings ?? new PlayModeSettingsViewModel(projectState, localization, configService, transport, audio);
        Settings.AttachHost(this);
        Settings.OpenFullSettingsRequested += () => OpenSettingsRequested?.Invoke();
        Setlists = setlists ?? new PlaySetlistViewModel(localization);
        Setlists.AttachHost(this);

        _profiles.ActiveProfileChanged += _ =>
        {
            OnPropertyChanged(nameof(ActiveProfileName));
            TuningMatch.ReloadProfile();
            SongSelect.RefreshSongStats();
        };
        if (_livePlay != null)
        {
            _livePlay.SessionCompleted += OnSessionCompleted;
            // Take finalization (WAV close + Opus encode) runs off-thread and
            // lands a second or two AFTER the results screen appears — that is
            // when listen-back becomes offerable, not before.
            _livePlay.RunArchive.TakeSaved += takeFile =>
                ListenBack.OnTakeSaved(takeFile, CurrentScreen == PlayModeScreen.Results);
            // Rock-meter fail-out (0.24 performance stakes): posted from the
            // session tick after the meter latches.
            _livePlay.Meters.RockMeterFailed += OnRockMeterFailed;
        }

        _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdownTimer.Tick += (_, _) => CountdownTick();
        // 125 ms: the tick is a few property reads (it once also fed the removed
        // LAN match stream at 8 Hz).
        _endWatchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(125) };
        _endWatchTimer.Tick += (_, _) => EndWatchTick();


        if (_rsToneCoordinator is { } toneCoordinator)
        {
            SongToneEdit = new SongToneEditViewModel(
                toneCoordinator,
                () => ToneRig.Pedalboard?.BuildCurrentRig(),
                // Library songs only: an own chart edits its tones in the Tone Designer.
                () => string.IsNullOrWhiteSpace(_flowSong?.Inner?.Song.PackagePath) ? null : _flowSong?.SongIdentity,
                RefreshInGameTonesAsync,
                () => ToneRig.IsPedalboardOpen = false,
                localization);
        }

        // Field writes, not properties: loading config must not save it back.
        var appConfig = _configService.LoadAppConfig();
        _useInGameTones = appConfig.UseInGameTones;
        _showGameplayToneHud = appConfig.ShowGameplayToneHud;
    }

    /// <summary>The settings modal's one read of the run chain's flag (.Flow).</summary>
    bool IPlayModeSettingsHost.RunSideEffectsApplied => _runSideEffectsApplied;
}
