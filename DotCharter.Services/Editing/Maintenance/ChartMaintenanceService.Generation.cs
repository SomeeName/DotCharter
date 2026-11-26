// ChartMaintenanceService: whole-chart generation and repair - auto-fix, improve, the FHP forwards, hand shapes, level squash, arrangement/vocal delete and the single-undo-step runner.
using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Fixing;
using DotCharter.Services.Editing.HandShapes;
using DotCharter.Services.Editing.Improvement;
using DotCharter.Services.State;

namespace DotCharter.Services.Editing.Maintenance;

// Whole-chart generation and repair surface: FHP anchor generation,
// the auto-fix pipeline (Improve chart runs through FixChart), and level squashing.
public sealed partial class ChartMaintenanceService
{
    /// <summary>The Errors group of <see cref="FixChart" />: the Log Charter-parity auto-fix pipeline over every arrangement, one undo step.</summary>
    public void AutoFixChart() => FixChart(FixChartGroups.Errors);

    /// <summary>
    ///     What <see cref="FixChart" /> would change: the two operations run on a snapshot of the chart and the
    ///     counts are read off their results. The copy is taken on the calling thread, the work on a worker.
    /// </summary>
    public Task<FixChartPreview> PreviewFixChartAsync(FixChartGroups groups)
    {
        if (_projectState.ActiveProject is not { } project) return Task.FromResult(FixChartPreview.Empty);
        var copy = CloneProject(project);
        var config = _configService.LoadAppConfig();
        var audioLengthMs = _projectState.TotalAudioLengthMs;
        var runner = new FixChartRunner(_autoFixService, _chartImprovementService);
        return Task.Run(() => runner.Run(copy, groups, config, audioLengthMs));
    }

    /// <summary>Runs the checked groups, Errors then Polish, on the live chart as ONE undo step.</summary>
    public void FixChart(FixChartGroups groups)
    {
        if (_projectState.ActiveProject is not { } project)
        {
            _projectState.MainDisplayMessage = L("Status_NoChartLoaded");
            return;
        }

        if (groups == FixChartGroups.None) return;
        try
        {
            var before = CloneProject(project);
            var after = CloneProject(project);
            var result = new FixChartRunner(_autoFixService, _chartImprovementService)
                .Run(after, groups, _configService.LoadAppConfig(), _projectState.TotalAudioLengthMs);
            if (result.Total > 0)
            {
                _historyService.Do(new ProjectSnapshotAction(this, before, after, GetActiveArrangementIndex(),
                    GetActiveLevelIndex(), GetActiveVocalTrackIndex()));
            }

            _projectState.MainDisplayMessage = result.ToStatusMessage(_localization, groups);
            _logger.Info(AppLogCategory.Editing, $"Fix chart {groups}: errors={result.ErrorCount}, polish={result.PolishCount}.");
        }
        catch (Exception ex)
        {
            _logger.Error(AppLogCategory.Editing, "Fix chart failed.", ex);
            _projectState.SetMessage(L("Status_FixChartFailed", ex.Message), StatusSeverity.Error);
        }
    }

    /// <summary>
    ///     Clears the chord name from every two-string shape in the project, as one
    ///     undoable snapshot.
    ///     <para>
    ///         The suppression setting only governs what the automatic namer mints
    ///         NEXT; a chart imported from a psarc carries the original charter's
    ///         names, and a chart authored before the setting existed carries its
    ///         own. This is how those get cleaned up, and it is why the setting
    ///         ships with a tool rather than on its own.
    ///     </para>
    /// </summary>
    public void RemoveDoubleStopChordNames()
    {
        var project = _projectState.ActiveProject;
        if (project == null)
        {
            _projectState.MainDisplayMessage = L("Status_NoChartLoaded");
            return;
        }

        try
        {
            var config = _configService.LoadAppConfig();
            // The tool is an explicit request, so it runs even when the automatic
            // suppression is switched off -- the user just asked for it. Only the
            // power-chord exemption is read from the settings, because that says
            // WHICH shapes count, not whether to act.
            var options = new DoubleStopNameOptions(
                Suppress: true, KeepPowerChordNames: config.KeepPowerChordNames);

            var arrangementIndex = GetActiveArrangementIndex();
            var levelIndex = GetActiveLevelIndex();
            var vocalTrackIndex = GetActiveVocalTrackIndex();
            var before = CloneProject(project);
            var after = CloneProject(project);

            var cleared = DoubleStopChordNames.Strip(after, options);
            if (cleared > 0)
            {
                _historyService.Do(new ProjectSnapshotAction(this, before, after, arrangementIndex, levelIndex,
                    vocalTrackIndex));
            }

            _projectState.MainDisplayMessage = cleared > 0
                ? _localization.Plural("Status_DoubleStopNamesClearedOne", "Status_DoubleStopNamesCleared", cleared)
                : L("Status_DoubleStopNamesNone");
            _logger.Info(AppLogCategory.Editing,
                $"Double-stop chord names: cleared {cleared} template name(s) " +
                $"(power chords {(options.KeepPowerChordNames ? "kept" : "included")}).");
        }
        catch (Exception ex)
        {
            _logger.Error(AppLogCategory.Editing, "Removing double-stop chord names failed.", ex);
            _projectState.SetMessage(L("Status_DoubleStopNamesFailed", ex.Message), StatusSeverity.Error);
        }
    }

    public void GenerateFhp() => _fhpGeneration.GenerateFhp();

    public void GenerateFhpForSelection() => _fhpGeneration.GenerateFhpForSelection();

    public void SquashLevels()
    {
        var project = _projectState.ActiveProject;
        var arrangement = _projectState.ActiveArrangement;
        if (project == null || arrangement == null)
        {
            _projectState.MainDisplayMessage = L("Status_NoArrangementSelected");
            return;
        }

        if (arrangement.Levels.Count <= 1)
        {
            _projectState.MainDisplayMessage = L("Status_SquashLevelsNoChange");
            return;
        }

        try
        {
            var arrangementIndex = GetActiveArrangementIndex();
            var levelIndex = 0;
            var vocalTrackIndex = GetActiveVocalTrackIndex();
            var live = LiveArrangementAt(project, arrangementIndex);
            if (live == null)
            {
                _projectState.MainDisplayMessage = L("Status_SquashLevelsNoChange");
                return;
            }

            // One arrangement changes, so the step snapshots one.
            var before = ChartProjectCloner.CloneArrangement(live, retainIdentities: true);
            var targetArrangement = ChartProjectCloner.CloneArrangement(live, retainIdentities: true);
            var templates = LevelTemplateOrphans.Capture(targetArrangement);
            if (!_levelSquashService.TrySquash(targetArrangement))
            {
                _projectState.MainDisplayMessage = L("Status_SquashLevelsNoChange");
                return;
            }

            // The reduced templates the dropped levels played go in the same node.
            _historyService.Do(LevelOperationAction(live, arrangementIndex, before, targetArrangement, templates,
                levelIndex, vocalTrackIndex));
            _projectState.SetMessage(L("Status_SquashLevelsApplied", targetArrangement.Name), StatusSeverity.Success);
            _logger.Info(AppLogCategory.Editing,
                $"Squashed levels for arrangement '{targetArrangement.Name}'. PreviousLevelCount={arrangement.Levels.Count}, NewLevelCount=1.");
        }
        catch (Exception ex)
        {
            _logger.Error(AppLogCategory.Editing, "Squash levels failed.", ex);
            _projectState.SetMessage(L("Status_SquashLevelsFailed", ex.Message), StatusSeverity.Error);
        }
    }

    /// <inheritdoc />
    public bool RunAsSingleUndoStep(Action mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        var project = _projectState.ActiveProject;
        if (project == null)
        {
            _projectState.MainDisplayMessage = L("Status_NoChartLoaded");
            return false;
        }

        var arrangementIndex = GetActiveArrangementIndex();
        var levelIndex = GetActiveLevelIndex();
        var vocalTrackIndex = GetActiveVocalTrackIndex();
        var before = CloneProject(project);

        var viewState = new MutationViewState(_projectState, _chartEditor);

        // Roll back partial imports before propagating the caller's failure.
        try
        {
            mutate();
        }
        catch
        {
            _projectState.ActiveProject = project;
            RestoreProjectSnapshot(before, arrangementIndex, levelIndex, vocalTrackIndex);
            viewState.Restore();
            throw;
        }

        var after = CloneProject(_projectState.ActiveProject ?? project);

        // Execute restores `after`, which the live project already equals.
        _historyService.Do(new ProjectSnapshotAction(
            this, before, after, arrangementIndex, levelIndex, vocalTrackIndex));
        return true;
    }

    /// <summary>
    ///     Deletes the active arrangement as ONE undo node.
    ///     <para>
    ///         <b>This must stay recoverable.</b> Removing the
    ///         arrangement straight out of the collection with no
    ///         <see cref="IHistoryService" /> involvement would mean Ctrl+Z could not
    ///         bring it back and hours of charting would go with it — the single most
    ///         destructive thing in the app, reachable from one menu click. The
    ///         same holds for the vocal track.
    ///     </para>
    ///     <para>
    ///         It goes through the project SNAPSHOT rather than a
    ///         remove-and-reinsert action because an arrangement is not just a row
    ///         in a list: undoing has to restore its levels, templates, phrases and
    ///         the selection state that pointed into it, which is exactly what
    ///         <c>ChartProjectCloner</c> already captures for save and recovery.
    ///     </para>
    /// </summary>
    public void DeleteActiveArrangement()
    {
        var project = _projectState.ActiveProject;
        if (project == null)
        {
            _projectState.MainDisplayMessage = L("Status_NoChartLoaded");
            return;
        }

        // A chart with no arrangement at all is not a state the editor can
        // represent, so the last one is kept.
        if (project.Arrangements.Count <= 1)
        {
            _logger.Warning(AppLogCategory.Project,
                "Delete arrangement requested but the project only has one arrangement.");
            _projectState.MainDisplayMessage = L("Status_CannotDeleteLastArrangement");
            return;
        }

        try
        {
            var arrangementIndex = GetActiveArrangementIndex();
            if (arrangementIndex < 0) return;

            var name = project.Arrangements[arrangementIndex].Name;
            var before = CloneProject(project);
            var after = CloneProject(project);
            after.Arrangements.RemoveAt(arrangementIndex);

            // Select a surviving arrangement: the one that shuffled into this
            // slot, or the last if the tail was deleted.
            var survivor = Math.Min(arrangementIndex, after.Arrangements.Count - 1);

            _historyService.Do(new ProjectSnapshotAction(
                this, before, after, survivor, 0, GetActiveVocalTrackIndex()));

            _projectState.MainDisplayMessage = L("Status_ArrangementDeleted", name);
            _logger.Info(AppLogCategory.Project,
                $"Deleted arrangement '{name}'. Remaining={after.Arrangements.Count}. Undoable.");
        }
        catch (Exception ex)
        {
            _logger.Error(AppLogCategory.Project, "Delete arrangement failed.", ex);
            _projectState.SetMessage(L("Status_ArrangementDeleteFailed", ex.Message), StatusSeverity.Error);
        }
    }

    /// <summary>
    ///     Deletes the active vocal track as ONE undo node. Same rationale as
    ///     <see cref="DeleteActiveArrangement" /> — and this is the one the owner
    ///     actually lost lyrics to.
    ///     <para>
    ///         Unlike an arrangement, a project with NO vocal track is a legitimate
    ///         state (most charts have none), so there is no last-one guard.
    ///     </para>
    /// </summary>
    public void DeleteActiveVocalTrack()
    {
        var project = _projectState.ActiveProject;
        if (project == null)
        {
            _projectState.MainDisplayMessage = L("Status_NoChartLoaded");
            return;
        }

        try
        {
            var vocalTrackIndex = GetActiveVocalTrackIndex();
            if (vocalTrackIndex < 0 || vocalTrackIndex >= project.VocalTracks.Count) return;

            var name = project.VocalTracks[vocalTrackIndex].Name;
            var before = CloneProject(project);
            var after = CloneProject(project);
            after.VocalTracks.RemoveAt(vocalTrackIndex);

            var survivor = Math.Min(vocalTrackIndex, after.VocalTracks.Count - 1);

            _historyService.Do(new ProjectSnapshotAction(
                this, before, after, GetActiveArrangementIndex(), GetActiveLevelIndex(), survivor));

            _projectState.MainDisplayMessage = L("Status_VocalTrackDeleted", name);
            _logger.Info(AppLogCategory.Project,
                $"Deleted vocal track '{name}'. Remaining={after.VocalTracks.Count}. Undoable.");
        }
        catch (Exception ex)
        {
            _logger.Error(AppLogCategory.Project, "Delete vocal track failed.", ex);
            _projectState.SetMessage(L("Status_VocalTrackDeleteFailed", ex.Message), StatusSeverity.Error);
        }
    }

    /// <summary>
    /// Tools ▸ Hand shapes ▸ Generate hand shapes (arrangement): covers every
    ///     chord no hand shape covers yet, on EVERY difficulty level of the active
    ///     arrangement, with the import path's grouping (<see cref="ChordHandShapePlanner" />).
    ///     It never touches a bar that exists, and the bars, the templates they mint and
    ///     the sentinel chords' repoints are ONE undo step.
    /// </summary>
    public void GenerateHandShapesForArrangement()
    {
        var arrangement = _projectState.ActiveArrangement;
        if (arrangement == null)
        {
            _projectState.MainDisplayMessage = L("Status_NoArrangementSelected");
            return;
        }

        var plan = ArrangementHandShapeGenerator.Plan(arrangement,
            ArrangementFingeringStyle.OfSong(_projectState.ActiveProject, arrangement));
        if (plan.IsEmpty)
        {
            _projectState.MainDisplayMessage = L("Status_HandShapesAllChordsCovered");
            return;
        }

        var actions = ArrangementHandShapeGenerator.BuildActions(arrangement, plan);
        if (!_historyService.Do(new CompositeAction(actions) { LabelKey = "History_GenerateHandShapes", LinkPolicyOverride = LinkPolicy.ShareFirstChanged })) return;

        _chartEditor.RequestRedraw();
        _projectState.MainDisplayMessage = _localization.Plural(
            "Status_HandShapesGeneratedOne", "Status_HandShapesGenerated", plan.HandShapeCount, plan.ChordCount);
        _logger.Info(AppLogCategory.Editing,
            $"Generated hand shapes for arrangement '{arrangement.Name}'. Levels={plan.Levels.Count}, " +
            $"shapes={plan.HandShapeCount}, chords covered={plan.ChordCount}, templates minted={plan.MintedTemplates.Count}.");
    }
}
