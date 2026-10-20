// PlayModeViewModel: the pre-play FLOW state (song, path, tuning, stems, speed) from picking a song through the tuning check to the countdown.

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.Core.Graphics;

namespace DotCharter.UI.ViewModels.PlayMode;

/// <summary>
///     The pre-play FLOW: the <c>Flow*</c> state a picked song produces (title,
///     tuning, paths), the tuning check and drop tuner, the backing-track stem
///     mutes and dynamic mix, practice speed and scroll speed, the guest session,
///     and the countdown that hands off to <c>BeginGameplay</c>.
///     <para>
///         Part of <see cref="PlayModeViewModel" />. Injected dependencies and
///         plain private fields live in the ROOT file; see
///         <c>PlayModeViewModel.cs</c> for the full field rule and
///         why the split exists.
///     </para>
/// </summary>
public sealed partial class PlayModeViewModel
{
    // ---- Gameplay flow -------------------------------------------------------

    [ObservableProperty] private string _flowSongTitle = "";

    /// <summary>"Erwartete Stimmung" on the pre-play screen: the common-tuning NAME
    /// when recognised, else the raw note letters. The raw letters are always in
    /// <see cref="FlowTuningNotes"/> for the tooltip.</summary>
    [ObservableProperty] private string _flowTuning = "";

    /// <summary>Raw per-string note letters of the expected tuning — the always-on
    /// tooltip for <see cref="FlowTuning"/> (and unchanged when the name is unknown).</summary>
    [ObservableProperty] private string _flowTuningNotes = "";
    [ObservableProperty] private string _flowArrangement = "";
    [ObservableProperty] private int _countdownValue;
    [ObservableProperty] private string _countdownLabel = "";
    [ObservableProperty] private string _flowError = "";

    private PlaySongItem? _flowSong;

    // ---- Pre-play setup: path, tuning, drop tuner, backing track ------------

    public ObservableCollection<ArrangementOption> FlowArrangements { get; } = [];

    [ObservableProperty] private ArrangementOption? _selectedFlowArrangement;

    /// <summary>Green badge: this tuning was confirmed on a previous run and
    /// the player opted out of re-checking (config AlwaysAskTuning).</summary>
    [ObservableProperty] private bool _isTuningVerified;

    [ObservableProperty] private bool _hasStems;
    [ObservableProperty] private bool _muteMyInstrument;

    partial void OnMuteMyInstrumentChanged(bool value)
    {
        var config = _configService.LoadAppConfig();
        if (config.GameplayMuteMyInstrument == value) return;
        config.GameplayMuteMyInstrument = value;
        _configService.SaveAppConfig(config);
    }

    /// <summary>Dynamic backing mix (0.21): duck-on-miss / swell-on-streak of the
    /// player's stem. Standing preference, persisted like <see cref="MuteMyInstrument"/>.</summary>
    [ObservableProperty] private bool _dynamicStemMix;

    partial void OnDynamicStemMixChanged(bool value)
    {
        var config = _configService.LoadAppConfig();
        if (config.GameplayDynamicStemMix != value)
        {
            config.GameplayDynamicStemMix = value;
            _configService.SaveAppConfig(config);
        }

        // Toggling mid-run (from the gameplay-settings overlay while paused/playing)
        // takes effect immediately: off restores the stem, on re-arms if playing.
        if (!value) _stemDuck.Stop();
        else if (CurrentScreen == PlayModeScreen.Playing) StartStemDuck();
    }

    /// <summary>Arms the dynamic-mix follower for the current run: resolves the
    /// player's stem and points the follower at it. No-op when the feature is off,
    /// the player muted their own instrument (nothing audible to duck), or the song
    /// has no separated stems.</summary>
    private void StartStemDuck()
    {
        if (!DynamicStemMix || MuteMyInstrument || _livePlay == null) return;
        var stems = ResolveInstrumentStems();
        if (stems.Count == 0) return;
        _stemDuck.Start(stems.Select(s => (s.Name, (Func<double>)(() => s.VolumeLinear))).ToList(),
            () => _livePlay!.Streak);
    }

    /// <summary>A bass PART — the Bass path, or a 5/6-string bass charted on
    /// a guitar path at A220 — plays against the bass stem.</summary>
    private static bool IsBassPart(Arrangement? arrangement) =>
        arrangement != null && ArrangementInstrument.IsBassPart(arrangement);

    /// <summary>The stems standing in for the instrument the player is playing —
    /// guitar on a 6-stem split, "other" on 4-stem (guitar is folded there), bass
    /// for a bass arrangement; with Guitar roles on the guitar is TWO stems,
    /// Lead and Rhythm, and both are followed. Mirrors
    /// <see cref="ApplyBackingTrackMutes"/>'s target rule, which already matches
    /// every stem whose name contains the target; empty when the song has no stems.</summary>
    internal IReadOnlyList<StemTrackViewModel> ResolveInstrumentStems()
    {
        if (_stemMixer == null) return [];
        var tracks = _stemMixer.Tracks.Where(t => !t.IsMasterMix).ToList();
        if (tracks.Count == 0) return [];

        var isBass = IsBassPart(SelectedFlowArrangement?.Arrangement);
        string target;
        if (isBass) target = "bass";
        else if (tracks.Any(t => t.Name.Contains("guitar", StringComparison.OrdinalIgnoreCase)))
            target = "guitar";
        else target = "other";

        return tracks.Where(t => t.Name.Contains(target, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>
    ///     Practice speed for the run: 100 = full speed, down to 50 %. Applied to
    ///     the transport by <c>BeginGameplay</c>, and live by
    ///     <c>PlayModeSettingsViewModel.ApplyGameplaySettingsToLiveRun</c> when the gameplay-settings
    ///     overlay changes it MID-RUN (the overlay opens from the pause menu
    ///     precisely so the song can be slowed where it is hard). The transport's
    ///     pre-run speed is restored by <c>EndRunSideEffects</c>.
    ///     <para>
    ///         <b>The property itself belongs to the PLAYER and is not restored.</b>
    ///         Nothing but the player writes it: the two run-scoped overrides
    ///         (the smart-practice drill and the LAN host's speed) do not exist,
    ///         so there is no stash-and-restore.
    ///     </para>
    /// </summary>
    [ObservableProperty] private int _practiceSpeedPercent = 100;

    /// <summary>Selectable practice speeds for the pre-play picker.</summary>
    public IReadOnlyList<int> SpeedOptions { get; } = new[] { 100, 90, 80, 70, 60, 50 };

    /// <summary>
    ///     Highway scroll speed for THIS playthrough. Seeded on the pre-song card
    ///     from whatever the song would use anyway (the chart's own value, or the
    ///     global override, or Rocksmith's 1.3), so the player is shown the real
    ///     number and can simply type over it. Rocksmith units — LOWER IS FASTER.
    ///     Applied at gameplay start and restored on run end; it never touches
    ///     AppConfig, which is what makes it per-playthrough.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunVisibleSecondsLabel))]
    private double _runScrollSpeed = HighwaySpeedPolicy.DefaultScrollSpeed;

    /// <summary>True when the loaded chart carried its own scroll speed, so the
    /// card can say whose number the player is looking at.</summary>
    [ObservableProperty] private bool _runScrollSpeedIsFromChart;

    /// <summary>The only figure that means anything to a player: how many seconds
    /// of chart the board will hold at this speed. Scroll speed being inverted
    /// (lower = faster) is exactly why this is shown.</summary>
    public string RunVisibleSecondsLabel =>
        (HighwaySpeedPolicy.HorizonMs((float)RunScrollSpeed, (float)_projectState.HighwayDrawDistanceZ) / 1000f)
        .ToString("0.0") + " s";

    private double _preRunScrollSpeed = HighwaySpeedPolicy.DefaultScrollSpeed;

    /// <summary>Raised by "separate stems" on the pre-play screen; the shell
    /// owns the (long-running) separation command.</summary>
    public event Action? StemSeparationRequested;

    [RelayCommand]
    private void RequestStemSeparation() => StemSeparationRequested?.Invoke();

    partial void OnSelectedFlowArrangementChanged(ArrangementOption? value)
    {
        if (value == null) return;
        _projectState.ActiveArrangement = value.Arrangement;
        FlowArrangement = value.Name;
        FlowTuning = value.TuningDisplayLabel; // resolved name (falls back to raw notes)
        FlowTuningNotes = value.TuningLabel;   // raw letters for the tooltip
        RefreshTuningState(value);
        RefreshInGameToneState(); // tones are per-arrangement
        SeedRunScrollSpeed();     // so is scroll speed
        RefreshGetReadyForArrangement(value); // and so are the best + the tuning lockup
        // Retarget the guided check when the player switches path on the screen —
        // a bass and a lead check different strings.
        if (IsTuningCheckScreen) BeginGuidedTuning();
    }

    /// <summary>Does this loaded arrangement answer to the path label the song
    /// library's detail rail was showing? Pure and case-insensitive; a null or
    /// empty pick never matches, so the caller falls through to its own
    /// default.</summary>
    internal static bool MatchesRailPick(ArrangementOption option, string? label)
        => !string.IsNullOrWhiteSpace(label) &&
           (string.Equals(option.Name, label, StringComparison.CurrentCultureIgnoreCase) ||
            string.Equals(option.TypeLabel, label, StringComparison.CurrentCultureIgnoreCase));

    private void RefreshTuningState(ArrangementOption option)
    {
        var config = _configService.LoadAppConfig();
        IsTuningVerified = !config.AlwaysAskTuning &&
                           option.TuningLabel == config.LastVerifiedTuning;
    }

    /// <summary>Internal rather than private so a test can put the pre-play
    /// screen into the state a real song load produces, without a library, a
    /// psarc and an audio device.</summary>
    internal void PreparePrePlay()
    {
        FlowArrangements.Clear();
        var project = _projectState.ActiveProject;
        if (project != null)
        {
            // Named before they are added: a row reads DisplayName once, when it binds.
            var options = project.Arrangements.Where(a => a.Levels.Count > 0)
                .Select(arr => new ArrangementOption(arr, _localization)).ToList();
            ArrangementOption.AssignDisplayNames(options);
            foreach (var option in options) FlowArrangements.Add(ApplyArrangementBest(option));
        }

        // The library rail's arrangement pick is a real decision, so it wins
        // here: choosing "Bass" on the detail rail is choosing the path the run
        // opens on. Falls through to the active/first arrangement when the rail
        // was never used or the label does not resolve (an own chart's paths are
        // named by the project, not by a Rocksmith route mask).
        SelectedFlowArrangement =
            FlowArrangements.FirstOrDefault(o => MatchesRailPick(o, SelectedArrangement?.Label))
            ?? FlowArrangements.FirstOrDefault(o => ReferenceEquals(o.Arrangement, _projectState.ActiveArrangement))
            ?? FlowArrangements.FirstOrDefault();

        HasStems = _stemMixer?.HasStems == true;
        // Remembered across runs — it is a standing preference ("I always want to hear
        // the band, not the recorded guitarist"), not a per-song decision. It was
        // being reset to false on every pre-play, so it could never be remembered.
        var config = _configService.LoadAppConfig();
        MuteMyInstrument = config.GameplayMuteMyInstrument;
        DynamicStemMix = config.GameplayDynamicStemMix;
        // Per SONG: the drop tuner is a decision about this song's tuning.
        DropTuner.LoadSong(_flowSong?.SongIdentity);
        SeedRunScrollSpeed();
        LoadSongTonePreference();
        RefreshInGameToneState();
        RefreshGetReadySongFacts();
        RefreshGetReadyForArrangement(SelectedFlowArrangement);
    }

    /// <summary>
    ///     Shows the player the speed this song would actually play at — resolved
    ///     exactly as the run will resolve it — so the pre-song box is pre-filled
    ///     with the truth and typing over it is an override rather than a guess.
    /// </summary>
    private void SeedRunScrollSpeed()
    {
        var config = _configService.LoadAppConfig();
        var chartValue = SelectedFlowArrangement?.Arrangement.ScrollSpeed;

        RunScrollSpeedIsFromChart = chartValue.HasValue;
        RunScrollSpeed = HighwaySpeedPolicy.ResolveScrollSpeed(
            perRun: null,
            globalOverride: config.HighwayScrollSpeedOverrideEnabled
                ? (float)config.HighwayScrollSpeed
                : null,
            chartValue: (float?)chartValue);
    }

    /// <summary>Called by the shell after a stem separation it ran for us finished.</summary>
    public void RefreshStemAvailability()
    {
        HasStems = _stemMixer?.HasStems == true;
        RefreshBackingStems();
        RefreshStemSeparationEstimate();
    }

    private readonly List<StemTrackViewModel> _mutedForRun = [];

    /// <summary>
    ///     Silences the recorded player of YOUR part for the run, so you hear the band
    ///     and not the guitarist you are trying to replace.
    /// </summary>
    /// <remarks>
    ///     Stem naming is the demucs model's, and the two models differ in a way that
    ///     matters: <c>htdemucs-6s</c> separates a real <c>guitar</c> stem, while
    ///     <c>htdemucs-4s</c> has no guitar at all — the guitar is folded into
    ///     <c>other</c>. So on 6-stem we mute ONLY <c>guitar</c>; the previous code
    ///     muted <c>guitar</c> AND <c>other</c>, which also killed the keys, synths and
    ///     strings that 6-stem correctly separated out.
    ///     <para>
    ///         On 4-stem, muting <c>other</c> is the best available answer and it does
    ///         take the keys with it. That is the model's coarseness, not a bug — the
    ///         fix is to separate with 6-stem.
    ///     </para>
    ///     <para>
    ///         Mutes are RUN-SCOPED (<see cref="StemTrackViewModel.SetMutedForRun" />):
    ///         they must not write the domain <c>Stem.Muted</c> flag, which is
    ///         serialized into the .rscp. Writing it would mean a crash mid-run left the
    ///         stem permanently muted in the saved project.
    ///     </para>
    /// </remarks>
    private void ApplyBackingTrackMutes()
    {
        RestoreBackingTrackMutes();
        if (!MuteMyInstrument || _stemMixer == null) return;

        var candidates = _stemMixer.Tracks
            .Where(t => !t.IsMasterMix && !t.IsMuted) // never disturb a mute the player set
            .ToList();

        var isBass = IsBassPart(SelectedFlowArrangement?.Arrangement);

        string target;
        if (isBass) target = "bass";
        else if (candidates.Any(t => t.Name.Contains("guitar", StringComparison.OrdinalIgnoreCase)))
            target = "guitar"; // 6-stem: a real guitar stem exists — mute just that
        else target = "other"; // 4-stem: guitar is folded into "other"

        foreach (var track in candidates)
        {
            if (!track.Name.Contains(target, StringComparison.OrdinalIgnoreCase)) continue;
            track.SetMutedForRun(true);
            _mutedForRun.Add(track);
        }

        if (_mutedForRun.Count > 0)
        {
            _logger.Info(AppLogCategory.Audio,
                $"Backing mix: muted [{string.Join(", ", _mutedForRun.Select(t => t.Name))}] for this run.");
        }
    }

    private void RestoreBackingTrackMutes()
    {
        foreach (var track in _mutedForRun)
        {
            track.SetMutedForRun(false);
            // The mute cleared the UI solo flag but deliberately left the project's
            // alone — put the two back in step.
            track.RestoreSoloFromProject();
        }

        _mutedForRun.Clear();
    }

    [RelayCommand]
    private async Task PlaySong(PlaySongItem? item)
    {
        item ??= SelectedSong;
        if (item is not { CanPlay: true }) return;
        if (!await LoadFlowSong(item)) return;

        CurrentScreen = PlayModeScreen.TuningCheck;
    }

    /// <summary>Loads a song into the flow (project/psarc load + pre-play
    /// prep) without changing the screen.</summary>
    private async Task<bool> LoadFlowSong(PlaySongItem item)
    {
        FlowError = "";
        SongSelect.IsLibraryLoading = true;
        try
        {
            bool loaded;
            if (item.IsOwnChart)
            {
                var previousProject = _projectState.ActiveProject;
                await _projectSession.LoadFile(item.ProjectPath);
                loaded = _projectState.ActiveProject != null
                    && !ReferenceEquals(previousProject, _projectState.ActiveProject);
            }
            else
            {
                if (_songBrowser == null) return false;
                loaded = await _songBrowser.LoadSongItem(item.Inner!, raiseSongLoaded: false);
            }

            if (!loaded)
            {
                FlowError = _localization["PlayMode_LoadFailed"];
                return false;
            }

            _flowSong = item;
            FlowSongTitle = item.IsOwnChart ? item.Title : item.Inner!.Song.DisplayTitle;
            PreparePrePlay();
            return true;
        }
        finally
        {
            SongSelect.IsLibraryLoading = false;
        }
    }

    /// <summary>Start after checking the tuning: remembers it so the next
    /// song in the same tuning shows the verified badge instead of nagging.</summary>
    [RelayCommand]
    private void ConfirmTuning()
    {
        // Neither the drop tuner nor the tuning-match shift tuned the guitar to the
        // CHART's tuning, so neither may record it as verified.
        if (SelectedFlowArrangement != null && !DropTuner.IsOn &&
            TuningMatch.EffectiveSemitones == 0)
        {
            var config = _configService.LoadAppConfig();
            config.LastVerifiedTuning = SelectedFlowArrangement.TuningLabel;
            _configService.SaveAppConfig(config);
        }

        BeginCountdown();
    }

    /// <summary>Always available: start without any tuning claim.</summary>
    [RelayCommand]
    private void SkipTuning() => BeginCountdown();

    // ---- Guest session fast path (v0.31 "friends-onboarding-lite") -----------
    //
    // A LAN guest gets playing in ~2 minutes: type a name, calibrate, tune,
    // play. This is a fast PATH, not a new onboarding system: the profile is
    // an ordinary IPlayerProfileService profile (delete it after the night
    // from the profile switcher), latency calibration is the EXISTING wizard
    // dialog, and the tuner is the EXISTING per-song TuningCheck screen that
    // already gates the first launch — nothing is forked. A roamed profile
    // from the 0.28 LAN import satisfies the same need for guests who bring
    // their own data: import it, switch to it, skip this button.

    /// <summary>The inline guest-entry row on the Play Mode menu is open.</summary>
    [ObservableProperty] private bool _isGuestEntryOpen;

    /// <summary>The guest's typed name (sanitized on start).</summary>
    [ObservableProperty] private string _guestName = "";

    [RelayCommand]
    private void ToggleGuestEntry() => IsGuestEntryOpen = !IsGuestEntryOpen;

    /// <summary>Creates + activates the guest profile, lands in Song Select
    /// and opens the latency wizard on top — the guest calibrates, picks a
    /// song, and the existing tuning check runs before the first run.</summary>
    [RelayCommand]
    private void StartGuestSession()
    {
        var name = GuestSessionNaming.Resolve(GuestName, _profiles.Profiles.Select(p => p.DisplayName));
        var guest = _profiles.Create(name);
        _profiles.SwitchTo(guest.Id);
        GuestName = "";
        IsGuestEntryOpen = false;

        CurrentScreen = PlayModeScreen.SongSelect;
        // The existing wizard dialog scrims over Song Select; when the guest
        // finishes (or skips) calibration they are already where they play.
        _ = _latencyPrompt?.Invoke()?.OpenAsync();
        _logger.Info(AppLogCategory.Application,
            $"Guest session started: profile '{name}' created and activated.");
    }

    /// <summary>Consumed once by <see cref="BeginGameplay"/>: start the run at
    /// this chart time (minus the usual lead-in) instead of the chart start —
    /// the rock meter's retry-from-section. Set ONLY through
    /// <see cref="BeginCountdown"/>, which is what keeps a stale target from
    /// leaking into an unrelated later run.</summary>
    private double? _pendingRunStartOverrideMs;

    private void BeginCountdown(double? runStartOverrideMs = null)
    {
        // Every countdown states its start target (default: the chart start),
        // so an abandoned retry can never leak into the next ordinary run.
        _pendingRunStartOverrideMs = runStartOverrideMs;
        CountdownValue = 3;
        CountdownLabel = "3";
        CurrentScreen = PlayModeScreen.Countdown;
        _countdownTimer.Start();
    }

    private void CountdownTick()
    {
        CountdownValue--;
        if (CountdownValue > 0)
        {
            CountdownLabel = CountdownValue.ToString();
            return;
        }

        _countdownTimer.Stop();
        CountdownLabel = _localization["PlayMode_Countdown_Go"];
        BeginGameplay();
    }

    private int _preRunPitchShift;
    private double _preRunSpeed = 1.0;

    /// <summary>
    ///     True from the moment <c>BeginGameplay</c> applies a run's transport /
    ///     highway side effects until <c>EndRunSideEffects</c> undoes them.
    ///     <para>
    ///         It exists because a run CHAIN — Retry from the results screen,
    ///         Restart from the pause menu, retry-from-section after a fail-out,
    ///         or re-entering after a refused start — reaches
    ///         <c>BeginGameplay</c> again WITHOUT passing through
    ///         <c>EndRunSideEffects</c>. The <c>_preRun*</c> baselines must be
    ///         captured only on the way IN to such a chain, never re-taken from
    ///         values the previous run already applied.
    ///     </para>
    /// </summary>
    private bool _runSideEffectsApplied;
}
