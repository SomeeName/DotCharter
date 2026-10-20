// PlayModeViewModel: the CurrentScreen switch, the Is*Screen predicates, the backdrop + input-signal probe those transitions drive, and the screen-navigation commands — OpenSongSelect, Back, NavigateBack.

using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.UI.Controls;

namespace DotCharter.UI.ViewModels.PlayMode;

/// <summary>
///     Screen state: <see cref="PlayModeViewModel.CurrentScreen" /> and its switch
///     handler, the <c>Is*Screen</c> predicates the markup binds, and the menu
///     backdrop + input-signal probe those transitions drive. The
///     <see cref="PlayModeScreen" /> enum itself is in <c>PlayModeScreen.cs</c>.
///     <para>
///         Part of <see cref="PlayModeViewModel" />. Injected dependencies and
///         plain private fields live in the ROOT file; see
///         <c>PlayModeViewModel.cs</c> for the full field rule and
///         why the split exists.
///     </para>
/// </summary>
public sealed partial class PlayModeViewModel
{
    // ---- Navigation ----------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStartScreen))]
    [NotifyPropertyChangedFor(nameof(IsSongSelectScreen))]
    [NotifyPropertyChangedFor(nameof(IsTuningCheckScreen))]
    [NotifyPropertyChangedFor(nameof(IsCountdownScreen))]
    [NotifyPropertyChangedFor(nameof(IsPlayingScreen))]
    [NotifyPropertyChangedFor(nameof(IsPausedScreen))]
    [NotifyPropertyChangedFor(nameof(IsFailedScreen))]
    [NotifyPropertyChangedFor(nameof(IsResultsScreen))]
    [NotifyPropertyChangedFor(nameof(IsBackdropVisible))]
    [NotifyPropertyChangedFor(nameof(BackdropIntensity))]
    private PlayModeScreen _currentScreen = PlayModeScreen.Start;

    partial void OnCurrentScreenChanged(PlayModeScreen value)
    {
        UpdateSignalProbe(value);
        UpdateAnnouncement(value);

        // The LAUNCH screen is the app's boot surface (CurrentScreen starts at
        // Start), so this hop runs before the first frame — EnterLaunchScreen
        // publishes only in-memory reads and defers the library scan behind it
        // to DispatcherPriority.Background.
        if (value == PlayModeScreen.Start) EnterLaunchScreen();
        // The facts Song Select reads (the verified tuning above all) —
        // recomputed on every entry. Cheap reads, never a scan.
        if (value == PlayModeScreen.SongSelect) RefreshFrontDoor();

        // Guided tuning check: live only while the pre-play Tuning-check screen
        // is up. Stop() on every other screen provably parks its poll timer.
        if (value == PlayModeScreen.TuningCheck)
            BeginGuidedTuning();
        else
            GuidedTuning.Stop();

        // Dynamic backing mix: follow the streak only while actually playing.
        // Stop() runs on EVERY other screen (pause, results, abort, retry), which
        // is what guarantees the stem is restored on every exit path.
        if (value == PlayModeScreen.Playing)
            StartStemDuck();
        else
            _stemDuck.Stop();
        // Listen-back borrows the master transport. Leaving the screen that
        // offers it (results) must hand the song straight back — most of all
        // before a new run, which would otherwise start on the take.
        if (ListenBack.IsTakePlaying && value != PlayModeScreen.Results)
            _ = ListenBack.StopAsync();
    }

    // ---- Pre-play input signal meter ----------------------------------------
    // "0 % the whole time" almost always means the app never heard the
    // guitar (another instance holding the exclusive ASIO input, wrong
    // channel, unplugged interface). Show the input level BEFORE the run so
    // a dead input is caught on the pre-play screen, not after a zero score.

    [ObservableProperty] private double _inputSignalLevel;
    [ObservableProperty] private bool _isInputSignalMissing;

    private void UpdateSignalProbe(PlayModeScreen screen)
    {
        if (screen == PlayModeScreen.TuningCheck && _livePlay != null)
        {
            if (_signalTimer != null) return;
            _signalSilentTicks = 0;
            _signalTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _signalTimer.Tick += (_, _) =>
            {
                var peak = _livePlay.ReadInputPeak();
                // sqrt maps the (mostly quiet) guitar range onto a readable bar.
                InputSignalLevel = Math.Sqrt(Math.Clamp(peak * 4, 0, 1));
                _signalSilentTicks = peak > 1e-6f ? 0 : _signalSilentTicks + 1;
                IsInputSignalMissing = _signalSilentTicks > 15; // ~3 s of dead silence
            };
            _signalTimer.Start();
        }
        else if (_signalTimer != null)
        {
            _signalTimer.Stop();
            _signalTimer = null;
            IsInputSignalMissing = false;
        }
    }

    /// <summary>Latest raw input peak for the menu backdrop's audio-reactive
    /// EQ bars (0.16 5.3) — the SAME non-destructive windowed capture tap the
    /// pre-play signal meter above polls (<c>LivePlayViewModel.ReadInputPeak</c>
    /// → <c>IAudioPlaybackService.ReadInputCaptureWindow</c>; no new native
    /// calls, safe alongside detection/tuner consumers). Polled by
    /// <c>MenuBackdropControl</c> at its own throttled tick, ONLY while the
    /// backdrop is live. 0 when Live Play (and thus input capture) is
    /// unavailable — the backdrop then keeps its idle animation.</summary>
    public float ReadBackdropInputPeak() => _livePlay?.ReadInputPeak() ?? 0f;

    // ---- Screen-reader announcement ------------------------------------

    private void UpdateAnnouncement(PlayModeScreen screen)
    {
        var from = _announcedFrom;
        _announcedFrom = screen;
        ScreenAnnouncement = screen switch
        {
            PlayModeScreen.Playing when from == PlayModeScreen.Paused => _localization["PlayMode_Announce_Resumed"],
            PlayModeScreen.Playing => string.Format(_localization["PlayMode_Announce_Playing"], FlowSongTitle),
            PlayModeScreen.Paused => _localization["PlayMode_Announce_Paused"],
            PlayModeScreen.Countdown => _localization["PlayMode_Announce_Countdown"],
            PlayModeScreen.Failed => _localization["PlayMode_Announce_Failed"],
            PlayModeScreen.Results => _localization["PlayMode_Announce_Finished"],
            _ => ""
        };
    }

    public bool IsStartScreen => CurrentScreen == PlayModeScreen.Start;
    public bool IsSongSelectScreen => CurrentScreen == PlayModeScreen.SongSelect;
    public bool IsTuningCheckScreen => CurrentScreen == PlayModeScreen.TuningCheck;
    public bool IsCountdownScreen => CurrentScreen == PlayModeScreen.Countdown;
    public bool IsPlayingScreen => CurrentScreen == PlayModeScreen.Playing;
    public bool IsPausedScreen => CurrentScreen == PlayModeScreen.Paused;
    public bool IsFailedScreen => CurrentScreen == PlayModeScreen.Failed;
    public bool IsResultsScreen => CurrentScreen == PlayModeScreen.Results;

    /// <summary>The ONE animated backdrop renders behind every Play Mode
    /// screen (0.16 visual pass) — except Paused, where the frozen-highway
    /// capture covers the surface anyway (hidden = the control's timer parks,
    /// so the pause menu costs nothing extra).</summary>
    public bool IsBackdropVisible =>
        CurrentScreen is not (PlayModeScreen.Paused or PlayModeScreen.Failed);

    /// <summary>Per-screen backdrop profile: the Start screen runs
    /// the full scene (glows, all particles, audio-reactive EQ bars); every
    /// content screen gets the dimmed Ambient treatment so lists and cards
    /// stay readable. Tuning lives in <see cref="MenuBackdropMath.ProfileFor" />.</summary>
    public MenuBackdropIntensity BackdropIntensity =>
        CurrentScreen is PlayModeScreen.Start
            ? MenuBackdropIntensity.Full
            : MenuBackdropIntensity.Ambient;

    // ---- Screen navigation (moved from the retired .Practice partial) ------

    [RelayCommand]
    private async Task OpenSongSelect()
    {
        CurrentScreen = PlayModeScreen.SongSelect;
        await SongSelect.EnsureLibrary();
    }

    [RelayCommand]
    private void Back() => NavigateBack();

    /// <summary>
    ///     One navigation level back (Esc). Song Select backs out to the shell's
    ///     Home. Returns false on the Start screen — there is nothing above it,
    ///     the caller decides what Esc means then.
    /// </summary>
    public bool NavigateBack()
    {
        switch (CurrentScreen)
        {
            case PlayModeScreen.SongSelect:
                HomeRequested?.Invoke();
                return true;
            case PlayModeScreen.TuningCheck:
                CurrentScreen = PlayModeScreen.SongSelect;
                return true;
            case PlayModeScreen.Countdown:
                _countdownTimer.Stop();
                _pendingRunStartOverrideMs = null;
                // A countdown reached from Retry / Restart / retry-from-section
                // sits INSIDE a run chain: the previous run's practice speed,
                // scroll speed, drop-tuner shift, backing mutes and in-game rig
                // are all still applied, and abandoning it here must unwind them rather than walk out
                // to Song Select carrying every one of them.
                EndRunSideEffects();
                CurrentScreen = PlayModeScreen.SongSelect;
                // Restart-from-pause counts down with the chrome hidden;
                // aborting must bring it back.
                ShowPlayModeSurfaceRequested?.Invoke();
                return true;
            case PlayModeScreen.Results:
                // The results screen is reached with the run's side effects
                // STILL APPLIED — the screen's own "Song select" button unwinds
                // them, and Esc is the same exit. It must not be a bare screen
                // assignment, or leaving by keyboard would take the run's practice
                // speed, per-playthrough highway scroll speed, drop-tuner shift
                // and backing-track mutes back into the editor with it (and
                // skip the stats refresh, so the row would still show the old
                // best).
                BackToSongSelect();
                return true;
            case PlayModeScreen.Paused:
                ResumeGameplay();
                return true;
            case PlayModeScreen.Failed:
                // The run is already over — Esc is the quit path.
                AbortRun(PlayModeScreen.SongSelect);
                return true;
            default:
                return false;
        }
    }
}
