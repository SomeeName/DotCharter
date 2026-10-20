using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Services.Library;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using DotCharter.Tone.UI.ViewModels.Shell;
using DotCharter.UI.ViewModels.Dialogs;
using DotCharter.UI.ViewModels.Shell;
using DotCharter.Services.Infrastructure.Shell;
using DotCharter.Presentation.Contracts.Settings;
using DotCharter.Presentation.Contracts.Onboarding;
using DotCharter.Presentation.Screens.Onboarding;
using DotCharter.Presentation.Screens.Settings;
using DotCharter.Services.Plugins;

namespace DotCharter.UI.ViewModels.Onboarding;

/// <summary>One pickable starter tone on the wizard's Tone page.</summary>
public sealed record OnboardingToneChoice(DspTonePreset Preset, string Name, string Category);

/// <summary>
///     The first-run onboarding wizard. Eight pages, in
///     <see cref="OnboardingStepMachine.Order"/>: role → library → audio →
///     input → tone → latency → profile → done. Every page is skippable, the
///     whole flow reopens from Settings ▸ General ▸ "Run setup again", and it
///     opens on its own exactly until <c>AppConfig.OnboardingCompleted</c> is
///     set — which Finish AND dismissal both do, so a user who closes it once is
///     never nagged.
///     <para>
///         It owns NO new audio, library, tone, calibration or profile code: the
///         audio pages drive the shared <see cref="AudioSettingsViewModel"/> (the
///         same Open / Save the Settings tab uses, so the engine is reconfigured
///         through the one writer of the live seat layout), Latency opens the
///         hosted calibration wizard on top (<see cref="DotCharter.Tone.Audio.Abstractions.ILatencyCalibrationPrompt"/>), the
///         tone page applies through the pedalboard's <see cref="PresetBrowserViewModel.ApplyPreset"/>
///         (or <see cref="IDspTonePresetService.ApplyToPlayer"/> when no board exists)
///         to seat 0, and the profile page is <see cref="IPlayerProfileService"/>.
///     </para>
///     <para>
///         Navigation is the pure <see cref="OnboardingStepMachine"/>; this class
///         is the side-effect layer around it. Nothing here runs a timer — the
///         input meter is polled by the VIEW (<c>OnboardingWizardView</c>) through
///         <see cref="PollInputMeter"/>, which keeps this whole type constructible
///         in the default gate.
///     </para>
/// </summary>
public partial class OnboardingWizardViewModel : ViewModelBase, IDialogViewModel, IFirstRunSetupPrompt
{
    private const double MeterFloorDb = -60.0;

    private readonly IConfigurationService _configService;
    private readonly IPlayerProfileService _profiles;
    private readonly IDspTonePresetService _presets;
    private readonly ILibraryService _library;
    private readonly IFileStorageService _storage;
    private readonly ILocalizationService _localization;
    private readonly IAudioPlaybackService _engine;
    private readonly IAppLogger _logger;
    private readonly SystemCheckViewModel _systemCheck;
    private readonly Func<string> _appVersion;
    private readonly Func<PedalboardViewModel?>? _pedalboard;
    private readonly OnboardingStepMachine _machine;
    private readonly float[] _meterBuffer = new float[2048];

    private CancellationTokenSource? _scanCts;
    private bool _audioPagesOpened;
    private bool _syncingProfile;

    public OnboardingWizardViewModel(
        IConfigurationService configService,
        AudioSettingsViewModel audio,
        DotCharter.Tone.Audio.Abstractions.ILatencyCalibrationPrompt latencyWizard,
        IPlayerProfileService profiles,
        IDspTonePresetService presets,
        ILibraryService library,
        IFileStorageService storage,
        ILocalizationService localization,
        IAudioPlaybackService engine,
        IAppLogger logger,
        SystemCheckViewModel systemCheck,
        IPackageFeatures packageFeatures,
        Func<string>? appVersion = null,
        Func<PedalboardViewModel?>? pedalboard = null)
    {
        _pedalboard = pedalboard;
        _machine = new OnboardingStepMachine(includeLibrary: packageFeatures.ReadsGamePackages);
        _configService = configService;
        Audio = audio;
        _audio = audio;
        LatencyWizard = latencyWizard;
        _profiles = profiles;
        _presets = presets;
        _library = library;
        _storage = storage;
        _localization = localization;
        _engine = engine;
        _logger = logger;
        _systemCheck = systemCheck;
        _appVersion = appVersion ?? CurrentAppVersion;

        LatencyWizard.CalibrationApplied += (_, _) => RefreshLatencySummary();
        PrimaryCommand = new RelayCommand(() =>
        {
            if (_machine.IsLast) Finish();
            else Next();
        });
    }

    /// <summary>The shared audio settings page — device lists, selections and
    /// the Save that reconfigures the engine. The Audio and Input pages bind
    /// straight to it.</summary>
    public IAudioSettingsScreen Audio { get; }

    /// <summary>The same instance, for the save the contract does not expose to views.</summary>
    private readonly AudioSettingsViewModel _audio;

    /// <summary>The first page's check of this computer: can the native audio
    /// engine load, can the GPU run the renderer. Started by <see cref="Open"/>,
    /// off the UI thread; a capability that cannot run shows disabled with its
    /// reason instead of failing later wherever it is first used.</summary>
    public ISystemCheckScreen SystemCheck => _systemCheck;

    /// <summary>The calibration wizard, a hosted dialog drawn ON TOP of this legacy one.</summary>
    public DotCharter.Tone.Audio.Abstractions.ILatencyCalibrationPrompt LatencyWizard { get; }

    // ---- dialog state --------------------------------------------------------

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private OnboardingStep _step;

    public bool IsWelcome => Step == OnboardingStep.Welcome;
    public bool IsLibrary => Step == OnboardingStep.Library;
    public bool IsAudio => Step == OnboardingStep.Audio;
    public bool IsInput => Step == OnboardingStep.Input;
    public bool IsTone => Step == OnboardingStep.Tone;
    public bool IsLatency => Step == OnboardingStep.Latency;
    public bool IsProfile => Step == OnboardingStep.Profile;
    public bool IsDone => Step == OnboardingStep.Done;
    public bool CanGoBack => !_machine.IsFirst;

    /// <summary>Whether this run has the Library page (absent without the package reader).</summary>
    public bool HasLibraryStep => _machine.Steps.Contains(OnboardingStep.Library);
    public bool CanSkipStep => !_machine.IsFirst && !_machine.IsLast;

    /// <summary>"Step 3 of 8" for the head's context line.</summary>
    public string StepCaption => LocF("Onboarding_StepOf", _machine.Index + 1, _machine.Count);

    /// <summary>The footer's ONE primary: Next everywhere, Finish on the last page.
    /// (One button whose face changes, so the DataContext-less grammar layout —
    /// where every IsVisible binding reads true — still shows a single primary.)</summary>
    public string PrimaryCaption => _machine.IsLast ? Loc("Onboarding_Finish") : Loc("LatencyWizard_Next");

    /// <summary>What the primary does — <see cref="Next"/> or <see cref="Finish"/>
    /// by page. A hand-built command rather than two generated ones, because
    /// <c>NextCommand</c>/<c>FinishCommand</c> would each be bound by nothing.</summary>
    public IRelayCommand PrimaryCommand { get; }

    /// <summary>Which pages the user skipped this run (read by the summary).</summary>
    public IReadOnlySet<OnboardingStep> SkippedSteps => _machine.Skipped;

    partial void OnStepChanged(OnboardingStep value)
    {
        OnPropertyChanged(nameof(IsWelcome));
        OnPropertyChanged(nameof(IsLibrary));
        OnPropertyChanged(nameof(IsAudio));
        OnPropertyChanged(nameof(IsInput));
        OnPropertyChanged(nameof(IsTone));
        OnPropertyChanged(nameof(IsLatency));
        OnPropertyChanged(nameof(IsProfile));
        OnPropertyChanged(nameof(IsDone));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanSkipStep));
        OnPropertyChanged(nameof(StepCaption));
        OnPropertyChanged(nameof(PrimaryCaption));
    }

    // ---- welcome: role, language, updates -----------------------------------

    /// <summary>"I'm a Charter": boot into the editor. The other radio is
    /// <see cref="IsPlayer"/>; exactly one is true.</summary>
    [ObservableProperty] private bool _isCharter;

    public bool IsPlayer
    {
        get => !IsCharter;
        set => IsCharter = !value;
    }

    partial void OnIsCharterChanged(bool value) => OnPropertyChanged(nameof(IsPlayer));

    public IReadOnlyList<LanguageOption> LanguageOptions => _localization.LanguageOptions;

    [ObservableProperty] private int _selectedLanguageIndex;

    /// <summary>What <see cref="Open" /> seeded <see cref="SelectedLanguageIndex" />
    /// with. A commit writes the culture only when the index has MOVED off it —
    /// see <see cref="CommitWelcome" />.</summary>
    private int _seededLanguageIndex;

    [ObservableProperty] private bool _updateCheckOnStartup = true;

    // ---- library ---------------------------------------------------------------

    [ObservableProperty] private string _libraryPath = string.Empty;
    [ObservableProperty] private bool _isScanningLibrary;
    [ObservableProperty] private string _libraryStatus = string.Empty;

    /// <summary>Songs the last scan found; -1 until a scan has run.</summary>
    [ObservableProperty] private int _librarySongCount = -1;

    public bool HasLibraryPath => !string.IsNullOrWhiteSpace(LibraryPath);

    partial void OnLibraryPathChanged(string value) => OnPropertyChanged(nameof(HasLibraryPath));

    // ---- audio -----------------------------------------------------------------

    [ObservableProperty] private string _audioTestStatus = string.Empty;
    [ObservableProperty] private bool _audioTestFailed;

    public bool HasAudioTestStatus => AudioTestStatus.Length > 0;

    partial void OnAudioTestStatusChanged(string value) => OnPropertyChanged(nameof(HasAudioTestStatus));

    // ---- input -----------------------------------------------------------------

    /// <summary>Latest input peak in dBFS (−∞ for silence), fed by <see cref="PollInputMeter"/>.</summary>
    [ObservableProperty] private double _inputLevelDb = double.NegativeInfinity;

    /// <summary>0..1 for the meter bar: −60 dBFS → 0, 0 dBFS → 1.</summary>
    public double InputLevelFraction =>
        double.IsNegativeInfinity(InputLevelDb) ? 0.0 : Math.Clamp((InputLevelDb - MeterFloorDb) / -MeterFloorDb, 0.0, 1.0);

    public bool HasInputSignal => InputLevelDb > MeterFloorDb;

    /// <summary>The meter's readout ("−18.2 dBFS", or the silence caption).</summary>
    public string InputLevelText => HasInputSignal
        ? LocF("Onboarding_Input_LevelFmt", Math.Round(InputLevelDb, 1))
        : Loc("Onboarding_Input_Silence");

    [ObservableProperty] private string _inputStatus = string.Empty;

    public bool HasInputStatus => InputStatus.Length > 0;

    partial void OnInputLevelDbChanged(double value)
    {
        OnPropertyChanged(nameof(InputLevelFraction));
        OnPropertyChanged(nameof(HasInputSignal));
        OnPropertyChanged(nameof(InputLevelText));
    }

    partial void OnInputStatusChanged(string value) => OnPropertyChanged(nameof(HasInputStatus));

    // ---- tone ------------------------------------------------------------------

    public ObservableCollection<OnboardingToneChoice> Tones { get; } = [];

    [ObservableProperty] private OnboardingToneChoice? _selectedTone;
    [ObservableProperty] private string _toneStatus = string.Empty;

    public bool HasToneStatus => ToneStatus.Length > 0;

    partial void OnToneStatusChanged(string value) => OnPropertyChanged(nameof(HasToneStatus));

    partial void OnSelectedToneChanged(OnboardingToneChoice? value)
    {
        if (value is null) return;
        // The pick goes through the rig's own preset browser when the pedalboard
        // exists — the SAME path a card click takes — so an open Amp & FX board
        // re-syncs its cards and marks the tone active instead of showing the
        // chain it had before the wizard (the 0.49 bug: the tone was applied,
        // the board on screen did not move). The wizard runs with one seat, so
        // the browser's selected seat is 0 — the solo player's chain. Without a
        // pedalboard (a headless build, the view-model tests) the service is
        // applied directly; a board built LATER reads the chain in its ctor.
        var board = _pedalboard?.Invoke();
        if (board is not null) board.Browser.ApplyPreset(value.Preset);
        else _presets.ApplyToPlayer(0, value.Preset);
        ToneStatus = LocF("Onboarding_Tone_Applied", value.Name);
        _logger.Info(AppLogCategory.Audio, $"Onboarding: applied starter tone '{value.Preset.Id}' to seat 0.");
    }

    // ---- latency ---------------------------------------------------------------

    [ObservableProperty] private string _latencySummary = string.Empty;

    // ---- profile ---------------------------------------------------------------

    public ObservableCollection<PlayerProfile> Profiles { get; } = [];

    [ObservableProperty] private PlayerProfile? _selectedProfile;
    [ObservableProperty] private string _newProfileName = string.Empty;
    [ObservableProperty] private string _profileStatus = string.Empty;

    public bool HasProfileStatus => ProfileStatus.Length > 0;

    partial void OnProfileStatusChanged(string value) => OnPropertyChanged(nameof(HasProfileStatus));

    /// <summary>The selected profile's name as the player is editing it —
    /// the first run's "Player 1" is meant to be replaced here, not kept
    /// beside a second profile (the 0.49 wizard offered only Create, so the
    /// only way to be called by name was to end up with two players).</summary>
    [ObservableProperty] private string _profileName = string.Empty;

    partial void OnSelectedProfileChanged(PlayerProfile? value)
    {
        ProfileName = value?.Name ?? string.Empty;
        if (_syncingProfile || value is null) return;
        if (_profiles.SwitchTo(value.Id))
            SetProfileResult(LocF("Onboarding_Profile_Active", value.Name));
    }

    // ---- done ------------------------------------------------------------------

    [ObservableProperty] private string _summary = string.Empty;

    // ---- open / close ----------------------------------------------------------

    /// <summary>Opens on the first page, with everything reset. Also what
    /// "Run setup again" calls — a re-run never inherits a previous run's skips.
    /// A plain method, not a <c>[RelayCommand]</c>: its two callers (the shell's
    /// startup hook, Settings) are C#, and a generated command nothing binds is
    /// exactly what the command-reachability lint fails on.</summary>
    public void Open()
    {
        var config = _configService.LoadAppConfig();
        _machine.Reset();
        _audioPagesOpened = false;
        CancelScan();

        IsCharter = !StartupRouting.BootsIntoPlayMode(config);
        // Remembered so CommitWelcome can tell "the user picked System" from
        // "the config named a language this build's picker does not list, and
        // LanguageIndexOf fell back to System".
        SelectedLanguageIndex = _seededLanguageIndex = LanguageIndexOf(config.LanguageCulture);
        UpdateCheckOnStartup = config.UpdateCheckOnStartup;
        LibraryPath = config.PsarcLibraryPath ?? string.Empty;
        LibrarySongCount = -1;
        LibraryStatus = string.Empty;
        AudioTestStatus = string.Empty;
        AudioTestFailed = false;
        InputStatus = string.Empty;
        InputLevelDb = double.NegativeInfinity;
        ToneStatus = string.Empty;
        SelectedTone = null;
        SetProfileResult(string.Empty);
        NewProfileName = string.Empty;
        Summary = string.Empty;

        Step = _machine.Current;
        IsOpen = true;
        _ = _systemCheck.Start();
        _logger.Info(AppLogCategory.Application, "Onboarding wizard opened.");
    }

    /// <summary>
    ///     The startup hook: opens only while <c>AppConfig.OnboardingCompleted</c>
    ///     is false. The shell posts this at background priority after its own
    ///     construction, the same way crash recovery is checked — so the first
    ///     frame paints before the wizard's view is built.
    /// </summary>
    public bool OpenAtStartupIfNeeded()
    {
        var config = _configService.LoadAppConfig();
        if (config.OnboardingCompleted) return false;
        if (IsExistingInstall(config))
        {
            // The flag did not exist before the wizard shipped, so every upgrade
            // would otherwise greet a configured install as a stranger. A config
            // that already names a library or has calibrated a device was set up
            // by hand; record that and stay out of the way. Settings ▸ General
            // still offers "Run setup again".
            SaveConfig(c =>
            {
                c.OnboardingCompleted = true;
                c.OnboardingCompletedVersion = _appVersion();
            });
            _logger.Info(AppLogCategory.Application,
                "Onboarding skipped: the config was already set up before the wizard existed.");
            return false;
        }
        Open();
        return true;
    }

    /// <summary>Library, calibration, project and picker history identify an
    /// existing install. Window size and dock layout do not: the shell writes
    /// them when an unfinished first-run window closes.</summary>
    internal static bool IsExistingInstall(AppConfig config) =>
        !string.IsNullOrWhiteSpace(config.PsarcLibraryPath) ||
        config.LivePlayLatencyOffsetsByDevice.Count > 0 ||
        config.RecentProjects.Count > 0 ||
        !string.IsNullOrWhiteSpace(config.LastProjectPath) ||
        HasRememberedPickerFolder(config);

    /// <summary>Any of the ten "last folder" memories a picker writes. One is
    /// enough: a fresh config has none, and the user cannot have opened,
    /// imported or exported anything without leaving one.</summary>
    private static bool HasRememberedPickerFolder(AppConfig config) =>
        !string.IsNullOrWhiteSpace(config.LastRscpDir) ||
        !string.IsNullOrWhiteSpace(config.LastAudioDir) ||
        !string.IsNullOrWhiteSpace(config.LastEofDir) ||
        !string.IsNullOrWhiteSpace(config.LastGpDir) ||
        !string.IsNullOrWhiteSpace(config.LastRsXmlDir) ||
        !string.IsNullOrWhiteSpace(config.LastMidiDir) ||
        !string.IsNullOrWhiteSpace(config.LastLintFolderDir) ||
        !string.IsNullOrWhiteSpace(config.LastProjectFolderDir) ||
        !string.IsNullOrWhiteSpace(config.LastExportDir) ||
        !string.IsNullOrWhiteSpace(config.LastAlbumArtDir);

    public void RequestClose() => CancelCommand.Execute(null);

    /// <summary>Dismiss ("Skip setup" / Esc). Counts as done: the flag is set
    /// so the wizard never opens on its own again; whatever pages were already
    /// committed stay committed.</summary>
    [RelayCommand]
    private void Cancel()
    {
        if (!IsOpen) return;
        CancelScan();
        MarkCompleted(dismissed: true);
        IsOpen = false;
    }

    /// <summary>The Done page's primary (via <see cref="PrimaryCommand"/>):
    /// persist the flag and close.</summary>
    public void Finish()
    {
        MarkCompleted(dismissed: false);
        IsOpen = false;
    }

    private void MarkCompleted(bool dismissed)
    {
        var version = _appVersion();
        SaveConfig(c =>
        {
            c.OnboardingCompleted = true;
            c.OnboardingCompletedVersion = version;
        });
        _logger.Info(AppLogCategory.Application,
            dismissed
                ? $"Onboarding dismissed at {version} (skipped: {string.Join(", ", _machine.Skipped)})."
                : $"Onboarding completed at {version} (skipped: {string.Join(", ", _machine.Skipped)}).");
    }

    // ---- navigation ------------------------------------------------------------

    /// <summary>Commits the current page and moves on (the primary's verb on
    /// every page but the last, via <see cref="PrimaryCommand"/>).</summary>
    public void Next()
    {
        CommitStep(_machine.Current);
        if (_machine.Next()) MoveTo(_machine.Current);
    }

    /// <summary>Moves on WITHOUT committing the current page.</summary>
    [RelayCommand]
    private void Skip()
    {
        if (_machine.Skip()) MoveTo(_machine.Current);
    }

    [RelayCommand]
    private void Back()
    {
        if (_machine.Back()) MoveTo(_machine.Current);
    }

    private void MoveTo(OnboardingStep step)
    {
        Step = step;
        EnterStep(step);
    }

    /// <summary>What LEAVING a page through Next persists. Skip bypasses this.</summary>
    private void CommitStep(OnboardingStep step)
    {
        switch (step)
        {
            case OnboardingStep.Welcome:
                CommitWelcome();
                break;
            case OnboardingStep.Audio:
            case OnboardingStep.Input:
                // One Save covers both pages: it writes every audio field the shared
                // page holds and reconfigures the engine only when routing changed.
                if (_audioPagesOpened) _audio.SaveCommand.Execute(null);
                break;
            case OnboardingStep.Profile:
                // Switching commits on pick; a name typed and not yet applied
                // with Rename is applied here, so Next never loses it.
                CommitProfileRename();
                break;
                // Library (the folder is saved by Browse), Tone (applied on pick) and
                // Latency (the calibration wizard writes its own results) commit as
                // they go.
        }
    }

    /// <summary>What ENTERING a page needs loaded. Kept cheap and off the UI
    /// thread where it is not: the device probe and the library scan are async.</summary>
    private void EnterStep(OnboardingStep step)
    {
        switch (step)
        {
            case OnboardingStep.Library:
                if (HasLibraryPath && LibrarySongCount < 0) _ = ScanLibraryAsync();
                break;
            case OnboardingStep.Audio:
            case OnboardingStep.Input:
                EnsureAudioPagesOpened();
                break;
            case OnboardingStep.Tone:
                if (Tones.Count == 0) LoadTones();
                break;
            case OnboardingStep.Latency:
                RefreshLatencySummary();
                break;
            case OnboardingStep.Profile:
                RefreshProfiles();
                break;
            case OnboardingStep.Done:
                Summary = BuildSummary();
                break;
        }
    }

    // ---- welcome ---------------------------------------------------------------

    private void CommitWelcome()
    {
        // Only a language the user actually PICKED is written.
        //
        // LanguageIndexOf matches ConfigValue exactly and falls back to index 0
        // ("System"), so a config naming a community pack that has since been
        // removed from the languages folder — or any hand-written value, or a
        // bare "de" — seeded the picker at System and Next then wrote "system"
        // back and called SetConfiguredCulture with it. Pressing Next on a
        // Welcome page the user never touched changed the app's language. A
        // commit may not differ from an untouched config.
        var picked = SelectedLanguageIndex != _seededLanguageIndex;
        var culture = picked && SelectedLanguageIndex >= 0 && SelectedLanguageIndex < LanguageOptions.Count
            ? LanguageOptions[SelectedLanguageIndex].ConfigValue
            : null;
        SaveConfig(c =>
        {
            c.StartupScreen = StartupRouting.ScreenForRole(IsCharter);
            c.UpdateCheckOnStartup = UpdateCheckOnStartup;
            if (culture != null) c.LanguageCulture = culture;
        });
        if (culture != null) _localization.SetConfiguredCulture(culture);
    }

    private int LanguageIndexOf(string? culture)
    {
        for (var i = 0; i < LanguageOptions.Count; i++)
        {
            if (string.Equals(LanguageOptions[i].ConfigValue, culture, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return 0;
    }

    // ---- library ---------------------------------------------------------------

    [RelayCommand]
    private async Task BrowseLibrary()
    {
        var folder = await _storage.OpenFolderPickerAsync(
            Loc("Picker_SelectPsarcFolder"), HasLibraryPath ? LibraryPath : null);
        if (string.IsNullOrWhiteSpace(folder)) return;

        LibraryPath = folder;
        SaveConfig(c => c.PsarcLibraryPath = folder);
        await ScanLibraryAsync();
    }

    [RelayCommand]
    private Task RescanLibrary() => ScanLibraryAsync();

    private async Task ScanLibraryAsync()
    {
        if (!HasLibraryPath) return;
        CancelScan();
        var cts = _scanCts = new CancellationTokenSource();
        var path = LibraryPath;
        IsScanningLibrary = true;
        LibraryStatus = Loc("Onboarding_Library_Scanning");
        try
        {
            var songs = await _library.ScanAsync(path, null, cts.Token);
            if (cts.IsCancellationRequested) return;
            LibrarySongCount = songs.Count;
            LibraryStatus = songs.Count == 0
                ? Loc("Onboarding_Library_NoneFound")
                : _localization.Plural("Onboarding_Library_FoundOne", "Onboarding_Library_FoundFmt", songs.Count);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer scan or by closing — nothing to report.
        }
        catch (Exception ex)
        {
            LibrarySongCount = 0;
            LibraryStatus = LocF("Onboarding_Library_Failed", ex.Message);
            _logger.Warning(AppLogCategory.Application, $"Onboarding library scan failed: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_scanCts, cts))
            {
                IsScanningLibrary = false;
                _scanCts = null;
            }
            cts.Dispose();
        }
    }

    private void CancelScan()
    {
        var cts = _scanCts;
        _scanCts = null;
        if (cts == null) return;
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { /* the scan's finally already disposed it */ }
        IsScanningLibrary = false;
    }

    // ---- audio -----------------------------------------------------------------

    private void EnsureAudioPagesOpened()
    {
        if (_audioPagesOpened) return;
        _audioPagesOpened = true;
        // The Settings tab's own Open: cheap fields now, the 200+ ms device probe
        // on the thread pool, lists marshalled back. Its IsOpen flag is bound by
        // nothing outside the Settings tab, so flipping it here is inert.
        Audio.Open();
    }

    /// <summary>Applies the chosen output (the Settings Save, which restarts the
    /// engine only if routing changed) and plays the engine's own click through
    /// it. The result line says what happened, including "no engine".</summary>
    [RelayCommand]
    private void TestAudio()
    {
        EnsureAudioPagesOpened();
        _audio.SaveCommand.Execute(null);

        if (_engine.ActiveSampleRate <= 0)
        {
            AudioTestFailed = true;
            AudioTestStatus = Loc("Onboarding_Audio_TestNoEngine");
            return;
        }

        try
        {
            _engine.PlayClap();
            AudioTestFailed = false;
            AudioTestStatus = LocF("Onboarding_Audio_TestPlayed", OutputDeviceLabel(), _engine.ActiveSampleRate);
        }
        catch (Exception ex)
        {
            AudioTestFailed = true;
            AudioTestStatus = LocF("Onboarding_Audio_TestFailed", ex.Message);
            _logger.Warning(AppLogCategory.Audio, $"Onboarding audio test failed: {ex.Message}");
        }
    }

    private string OutputDeviceLabel()
    {
        var name = Audio.SelectedDriver == "ASIO"
            ? NameAt(Audio.AsioDevices, Audio.SelectedAsioOutputIndex)
            : NameAt(Audio.WasapiDevices, Audio.SelectedWasapiOutputIndex);
        return name.Length == 0 ? Loc("Onboarding_DefaultDevice") : name;
    }

    private string InputDeviceLabel()
    {
        if (!Audio.EnableInput) return Loc("Onboarding_Input_Off");
        var name = Audio.SelectedInputDriver == "ASIO"
            ? NameAt(Audio.AsioInputDevicesList, Audio.SelectedAsioInputIndex)
            : NameAt(Audio.WasapiInputDevicesList, Audio.SelectedWasapiInputIndex);
        return name.Length == 0 ? Loc("Onboarding_DefaultDevice") : name;
    }

    private static string NameAt(IReadOnlyList<string> items, int index) =>
        index >= 0 && index < items.Count ? items[index] : string.Empty;

    // ---- input -----------------------------------------------------------------

    /// <summary>Applies the input choice so the meter below has something to
    /// read — the engine only opens the input the config names.</summary>
    [RelayCommand]
    private void ApplyInput()
    {
        EnsureAudioPagesOpened();
        _audio.SaveCommand.Execute(null);
        InputStatus = _engine.InputSampleRate > 0
            ? LocF("Onboarding_Input_Applied", InputDeviceLabel())
            : Loc("Onboarding_Input_NoCapture");
    }

    /// <summary>One meter sample: the latest captured window's peak, off the
    /// engine's non-destructive window read (the tuner's tap, so it never starves
    /// live note detection). Called by the view's timer while the Input page is
    /// showing; a no-engine build reads 0 samples and reports silence.</summary>
    public void PollInputMeter()
    {
        var read = _engine.ReadInputCaptureWindow(_meterBuffer);
        var peak = 0f;
        for (var i = 0; i < read; i++)
        {
            var a = Math.Abs(_meterBuffer[i]);
            if (a > peak) peak = a;
        }

        InputLevelDb = peak <= 0f ? double.NegativeInfinity : 20.0 * Math.Log10(peak);
    }

    // ---- tone ------------------------------------------------------------------

    private void LoadTones()
    {
        Tones.Clear();
        foreach (var preset in _presets.AllPresets)
            Tones.Add(new OnboardingToneChoice(preset, preset.Name, PresetBrowserViewModel.CategoryLabel(_localization, preset.Category)));
    }

    // ---- latency ---------------------------------------------------------------

    /// <summary>Re-reads the summary however the wizard closed: a judging run writes its offset without an Apply.</summary>
    [RelayCommand]
    private async Task CalibrateLatency()
    {
        await LatencyWizard.OpenAsync();
        if (IsOpen) RefreshLatencySummary();
    }

    private void RefreshLatencySummary()
    {
        var config = _configService.LoadAppConfig();
        LatencySummary = LocF("Onboarding_Latency_Current",
            Math.Round(config.LivePlayLatencyOffsetMs, 1), Math.Round(config.VisualLeadMs, 1));
    }

    // ---- profile ---------------------------------------------------------------

    private void RefreshProfiles()
    {
        _syncingProfile = true;
        try
        {
            Profiles.Clear();
            foreach (var p in _profiles.Profiles) Profiles.Add(p);
            var active = Profiles.FirstOrDefault(p => p.Id == _profiles.ActiveProfile.Id);
            SelectedProfile = active;
            // Written explicitly rather than left to OnSelectedProfileChanged.
            // That hook only fires on a CHANGE, and re-selecting the same
            // PlayerProfile instance is not one — so an edit typed into the name
            // box and never applied survived a Cancel and a whole second run of
            // the wizard, and the next Next() committed it as a rename.
            ProfileName = active?.Name ?? string.Empty;
        }
        finally
        {
            _syncingProfile = false;
        }

        OnPropertyChanged(nameof(CanCreateProfile));
        CreateProfileCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanRenameProfile));
        RenameProfileCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    ///     A name nobody else already answers to. Two profiles called the same
    ///     thing are indistinguishable everywhere a profile is shown BY NAME —
    ///     the picker, the local leaderboard rows — while every
    ///     score, best and practice record behind them stays split across two
    ///     ids. The wizard's own profile page exists to REPLACE the shipped
    ///     "Player 1", not to sit a second one beside it, so typing the name that
    ///     is already there produced exactly the outcome the page was added to
    ///     prevent.
    /// </summary>
    private bool NameIsFree(string name, Guid? except = null)
    {
        foreach (var profile in _profiles.Profiles)
        {
            if (except is { } id && profile.Id == id) continue;
            if (string.Equals(profile.DisplayName.Trim(), name, StringComparison.CurrentCultureIgnoreCase))
                return false;
        }

        return true;
    }

    public bool CanCreateProfile =>
        !string.IsNullOrWhiteSpace(NewProfileName) && NameIsFree(NewProfileName.Trim());

    /// <summary>
    ///     Whether <see cref="ProfileStatus" /> is currently a REFUSAL rather
    ///     than a result. The view paints the strip with it — it was hard-coded
    ///     <c>Classes="DlgStrip Ok"</c>, so "that name is taken" was shown in the
    ///     success colour, which is the pattern the Audio page's own strip
    ///     already got right (<c>Classes.Error="{Binding AudioTestFailed}"</c>).
    /// </summary>
    [ObservableProperty] private bool _profileStatusIsRefusal;

    partial void OnNewProfileNameChanged(string value)
    {
        OnPropertyChanged(nameof(CanCreateProfile));
        CreateProfileCommand.NotifyCanExecuteChanged();
        var name = value.Trim();
        if (name.Length > 0 && !NameIsFree(name)) RefuseName(name);
        else ClearRefusal();
    }

    [RelayCommand(CanExecute = nameof(CanCreateProfile))]
    private void CreateProfile()
    {
        var name = NewProfileName.Trim();
        if (name.Length == 0 || !NameIsFree(name)) return;
        var created = _profiles.Create(name);
        _profiles.SwitchTo(created.Id);
        NewProfileName = string.Empty;
        RefreshProfiles();
        SetProfileResult(LocF("Onboarding_Profile_Created", created.Name));
        ReapplyStarterTone(created);
    }

    public bool CanRenameProfile =>
        SelectedProfile is { } p && !string.IsNullOrWhiteSpace(ProfileName) &&
        !string.Equals(ProfileName.Trim(), p.DisplayName, StringComparison.Ordinal) &&
        NameIsFree(ProfileName.Trim(), p.Id);

    partial void OnProfileNameChanged(string value)
    {
        OnPropertyChanged(nameof(CanRenameProfile));
        RenameProfileCommand.NotifyCanExecuteChanged();
        var name = value.Trim();
        if (name.Length > 0 && SelectedProfile is { } p && !NameIsFree(name, p.Id)) RefuseName(name);
        else ClearRefusal();
    }

    /// <summary>
    ///     Says a name is already taken. Paired with <see cref="ClearRefusal" />
    ///     on purpose: a refusal that is written and never withdrawn would, after
    ///     typing "Player 1", seeing it refused and then typing "Ana", leave
    ///     "Player 1 is already taken" on screen beside an ENABLED Create button.
    ///     The user is told the name they are looking at is refused when it is
    ///     not.
    /// </summary>
    private void RefuseName(string name)
    {
        ProfileStatus = LocF("Onboarding_Profile_NameTaken", name);
        ProfileStatusIsRefusal = true;
    }

    /// <summary>Withdraws a standing refusal — and ONLY a refusal, so a
    /// "created" / "renamed" / "now active" result typed over is left where the
    /// user can still read it.</summary>
    private void ClearRefusal()
    {
        if (!ProfileStatusIsRefusal) return;
        ProfileStatus = string.Empty;
        ProfileStatusIsRefusal = false;
    }

    /// <summary>A RESULT — created, renamed, now active, or cleared. Always
    /// takes the strip out of its refusal state, so a stale "that name is
    /// taken" can never outlive the thing that answered it.</summary>
    private void SetProfileResult(string text)
    {
        ProfileStatus = text;
        ProfileStatusIsRefusal = false;
    }

    [RelayCommand(CanExecute = nameof(CanRenameProfile))]
    private void RenameProfile() => CommitProfileRename();

    /// <summary>Applies a pending edit of the selected profile's name. A no-op
    /// when nothing changed, so Next on an untouched page writes nothing.</summary>
    private void CommitProfileRename()
    {
        if (!CanRenameProfile || SelectedProfile is not { } profile) return;
        var name = ProfileName.Trim();
        _profiles.Rename(profile.Id, name);
        RefreshProfiles();
        SetProfileResult(LocF("Onboarding_Profile_Renamed", name));
    }

    /// <summary>
    ///     A profile made here starts with NO rig of its own, and the tone rig
    ///     follows the profile (<c>ProfileToneRigCoordinator</c>): the switch
    ///     flushes the board the Tone page just built into the OLD profile and
    ///     brings the new one up from its store — empty. So the starter tone the
    ///     player picked two pages ago vanished the moment they typed their name.
    ///     Re-apply the pick — or the shipped default
    ///     when nothing was picked — through the same path the Tone page uses,
    ///     so an open board re-syncs too.
    /// </summary>
    private void ReapplyStarterTone(PlayerProfile created)
    {
        var preset = SelectedTone?.Preset;
        if (preset is null)
        {
            var defaultId = (_configService.LoadAppConfig().DefaultTonePresetId ?? string.Empty).Trim();
            if (defaultId.Length > 0) preset = _presets.FindById(defaultId);
        }
        if (preset is null) return;

        var board = _pedalboard?.Invoke();
        if (board is not null) board.Browser.ApplyPreset(preset);
        else _presets.ApplyToPlayer(0, preset);
        _logger.Info(AppLogCategory.Audio,
            $"Onboarding: re-applied starter tone '{preset.Id}' to seat 0 for the new profile '{created.Name}'.");
    }

    // ---- done ------------------------------------------------------------------

    private string BuildSummary()
    {
        var config = _configService.LoadAppConfig();
        var skipped = _machine.Skipped;
        var lines = new List<string?>
        {
            Line("Onboarding_Summary_Role",
                StartupRouting.BootsIntoPlayMode(config) ? Loc("Onboarding_Role_Player") : Loc("Onboarding_Role_Charter")),
            HasLibraryStep ? Line("Onboarding_Summary_Library",
                skipped.Contains(OnboardingStep.Library) || string.IsNullOrWhiteSpace(config.PsarcLibraryPath)
                    ? Loc("Onboarding_Summary_NotSet")
                    : LibrarySongCount >= 0
                        ? $"{config.PsarcLibraryPath} ({_localization.Plural("Onboarding_Library_FoundOne", "Onboarding_Library_FoundFmt", LibrarySongCount)})"
                        : config.PsarcLibraryPath) : null,
            Line("Onboarding_Summary_Output",
                skipped.Contains(OnboardingStep.Audio) ? Loc("Onboarding_Summary_NotSet") : $"{config.AudioDriver} · {OutputDeviceLabel()}"),
            Line("Onboarding_Summary_Input",
                skipped.Contains(OnboardingStep.Input) ? Loc("Onboarding_Summary_NotSet") : InputDeviceLabel()),
            Line("Onboarding_Summary_Tone",
                SelectedTone is null ? Loc("Onboarding_Summary_Unchanged") : SelectedTone.Name),
            Line("Onboarding_Summary_Latency",
                LocF("Onboarding_Latency_Current", Math.Round(config.LivePlayLatencyOffsetMs, 1), Math.Round(config.VisualLeadMs, 1))),
            Line("Onboarding_Summary_Profile", _profiles.ActiveProfile.DisplayName)
        };
        return string.Join(Environment.NewLine, lines.OfType<string>());

        string Line(string key, string value) => $"{Loc(key)}: {value}";
    }

    // ---- shared ----------------------------------------------------------------

    private void SaveConfig(Action<AppConfig> mutate)
    {
        var config = _configService.LoadAppConfig();
        mutate(config);
        _configService.SaveAppConfig(config);
    }

    private static string CurrentAppVersion() =>
        DotCharter.Services.Infrastructure.AppVersionInfo.Current;

    private string Loc(string key) => _localization[key];
    private string LocF(string key, params object[] args) => _localization.Format(key, args);
}
