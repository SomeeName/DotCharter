// ChartMaintenanceService: "Apply DLC Builder fixes" and the export findings' Fix — each run
// is ONE project-snapshot undo node — and the undefined-tone-key repair.
using DotCharter.Services.Editing.Preparation;
using DotCharter.Services.Export;
using DotCharter.Services.Export.DynamicDifficulty;
using DotCharter.Services.Editing.Tones;
using DotCharter.Services.Export.Validation;
using DotCharter.Services.State;

namespace DotCharter.Services.Editing.Maintenance;

public sealed partial class ChartMaintenanceService
{
    private ExportFindingFixer Fixer() => new(_autoFixService, _fretAnchorGenerationService);

    /// <inheritdoc />
    public int ApplyDlcBuilderFixes(IReadOnlyList<Arrangement>? arrangements = null)
    {
        var project = _projectState.ActiveProject;
        if (project == null)
        {
            _projectState.MainDisplayMessage = L("Status_NoChartLoaded");
            return 0;
        }

        var wanted = arrangements ?? (_projectState.ActiveArrangement is { } active ? [active] : []);
        var indices = wanted.Select(a => project.Arrangements.IndexOf(a))
            .Where(i => i >= 0 && project.Arrangements[i].ArrangementType != ArrangementType.Vocal)
            .Distinct()
            .ToList();
        if (indices.Count == 0)
        {
            _projectState.MainDisplayMessage = L("Status_NoArrangementSelected");
            return 0;
        }

        var shownWasSingleLevel = _projectState.ActiveArrangement is { Levels.Count: 1 } current &&
                                  indices.Contains(project.Arrangements.IndexOf(current));
        try
        {
            // The same steps the export runs, whatever the export toggles say: this verb is asked for.
            var config = _configService.LoadAppConfig();
            var steps = new DlcBuilderSteps(true, true, DlcBuilderSteps.ParseMode(config.ExportDdLevelCountMode));
            var audioLength = _projectState.TotalAudioLengthMs > 0 ? _projectState.TotalAudioLengthMs : (double?)null;
            var changed = RunOnSnapshot(project, after =>
            {
                var count = 0;
                foreach (var i in indices)
                {
                    // The structure the export generates for a chart without one, as it does.
                    PhraseSectionGenerator.GenerateIfAbsent(after.Arrangements[i], after.Beats, audioLength ?? 0);
                    if (DlcBuilderBuildSteps.Apply(after, after.Arrangements[i], audioLength, steps) is not { } built) continue;
                    after.Arrangements[i] = built;
                    count++;
                }

                return count;
            }, c => c);

            // Freshly generated levels land on the TOP one, which is the chart the charter just had.
            if (shownWasSingleLevel && _projectState.ActiveArrangement is { Levels.Count: > 1 } shown)
            {
                _projectState.ActiveLevel = shown.Levels.OrderBy(l => l.DifficultyIndex).Last();
            }

            _projectState.SetMessage(changed > 0
                ? _localization.Plural("Status_DlcBuilderFixesAppliedOne", "Status_DlcBuilderFixesApplied", changed)
                : L("Status_DlcBuilderFixesNoChange"), StatusSeverity.Success);
            _logger.Info(AppLogCategory.Editing,
                $"Applied DLC Builder's fixes and dynamic difficulty to {changed} of {indices.Count} arrangement(s) ({steps.LevelCountMode} level counts).");
            return changed;
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or ArgumentException
                                       or IndexOutOfRangeException or KeyNotFoundException or FormatException
                                       or NullReferenceException)
        {
            _logger.Error(AppLogCategory.Editing, "Applying DLC Builder's fixes failed.", ex);
            _projectState.SetMessage(L("Status_DlcBuilderFixesFailed", ex.Message), StatusSeverity.Error);
            return 0;
        }
    }

    /// <inheritdoc />
    public int FixExportFinding(ExportFixKind kind, Arrangement arrangement)
    {
        var project = _projectState.ActiveProject;
        var index = project?.Arrangements.IndexOf(arrangement) ?? -1;
        if (project == null || index < 0 || kind is ExportFixKind.None or ExportFixKind.OpenToneDesigner) return 0;

        try
        {
            var changes = RunOnSnapshot(project, after => Fixer().Fix(kind, after, after.Arrangements[index]), c => c);
            _projectState.SetMessage(changes > 0
                ? _localization.Plural("Status_ExportFixAppliedOne", "Status_ExportFixApplied", changes, arrangement.Name)
                : L("Status_ExportFixNoChange", arrangement.Name), changes > 0 ? StatusSeverity.Success : StatusSeverity.Warning);
            _logger.Info(AppLogCategory.Editing, $"Export fix {kind} on '{arrangement.Name}': {changes} change(s).");
            return changes;
        }
        catch (Exception ex)
        {
            _logger.Error(AppLogCategory.Editing, $"Export fix {kind} failed.", ex);
            _projectState.SetMessage(L("Status_ExportFixFailed", ex.Message), StatusSeverity.Error);
            return 0;
        }
    }

    /// <inheritdoc />
    public ToneKeyRepairProposal? RepairUndefinedToneKey(string undefinedKey)
    {
        var project = _projectState.ActiveProject;
        if (project == null || ToneKeyRepair.ProposeFor(project, undefinedKey) is not { } repair) return null;

        _historyService.Do(repair.Action);
        _chartEditor.RequestRedraw();
        _projectState.SetMessage(L("Status_ToneKeyRepaired", repair.From, repair.To), StatusSeverity.Success);
        _logger.Info(AppLogCategory.Editing, $"Export fix: renamed tone '{repair.From}' to '{repair.To}', the undefined key the chart uses.");
        return repair;
    }

    /// <summary>Clones the live project, runs <paramref name="work" /> on the clone and, when it
    /// changed anything, records the before/after pair as ONE undo node.</summary>
    private T RunOnSnapshot<T>(ChartProject project, Func<ChartProject, T> work, Func<T, int> changes)
    {
        var arrangementIndex = GetActiveArrangementIndex();
        var levelIndex = GetActiveLevelIndex();
        var vocalTrackIndex = GetActiveVocalTrackIndex();
        var before = CloneProject(project);
        var after = CloneProject(project);
        var result = work(after);
        if (changes(result) > 0)
        {
            _historyService.Do(new ProjectSnapshotAction(this, before, after, arrangementIndex, levelIndex,
                vocalTrackIndex));
            _chartEditor.RequestRedraw();
        }

        return result;
    }
}
