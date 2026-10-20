using DotCharter.Localization;
using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.State;

namespace DotCharter.Services.Editing.Maintenance;

/// <summary>
/// Generate FHP, for the whole arrangement and for the selected range: the anchors the generator
///     proposes replace the ones in scope in ONE undo node, together with the fingering of every
///     chord that follows the hand (a free grip gets its own template minted in that node; a
///     shared template is never edited). A collaborator <see cref="ChartMaintenanceService" />
///     owns and forwards to.
/// </summary>
internal sealed class FhpGenerationCommands(
    ProjectState projectState,
    IChartEditorSurface chartEditor,
    IHistoryService historyService,
    ILocalizationService localization,
    IAppLogger logger,
    IFretAnchorGenerationService fretAnchorGenerationService)
{
    private readonly IChartEditorSurface _chartEditor = chartEditor;
    private readonly IFretAnchorGenerationService _fretAnchorGenerationService = fretAnchorGenerationService;
    private readonly IHistoryService _historyService = historyService;
    private readonly ILocalizationService _localization = localization;
    private readonly IAppLogger _logger = logger;
    private readonly ProjectState _projectState = projectState;

    public void GenerateFhp()
    {
        var arrangement = _projectState.ActiveArrangement;
        if (arrangement == null)
        {
            _projectState.MainDisplayMessage = L("Status_NoArrangementSelected");
            return;
        }

        // Regenerate: existing anchors are replaced (one undoable action).
        // The tool does not refuse when anchors exist, which would make it dead on
        // every imported chart — imports always generate anchors.
        var actions = new List<IChartAction>();
        var generatedCount = 0;
        var replacedCount = 0;
        // A chord whose fingering is still the suggestion is FREE: the walk may
        // keep the hand where it is and the chord's fingering follows.
        // Its variant is its own template, minted in THIS undo node with the
        // anchors; the shared template is never touched.
        var freeGrip = ChordFingeringFollower.FreeGripResolver(arrangement);
        var fingering = new FingeringGenerationService();
        var minted = new List<ChordTemplate>();
        var chordRepoints = new List<(Chord, int, int)>();
        var shapeRepoints = new List<(HandShape, int, int)>();
        foreach (var level in arrangement.Levels)
        {
            var generatedAnchors = _fretAnchorGenerationService
                .GenerateAnchors(level, chordTemplates: arrangement.ChordTemplates, capo: arrangement.Capo,
                    bassArrangement: ArrangementInstrument.IsBassPart(arrangement), freeGrip: freeGrip,
                    tuning: arrangement)
                .ToList();
            if (generatedAnchors.Count == 0) continue;

            if (level.Anchors.Count > 0)
            {
                replacedCount += level.Anchors.Count;
                actions.Add(new RemoveAnchorsAction(level.Anchors, level.Anchors.ToList()));
            }

            actions.Add(new AddAnchorsAction(level.Anchors, generatedAnchors));
            generatedCount += generatedAnchors.Count;

            var plan = ChordFingeringFollower.Plan(arrangement, level, generatedAnchors, freeGrip, fingering, minted);
            minted.AddRange(plan.MintedTemplates);
            chordRepoints.AddRange(plan.Chords);
            shapeRepoints.AddRange(plan.HandShapes);
        }

        if (generatedCount == 0 || actions.Count == 0)
        {
            _projectState.MainDisplayMessage = L("Status_FhpNoFrettedNotes");
            return;
        }

        if (minted.Count > 0) actions.Insert(0, new AddChordTemplatesAction(arrangement.ChordTemplates, minted));
        if (chordRepoints.Count > 0) actions.Add(new SetTemplateIdsAction(chordRepoints, _chartEditor));
        if (shapeRepoints.Count > 0) actions.Add(new SetHandShapeTemplateIdsAction(shapeRepoints, _chartEditor));

        var action = actions.Count == 1 ? actions[0] : new CompositeAction(actions) { LabelKey = "History_GenerateFhp" };
        if (!_historyService.Do(action)) return;

        SelectionBulkOps.ReplaceWith(_chartEditor.Selection.SelectedAnchors,
            _projectState.ActiveLevel?.Anchors ?? Enumerable.Empty<FretAnchor>());

        _chartEditor.RequestRedraw();
        _projectState.MainDisplayMessage = replacedCount > 0
            ? L("Status_FhpRegenerated", generatedCount, replacedCount)
            : L("Status_FhpGenerated", generatedCount);
        _logger.Info(AppLogCategory.Editing,
            $"Generated FHP anchors for arrangement '{arrangement.Name}'. Levels={arrangement.Levels.Count}, anchors={generatedCount}, replaced={replacedCount}, " +
            $"chords re-fingered={chordRepoints.Count}, templates minted={minted.Count}.");
    }

    public void GenerateFhpForSelection()
    {
        var level = _projectState.ActiveLevel;
        if (level == null)
        {
            _projectState.MainDisplayMessage = L("Status_NoActiveLevel");
            return;
        }

        var range = GetSelectedSoundRange(_chartEditor);
        if (range == null)
        {
            _projectState.MainDisplayMessage = L("Status_SelectNoteChordRange");
            return;
        }

        var generatedAnchors = _fretAnchorGenerationService
            .GenerateAnchors(level, range.Value.Start, range.Value.End,
                _projectState.ActiveArrangement?.ChordTemplates,
                _projectState.ActiveArrangement?.Capo ?? 0,
                _projectState.ActiveArrangement is { } active && ArrangementInstrument.IsBassPart(active),
                tuning: _projectState.ActiveArrangement)
            .ToList();
        if (generatedAnchors.Count == 0)
        {
            _projectState.MainDisplayMessage = L("Status_FhpNoFrettedNotesInRange");
            return;
        }

        var anchorsToReplace = level.Anchors
            .Where(anchor => anchor.Position >= range.Value.Start && anchor.Position <= range.Value.End)
            .ToList();

        var actions = new List<IChartAction>();
        if (anchorsToReplace.Count > 0)
            actions.Add(new RemoveAnchorsAction(level.Anchors, anchorsToReplace));
        actions.Add(new AddAnchorsAction(level.Anchors, generatedAnchors));

        var action = actions.Count == 1 ? actions[0] : new CompositeAction(actions) { LabelKey = "History_GenerateFhp" };
        if (!_historyService.Do(action)) return;

        SelectionBulkOps.ReplaceWith(_chartEditor.Selection.SelectedAnchors, generatedAnchors);

        _chartEditor.RequestRedraw();
        _projectState.MainDisplayMessage = L("Status_FhpGeneratedForRange", generatedAnchors.Count);
        _logger.Info(AppLogCategory.Editing,
            $"Generated FHP anchors for selected range. Start={FormatPosition(range.Value.Start)}, End={FormatPosition(range.Value.End)}, removed={anchorsToReplace.Count}, added={generatedAnchors.Count}.");
    }

    /// <summary>The span from the first selected note or chord to the last one's end, or null with nothing selected.</summary>
    public static (FractionalPosition Start, FractionalPosition End)? GetSelectedSoundRange(IChartEditorSurface editor)
    {
        var selectedNotes = editor.SelectedNotes.ToList();
        var selectedChords = editor.SelectedChords.ToList();
        if (selectedNotes.Count == 0 && selectedChords.Count == 0) return null;

        var start = FractionalPosition.Min(
            selectedNotes.Count > 0 ? selectedNotes.Min(n => n.Position) : new FractionalPosition(999999),
            selectedChords.Count > 0 ? selectedChords.Min(c => c.Position) : new FractionalPosition(999999));

        var end = FractionalPosition.Max(
            selectedNotes.Count > 0 ? selectedNotes.Max(n => n.EndPosition) : FractionalPosition.Zero,
            selectedChords.Count > 0 ? selectedChords.Max(c => c.EndPosition) : FractionalPosition.Zero);

        if (end < start) end = start;
        return (start, end);
    }

    private string FormatPosition(FractionalPosition position)
        => position.FormatWithMs(_chartEditor.Beats);

    private string L(string key) => _localization[key];

    private string L(string key, params object[] args) => _localization.Format(key, args);
}
