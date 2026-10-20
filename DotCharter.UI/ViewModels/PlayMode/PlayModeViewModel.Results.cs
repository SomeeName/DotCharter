// PlayModeViewModel: entering the Results screen and the exit verbs that undo a run's side effects; the board itself is RunResultsViewModel.
using CommunityToolkit.Mvvm.Input;
using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.UI.ViewModels.PlayMode;

/// <summary>
///     The results screen's half that touches the run: entering it when a
///     session this flow started completes, and the exit verbs that undo the
///     run's side effects. The board it shows (score, bests, sections, timing,
///     leaderboard) is <see cref="RunResultsViewModel" />, filled from the report.
///     <para>
///         Part of <see cref="PlayModeViewModel" />. Injected dependencies and
///         plain private fields live in the ROOT file; see
///         <c>PlayModeViewModel.cs</c> for the full field rule and
///         why the split exists.
///     </para>
/// </summary>
public sealed partial class PlayModeViewModel
{
    private void OnSessionCompleted(LivePlaySessionReport report)
    {
        // Only handle sessions this flow started; ordinary editor practice
        // sessions end here too and must not hijack the UI.
        if (CurrentScreen != PlayModeScreen.Playing) return;

        _endWatchTimer.Stop();
        // A normal song-end doesn't route through EndRunSideEffects, so restore
        // the manual rig here too (idempotent).
        _rsToneCoordinator?.EndRun();
        // Same: kill this run's count-in. "End run from pause" during a count-in
        // lands here with CurrentScreen forced back to Playing, which is exactly
        // the state a screen-only guard would mistake for "still counting in".
        CancelCountIn();
        _lastReport = report;

        RunResults.Show(report);

        _flowSong?.ApplyStats(_stats.GetBestForSong(report.SongKey));

        CurrentScreen = PlayModeScreen.Results;
        ShowPlayModeSurfaceRequested?.Invoke();
    }

    // ---- Results: leaving the run ---------------------------------------------

    [RelayCommand]
    private void Retry()
    {
        if (_flowSong == null) return;
        BeginCountdown();
    }

    [RelayCommand]
    private void BackToSongSelect()
    {
        _transport?.StopCommand.Execute(null);
        EndRunSideEffects();
        CurrentScreen = PlayModeScreen.SongSelect;
        SongSelect.RefreshSongStats();
    }

    /// <summary>Undo per-run audio routing (drop tuner, tuning-match song shift, speed,
    /// backing-track mutes).</summary>
    private void EndRunSideEffects()
    {
        // The run chain ends here: the next BeginGameplay captures fresh
        // pre-run baselines (see _runSideEffectsApplied).
        var hadRunSideEffects = _runSideEffectsApplied;
        _runSideEffectsApplied = false;
        // Whether the CHAIN ever applied a drop-tuner shift, read before it is
        // cleared — the restore below is keyed on this and not on DropTuner.IsOn.
        var appliedPitchShift = _runAppliedPitchShift;
        _runAppliedPitchShift = false;
        // …and the same for the tuning-match song shift. Judging goes back to the
        // chart's own pitches whatever happens below: the editor's Live Play never shifts.
        var appliedSongTranspose = _runAppliedSongTranspose;
        _runAppliedSongTranspose = false;
        _livePlay?.TransposeSemitones = 0;
        // A count-in still ticking belongs to the run that is ending — kill it, or
        // it resumes for the next one (CurrentScreen alone cannot tell them apart).
        CancelCountIn();
        RestoreBackingTrackMutes();
        // Restore the player's manual rig if in-game tones drove this run
        // (idempotent — safe on every exit path).
        _rsToneCoordinator?.EndRun();
        // Ahead of the transport guard on purpose: the per-run scroll speed is
        // ProjectState, not transport state, and leaving it applied would silently
        // turn "just for this playthrough" into a permanent setting for the editor
        // preview and every later song.
        if (hadRunSideEffects) _projectState.HighwayScrollSpeed = _preRunScrollSpeed;

        if (_transport == null || !hadRunSideEffects) return;
        // Keyed on what the RUN applied, not on DropTuner.IsOn. That is a
        // pre-play control that PreparePrePlay reloads on every song
        // pick, so any exit that happens after the player has moved on — and a
        // REFUSED run lands them straight back on the pre-play screen — left the
        // input permanently pitch-shifted with nothing on screen saying so.
        if (appliedPitchShift) _transport.InputPitchShiftSemis = _preRunPitchShift;
        if (appliedSongTranspose) _transport.PitchShift = _preRunSongPitchShift;
        _transport.PlaybackSpeed = _preRunSpeed;
        _transport.GameplayMetronomeActive = false;
        _transport.GameplayClapsActive = false;
    }

    /// <summary>Editor bridge: drop into the editor at the first failed
    /// section of the last run (or the start when everything was clean).</summary>
    [RelayCommand]
    private void OpenInEditor()
    {
        EndRunSideEffects();
        var target = _lastReport?.WorstSection?.StartMs ?? 0;
        _transport?.SeekTo(target);
        ExitToEditorRequested?.Invoke();
    }

    /// <summary>Editor bridge from the section breakdown: open at a section.
    /// <para>
    /// It unwinds the run for the same reason <see cref="OpenInEditor" /> does,
    /// as every editor bridge must. The results screen
    /// is reached with every run side effect still applied — the practice
    /// speed, the per-playthrough highway scroll speed, the drop-tuner shift,
    /// the backing-track mutes and the in-game rig — so a "Practice" click on a
    /// section row would drop the player into the CHART EDITOR carrying all of it,
    /// and the scroll speed and pitch shift would then read as permanent settings.
    /// </para></summary>
    [RelayCommand]
    private void OpenSectionInEditor(SectionRow? row)
    {
        if (row == null) return;
        EndRunSideEffects();
        _transport?.SeekTo(row.StartMs);
        ExitToEditorRequested?.Invoke();
    }
}
