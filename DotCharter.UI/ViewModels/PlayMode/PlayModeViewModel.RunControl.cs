// PlayModeViewModel: the run's lifecycle verbs - pause, resume, riff repeater, restart, abort, end - the shell events they raise, and the rock-meter fail-out.

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DotCharter.UI.ViewModels.PlayMode;

/// <summary>
///     The verbs that CONTROL a run once it is going: pause and its backdrop
///     freeze-frame, resume, riff repeater, restart, the two aborts, ending from
///     the pause menu, and the rock-meter fail-out with its retry. The events the
///     shell listens to (exit to editor, open settings, surface swaps) live here
///     too, because every one of them is raised by one of those verbs.
///     <para>
///         Part of <see cref="PlayModeViewModel" />. Injected dependencies and
///         plain private fields live in the ROOT file; see
///         <c>PlayModeViewModel.cs</c> for the full field rule and
///         why the split exists.
///     </para>
/// </summary>
public sealed partial class PlayModeViewModel
{
    public string ActiveProfileName => _profiles.ActiveProfile.DisplayName;

    /// <summary>Raised when the user leaves Play Mode for the editor.</summary>
    public event Action? ExitToEditorRequested;

    /// <summary>Raised when Play Mode hands the player to the shell's Home: Start's
    ///     button, Esc on Song Select, and the pause menu's Home.</summary>
    public event Action? HomeRequested;

    /// <summary>Raised by the menu's Settings entry; the shell owns the tab.</summary>
    public event Action? OpenSettingsRequested;

    /// <summary>Shell should show the editor surface (gameplay runs there).</summary>
    public event Action? ShowEditorSurfaceRequested;

    /// <summary>Shell should show the Play Mode surface again (results).</summary>
    public event Action? ShowPlayModeSurfaceRequested;

    /// <summary>Start's button and its Enter key: on to Home.</summary>
    [RelayCommand]
    private void EnterHome() => HomeRequested?.Invoke();

    // ---- Pause menu (Rocksmith-style, Esc during a run) ----------------------

    /// <summary>Raised by Riff Repeater: the shell shows the practice/loop
    /// strip on the gameplay HUD for the rest of this run.</summary>
    public event Action? RiffRepeaterRequested;

    /// <summary>Pause variant of ShowPlayModeSurfaceRequested: the shell
    /// switches surfaces but KEEPS the gameplay chrome hidden, so the pause
    /// menu floats over the frozen-highway backdrop uninterrupted.</summary>
    public event Action? ShowPauseSurfaceRequested;

    /// <summary>Set by the window: captures the CURRENT window contents
    /// (including the native highway) right before pausing. Airspace makes a
    /// live overlay impossible; a still frame of a paused game is identical.</summary>
    public Func<Avalonia.Media.Imaging.Bitmap?>? PauseBackdropProvider { get; set; }

    [ObservableProperty] private Avalonia.Media.Imaging.Bitmap? _pauseBackdrop;

    private void ClearPauseBackdrop()
    {
        var old = PauseBackdrop;
        PauseBackdrop = null;
        old?.Dispose();
    }

    /// <summary>Esc during gameplay: pause the song and show the pause menu
    /// over a frozen frame of the highway.</summary>
    public void PauseGameplay()
    {
        if (CurrentScreen != PlayModeScreen.Playing) return;
        _transport?.PauseCommand.Execute(null);
        ClearPauseBackdrop();
        PauseBackdrop = PauseBackdropProvider?.Invoke();
        // After the transport stops, so the elapsed figure is the frame the
        // frozen backdrop above is showing.
        CapturePausedRun();
        CurrentScreen = PlayModeScreen.Paused;
        ShowPauseSurfaceRequested?.Invoke();
    }

    [RelayCommand]
    private void ResumeGameplay()
    {
        ClearPauseBackdrop();
        if (_livePlay is not { IsActive: true })
        {
            // No session to go back to — so this is an EXIT, and an exit unwinds
            // the run. See EndRunFromPause's else branch for what is otherwise
            // left applied.
            EndRunSideEffects();
            CurrentScreen = PlayModeScreen.SongSelect;
            ShowPlayModeSurfaceRequested?.Invoke();
            return;
        }

        CurrentScreen = PlayModeScreen.Playing;
        ShowEditorSurfaceRequested?.Invoke();

        // Rocksmith-style resume rewind: back up a lead-in so the player re-orients
        // before notes arrive. Everything already judged stays judged (the session
        // arms the seek as its OWN, so the matcher is never rewound) — the replayed
        // notes render dimmed and can neither score nor miss again.
        _livePlay.BeginResumeRewind(_configService.LoadAppConfig().GameplayResumeRewindMs);

        _transport?.PlayCommand.Execute(null);
        _endWatchTimer.Start();
    }

    /// <summary>Riff Repeater: back to the highway with the loop/practice
    /// strip visible; the song stays paused so a section can be picked.</summary>
    [RelayCommand]
    private void OpenRiffRepeater()
    {
        ClearPauseBackdrop();
        if (_livePlay is not { IsActive: true })
        {
            // No session to go back to — so this is an EXIT, and an exit unwinds
            // the run. See EndRunFromPause's else branch for what is otherwise
            // left applied.
            EndRunSideEffects();
            CurrentScreen = PlayModeScreen.SongSelect;
            ShowPlayModeSurfaceRequested?.Invoke();
            return;
        }

        // The Riff Repeater is practice: performance stakes come OFF for the
        // rest of this run (hard exclusion — a loop drill must not fail out).
        _livePlay.DisarmRockMeter();
        _livePlay.DisarmCrowdMeter(); // a drill has no crowd either

        RiffRepeaterRequested?.Invoke();
        CurrentScreen = PlayModeScreen.Playing;
        ShowEditorSurfaceRequested?.Invoke();
        _endWatchTimer.Start();
    }

    [RelayCommand]
    private void RestartRun()
    {
        // Screen is Paused here, so OnSessionCompleted ignores the stop —
        // no results flash before the countdown.
        ClearPauseBackdrop();
        _livePlay?.IsActive = false;
        _transport?.StopCommand.Execute(null);
        BeginCountdown();
    }

    /// <summary>Abandon the run (no results) and land on a Play Mode screen —
    /// the guaranteed mouse-only way back out of gameplay.</summary>
    private void AbortRun(PlayModeScreen target)
    {
        ClearPauseBackdrop();
        // Screen is Paused/Playing but NOT left on Playing, so
        // OnSessionCompleted ignores the stop — no results flash.
        CurrentScreen = target;
        if (_livePlay is { IsActive: true }) _livePlay.IsActive = false;
        _transport?.StopCommand.Execute(null);
        EndRunSideEffects();
        ShowPlayModeSurfaceRequested?.Invoke();
        if (target == PlayModeScreen.SongSelect) SongSelect.RefreshSongStats();
    }

    [RelayCommand]
    private void AbortToSongSelect() => AbortRun(PlayModeScreen.SongSelect);

    /// <summary>The pause menu's Home: the run is abandoned, then the shell shows Home.</summary>
    [RelayCommand]
    private void AbortToHome()
    {
        AbortRun(PlayModeScreen.SongSelect);
        HomeRequested?.Invoke();
    }

    /// <summary>End the run from the pause menu — straight to the results.</summary>
    [RelayCommand]
    private void EndRunFromPause()
    {
        ClearPauseBackdrop();
        if (_livePlay is { IsActive: true })
        {
            CurrentScreen = PlayModeScreen.Playing; // let OnSessionCompleted accept
            _livePlay.IsActive = false;
            _transport?.StopCommand.Execute(null);
        }
        else
        {
            // There is no session left to end, but the RUN's side effects are
            // still applied — the practice speed, the per-playthrough highway
            // scroll speed, the drop-tuner shift, the backing-track mutes, the
            // in-game rig and the phone dashboard's "still playing" row. This
            // branch is the sibling of AbortRun, which unwinds all of them; it
            // must not walk out to Song Select leaving every one in place, or a
            // pause menu that had lost its session would take the run's transport
            // settings back into the editor with it.
            _transport?.StopCommand.Execute(null);
            EndRunSideEffects();
            CurrentScreen = PlayModeScreen.SongSelect;
            ShowPlayModeSurfaceRequested?.Invoke();
        }
    }

    // ---- Rock-meter fail-out (0.24 performance stakes) -----------------------

    /// <summary>Start of the section the failed run died in — the retry
    /// target. Null on sectionless charts (retry degrades to a full restart).</summary>
    private double? _failedSectionStartMs;

    /// <summary>The rock meter hit zero (posted from the session tick): end
    /// the run exactly like a deliberate abort — screen first, so
    /// <see cref="OnSessionCompleted"/> ignores the stop and no results screen
    /// flashes — and show the fail overlay over a frozen frame of the highway
    /// (the pause menu's airspace pattern). The session still records, marked
    /// failed; bests only ever improve, so the invariant is untouched.</summary>
    private void OnRockMeterFailed()
    {
        if (CurrentScreen != PlayModeScreen.Playing) return;
        if (_livePlay is not { IsActive: true }) return;

        _endWatchTimer.Stop();

        // Freeze the moment of failure BEFORE any teardown (a live overlay
        // over the native highway is impossible; a still frame is not).
        ClearPauseBackdrop();
        PauseBackdrop = PauseBackdropProvider?.Invoke();

        var diedAtMs = _projectState.CurrentTimeMs;
        _failedSectionStartMs = _livePlay.Sections
            .FirstOrDefault(s => diedAtMs >= s.StartMs && diedAtMs < s.EndMs)?.StartMs;

        CurrentScreen = PlayModeScreen.Failed;
        _livePlay.MarkRunFailed(); // BEFORE deactivating — the record carries the mark
        _livePlay.IsActive = false;
        _transport?.StopCommand.Execute(null);
        EndRunSideEffects();
        ShowPauseSurfaceRequested?.Invoke();

        _logger.Info(AppLogCategory.Application,
            $"Play Mode run failed out at {diedAtMs:0} ms (retry section start: " +
            $"{(_failedSectionStartMs is { } s ? s.ToString("0") : "none")}).");
    }

    /// <summary>"Ab Abschnitt wiederholen": a NEW run through the ordinary
    /// run-start machinery (countdown → lead-in → count-in), seeked to the
    /// failed section's start instead of the chart start. Scoring restarts
    /// from zero — the best-score invariant is untouched.</summary>
    [RelayCommand]
    private void RetryFromFailedSection()
    {
        if (_flowSong == null) return;
        ClearPauseBackdrop();
        BeginCountdown(_failedSectionStartMs);
    }
}
