// ChartMaintenanceService: the tempo map - beat insert/remove, measures at start/end, grid alignment, the beat-map lock and the default start silence.
using System.Globalization;
using DotCharter.Services.Editing.Tempo;
using DotCharter.Services.State;
namespace DotCharter.Services.Editing.Maintenance;

// Tempo-map / timeline surface: each verb clones the project, edits the AFTER copy and
// records one ProjectSnapshotAction. The grid arithmetic is TempoMapBeats; moving the
// chart's positions with the grid is ChartProjectPositions; Add silence is ProjectSilencePadder.
public sealed partial class ChartMaintenanceService
{
    public void SetDefaultStartSilence(double seconds)
    {
        var config = _configService.LoadAppConfig();
        var sanitized = Math.Max(0, seconds);
        config.PadSilenceOnNewProject = true;
        config.LeadingSilenceSeconds = sanitized;
        _configService.SaveAppConfig(config);
        _projectState.MainDisplayMessage = L("Status_DefaultStartSilenceUpdated", sanitized);
        _logger.Info(AppLogCategory.Settings,
            $"Updated default start silence to {sanitized:0.###} seconds.");
    }

    /// <summary>
    ///     Flips the beat-map lock, returning the new state.
    ///     <para>
    ///         It lives here rather than in the menu view-model because the flag
    ///         is PERSISTED — <c>RscpWriter</c> emits <c>dcBeatMapLocked</c> and
    ///         <c>RscpHeaderParser</c> reads it back — so toggling it changes the
    ///         project relative to the file on disk and has to say so. It cannot
    ///         say so with a history node: the menu checkmark reads the flag
    ///         directly and an undo would leave it stale, so this is precisely the
    ///         <c>IProjectSessionService.MarkProjectDirty</c> case, "a
    ///         change the history cannot represent". Recording nothing meant
    ///         locking a chart and closing it raised no unsaved-changes prompt and
    ///         dropped the lock, leaving the next session's tempo verbs free to
    ///         move a grid the charter had deliberately pinned.
    ///     </para>
    /// </summary>
    public bool ToggleBeatMapLock()
    {
        var project = _projectState.ActiveProject;
        if (project == null)
        {
            _projectState.MainDisplayMessage = L("Status_NoChartLoaded");
            return false;
        }

        project.IsBeatMapLocked = !project.IsBeatMapLocked;
        _projectSession.MarkProjectDirty();
        _projectState.SetMessage(L(project.IsBeatMapLocked ? "Status_BeatMapLocked" : "Status_BeatMapUnlocked"));
        _logger.Info(AppLogCategory.Events, $"Beat map lock toggled. IsLocked={project.IsBeatMapLocked}.");
        return project.IsBeatMapLocked;
    }

    public Task AddSilenceAtBeginningAsync(double seconds) => _silencePadder.AddSilenceAsync(seconds, atStart: true);

    public Task AddSilenceAtEndAsync(double seconds) => _silencePadder.AddSilenceAsync(seconds, atStart: false);

    /// <summary>
    ///     Prepends whole measures before the first beat.
    ///     <para>
    ///         The unit is MEASURES, matching <see cref="AddMeasuresAtEnd" /> —
    ///         and it is not only for symmetry. This verb shifts the ENTIRE chart
    ///         along the grid, so a count that is not a whole number of bars
    ///         moves every downbeat in the song onto an off-beat. The old
    ///         beat-counted form defaulted to 4 for exactly that reason.
    ///     </para>
    /// </summary>
    public void AddMeasuresAtStart(int measureCount)
    {
        var project = _projectState.ActiveProject;
        if (project == null)
        {
            _projectState.MainDisplayMessage = L("Status_NoChartLoaded");
            return;
        }

        if (project.IsBeatMapLocked)
        {
            _projectState.MainDisplayMessage = L("Status_BeatMapLocked");
            return;
        }

        if (measureCount <= 0 || project.Beats.Count == 0)
        {
            _projectState.MainDisplayMessage = L("Status_TempoMapOperationNoChange");
            return;
        }

        // Beats before 0:00 sit before the audio, and Rocksmith's beat list cannot
        // hold them. The owner's rule: add only the whole
        // measures that fit, and say so; add nothing when none fit.
        var requested = measureCount;
        measureCount = Math.Min(measureCount, TempoMapBeats.MeasuresThatFitBeforeFirstBeat(project));
        if (measureCount <= 0)
        {
            _projectState.SetMessage(L("Status_NoRoomBeforeFirstBeat",
                (project.Beats[0].Time / 1000.0).ToString("0.00", CultureInfo.CurrentCulture)), StatusSeverity.Warning);
            return;
        }

        var beatCount = measureCount * Math.Max(1, project.Beats[0].BeatsInMeasure);

        try
        {
            var arrangementIndex = GetActiveArrangementIndex();
            var levelIndex = GetActiveLevelIndex();
            var vocalTrackIndex = GetActiveVocalTrackIndex();
            var before = CloneProject(project);
            var after = CloneProject(project);

            TempoMapBeats.PrependBeats(after, beatCount);
            ChartProjectPositions.ShiftAll(after, new FractionalPosition(beatCount));

            _historyService.Do(new ProjectSnapshotAction(this, before, after, arrangementIndex, levelIndex,
                vocalTrackIndex));
            if (measureCount < requested)
            {
                _projectState.SetMessage(L("Status_MeasuresAddedAtStartClamped", measureCount, requested, beatCount),
                    StatusSeverity.Warning);
            }
            else
            {
                _projectState.MainDisplayMessage = _localization.Plural("Status_MeasuresAddedAtStartOne",
                    "Status_MeasuresAddedAtStart", measureCount, beatCount);
            }
            _logger.Info(AppLogCategory.Editing,
                $"Added {measureCount} measure(s) ({beatCount} beat(s)) at the start of the timeline.");
        }
        catch (Exception ex)
        {
            _logger.Error(AppLogCategory.Editing, "Add measures at start failed.", ex);
            _projectState.SetMessage(L("Status_AddBeatsFailed", ex.Message), StatusSeverity.Error);
        }
    }

    /// <summary>
    ///     Appends whole measures to the END of the beat grid, continuing the
    ///     last beat's tempo and time signature.
    ///     <para>
    ///         The reason this verb exists: RS-XML's LAST phrase is the END
    ///         marker and may not contain notes, so a chart whose final measure
    ///         still plays has nowhere to put it. Every fix by hand — drag the
    ///         last beat, insert-at-playhead four times — moves content that was
    ///         already correct.
    ///     </para>
    ///     <para>
    ///         Nothing follows the last beat, so unlike
    ///         <see cref="AddMeasuresAtStart" /> and <see cref="InsertBeatAtPlayhead" />
    ///         this shifts NO positions: appending is the one tempo-map edit that
    ///         cannot move a note. It still goes through
    ///         <c>ProjectSnapshotAction</c> like every sibling here, so one undo
    ///         takes it back.
    ///     </para>
    /// </summary>
    public void AddMeasuresAtEnd(int measureCount)
    {
        var project = _projectState.ActiveProject;
        if (project == null)
        {
            _projectState.MainDisplayMessage = L("Status_NoChartLoaded");
            return;
        }

        if (project.IsBeatMapLocked)
        {
            _projectState.MainDisplayMessage = L("Status_BeatMapLocked");
            return;
        }

        if (measureCount <= 0 || project.Beats.Count == 0)
        {
            _projectState.MainDisplayMessage = L("Status_TempoMapOperationNoChange");
            return;
        }

        try
        {
            var arrangementIndex = GetActiveArrangementIndex();
            var levelIndex = GetActiveLevelIndex();
            var vocalTrackIndex = GetActiveVocalTrackIndex();
            var before = CloneProject(project);
            var after = CloneProject(project);

            var added = TempoMapBeats.AppendMeasures(after, measureCount);

            _historyService.Do(new ProjectSnapshotAction(this, before, after, arrangementIndex, levelIndex,
                vocalTrackIndex));

            // The grid may now run past the audio, which is legal and often
            // exactly what is wanted (an END phrase in the fade-out) — but it is
            // never a surprise the charter should have to discover.
            var endMs = after.Beats[^1].Time;
            var audioMs = _projectState.TotalAudioLengthMs;
            _projectState.MainDisplayMessage = audioMs > 0 && endMs > audioMs
                ? _localization.Plural("Status_MeasuresAddedAtEndPastAudioOne", "Status_MeasuresAddedAtEndPastAudio",
                    measureCount, added)
                : _localization.Plural("Status_MeasuresAddedAtEndOne", "Status_MeasuresAddedAtEnd", measureCount, added);
            _logger.Info(AppLogCategory.Editing,
                $"Added {measureCount} measure(s) ({added} beat(s)) at the end of the timeline. " +
                $"Grid now ends at {endMs:0}ms, audio at {audioMs:0}ms.");
        }
        catch (Exception ex)
        {
            _logger.Error(AppLogCategory.Editing, "Add measures at end failed.", ex);
            _projectState.SetMessage(L("Status_AddBeatsFailed", ex.Message), StatusSeverity.Error);
        }
    }

    /// <summary>
    /// Tempo map ▸ Align beat grid to notes. The grid moves; nothing that
    ///     is timed does — <see cref="BeatGridShift" /> re-places every element at
    ///     its unchanged time on the shifted grid. One <c>ProjectSnapshotAction</c>,
    ///     like every sibling here, so one undo puts grid and positions back.
    /// </summary>
    public bool AlignBeatGridToNotes(double offsetMs)
    {
        var project = _projectState.ActiveProject;
        if (project == null)
        {
            _projectState.MainDisplayMessage = L("Status_NoChartLoaded");
            return false;
        }

        if (project.IsBeatMapLocked)
        {
            _projectState.MainDisplayMessage = L("Status_BeatMapLocked");
            _logger.Info(AppLogCategory.Editing, "Align beat grid refused: the beat map is locked.");
            return false;
        }

        try
        {
            var arrangementIndex = GetActiveArrangementIndex();
            var levelIndex = GetActiveLevelIndex();
            var vocalTrackIndex = GetActiveVocalTrackIndex();
            var before = CloneProject(project);
            var after = CloneProject(project);

            var result = BeatGridShift.Apply(after, offsetMs);
            if (!result.Applied)
            {
                _projectState.MainDisplayMessage = L("Status_TempoMapOperationNoChange");
                return false;
            }

            _historyService.Do(new ProjectSnapshotAction(this, before, after, arrangementIndex, levelIndex,
                vocalTrackIndex));
            _projectState.MainDisplayMessage = L("Status_BeatGridAligned",
                offsetMs.ToString("+0.0;-0.0;0.0", CultureInfo.CurrentCulture));
            _logger.Info(AppLogCategory.Editing,
                $"Aligned the beat grid to the notes: moved {offsetMs:0.0} ms, beats added at start " +
                $"{result.BeatsAddedAtStart}, at end {result.BeatsAddedAtEnd}, dropped before 0:00 {result.BeatsDropped}.");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(AppLogCategory.Editing, "Align beat grid failed.", ex);
            _projectState.SetMessage(L("Status_AlignBeatGridFailed", ex.Message), StatusSeverity.Error);
            return false;
        }
    }

    /// <summary>
    /// Inserts a new beat at the playhead. Mirrors RsCharter's <c>BeatsService.addBeat()</c>:
    /// shifts every chart position with <c>BeatId &gt;= pivot</c> by +1, shifts beats[pivot..]
    /// time forward by one beat interval, then inserts a duplicated beat at <c>pivot</c>.
    /// </summary>
    public void InsertBeatAtPlayhead()
    {
        var project = _projectState.ActiveProject;
        if (project == null)
        {
            _projectState.MainDisplayMessage = L("Status_NoChartLoaded");
            return;
        }
        if (project.IsBeatMapLocked)
        {
            _projectState.MainDisplayMessage = L("Status_BeatMapLocked");
            return;
        }
        if (project.Beats.Count == 0)
        {
            _projectState.MainDisplayMessage = L("Status_TempoMapOperationNoChange");
            return;
        }

        var pivot = TempoMapBeats.NearestBeatIndex(project.Beats, _projectState.CurrentTimeMs);
        // Clamp so we always have a valid surrounding beat to derive the
        // interval from (we use the gap between pivot and pivot+1, or pivot-1
        // and pivot if we're at the end).
        if (pivot < 0) pivot = 0;
        if (pivot >= project.Beats.Count) pivot = project.Beats.Count - 1;

        try
        {
            var arrangementIndex = GetActiveArrangementIndex();
            var levelIndex = GetActiveLevelIndex();
            var vocalTrackIndex = GetActiveVocalTrackIndex();
            var before = CloneProject(project);
            var after = CloneProject(project);

            ChartProjectPositions.ShiftFromBeat(after, pivot, +1);
            TempoMapBeats.InsertBeatAt(after, pivot);

            _historyService.Do(new ProjectSnapshotAction(this, before, after, arrangementIndex, levelIndex,
                vocalTrackIndex));
            _projectState.MainDisplayMessage = L("Status_BeatInserted", pivot);
            _logger.Info(AppLogCategory.Editing, $"Inserted new beat at index {pivot}.");
        }
        catch (Exception ex)
        {
            _logger.Error(AppLogCategory.Editing, "Insert beat at playhead failed.", ex);
            _projectState.SetMessage(L("Status_InsertBeatFailed", ex.Message), StatusSeverity.Error);
        }
    }

    /// <summary>
    /// Removes the beat closest to the playhead. Mirrors RsCharter's
    /// <c>BeatsService.removeBeat()</c>: deletes any chart content at <c>BeatId == pivot</c>,
    /// shifts remaining content with <c>BeatId &gt; pivot</c> back by -1, shifts beats[pivot+1..]
    /// time backward, and removes the beat marker.
    /// </summary>
    public void RemoveBeatAtPlayhead()
    {
        var project = _projectState.ActiveProject;
        if (project == null)
        {
            _projectState.MainDisplayMessage = L("Status_NoChartLoaded");
            return;
        }
        if (project.IsBeatMapLocked)
        {
            _projectState.MainDisplayMessage = L("Status_BeatMapLocked");
            return;
        }
        if (project.Beats.Count <= 1)
        {
            _projectState.MainDisplayMessage = L("Status_TempoMapOperationNoChange");
            return;
        }

        var pivot = TempoMapBeats.NearestBeatIndex(project.Beats, _projectState.CurrentTimeMs);
        if (pivot < 0) pivot = 0;
        if (pivot >= project.Beats.Count) pivot = project.Beats.Count - 1;

        try
        {
            var arrangementIndex = GetActiveArrangementIndex();
            var levelIndex = GetActiveLevelIndex();
            var vocalTrackIndex = GetActiveVocalTrackIndex();
            var before = CloneProject(project);
            var after = CloneProject(project);

            TempoMapBeats.RemoveContentAtBeat(after, pivot);
            ChartProjectPositions.ShiftFromBeat(after, pivot + 1, -1);
            TempoMapBeats.RemoveBeatAt(after, pivot);

            _historyService.Do(new ProjectSnapshotAction(this, before, after, arrangementIndex, levelIndex,
                vocalTrackIndex));
            _projectState.MainDisplayMessage = L("Status_BeatRemoved", pivot);
            _logger.Info(AppLogCategory.Editing, $"Removed beat at index {pivot}.");
        }
        catch (Exception ex)
        {
            _logger.Error(AppLogCategory.Editing, "Remove beat at playhead failed.", ex);
            _projectState.SetMessage(L("Status_RemoveBeatFailed", ex.Message), StatusSeverity.Error);
        }
    }
}
