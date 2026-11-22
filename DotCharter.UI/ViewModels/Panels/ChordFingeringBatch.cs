using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.UI.ViewModels.Panels;

/// <summary>
///     The panel's fingering batch verbs: generate every missing fingering as one history node,
///     and walk to the next template that still needs one (saving the typed draft first, then
///     seeking the timeline to its first use). Owned by <see cref="ChordTemplateEditorViewModel" />.
/// </summary>
internal sealed class ChordFingeringBatch(
    ChartEditorViewModel editor,
    IFingeringGenerationService fingeringService,
    IHistoryService historyService,
    IChordFingerSurface surface,
    ChordFingerDraftEditor fingers,
    Func<Arrangement, ChordNamingOptions> chordNaming,
    Action reloadShown,
    Action<ChordTemplate> showTemplate)
{
    private readonly ChartEditorViewModel _editor = editor;
    private readonly IFingeringGenerationService _fingeringService = fingeringService;
    private readonly IHistoryService _historyService = historyService;
    private readonly IChordFingerSurface _surface = surface;
    private readonly ChordFingerDraftEditor _fingers = fingers;
    private readonly Func<Arrangement, ChordNamingOptions> _chordNaming = chordNaming;
    private readonly Action _reloadShown = reloadShown;
    private readonly Action<ChordTemplate> _showTemplate = showTemplate;

    internal void GenerateAllFingerings()
    {
        var arrangement = _editor.ActiveArrangement;
        if (arrangement == null) return;

        var actions = ChordTemplateFingeringBackfill.Plan(arrangement, _fingeringService, _editor, _chordNaming(arrangement));
        if (actions.Count == 0) return;
        _historyService.Do(new CompositeAction(actions) { LabelKey = "History_GenerateFingerings", LinkPolicyOverride = LinkPolicy.ShareFirstChanged });

        if (_surface.Template != null) _reloadShown();
    }

    internal void SelectNextMissingFingering()
    {
        // Auto-save any in-progress finger edits before jumping away — matches
        // the user's mental model of "save & next" tied to this single button.
        // Only TYPED ones: on "+ same grip" Save has work with nothing typed,
        // and walking on to the next shape must not copy this one's fingering.
        if (_surface.HasPendingFingerEdits && !_fingers.CommitPendingFingerEdits()) return;

        SelectNextMissingFingering(_surface.Template?.Id ?? -1);
    }

    internal void SaveAndNextFingering()
    {
        // An isolated save may mint a new id. Walk from the shape the user was editing.
        var startId = _surface.Template?.Id ?? -1;
        if (_fingers.CommitPendingFingerEdits()) SelectNextMissingFingering(startId);
    }

    private void SelectNextMissingFingering(int startId)
    {
        var arrangement = _editor.ActiveArrangement;
        if (arrangement == null) return;

        var pending = _fingeringService.FindTemplatesNeedingFingering(arrangement);
        if (pending.Count == 0) return;

        // Skip past the current template so repeated clicks walk the whole list.
        var next = pending.FirstOrDefault(t => t.Id > startId) ?? pending[0];

        _showTemplate(next);

        // Jump the timeline to the first chord/handshape that uses this template
        // so the user sees the unset chord on the highway, EOF-style.
        SeekToFirstUsage(arrangement, next);
    }

    private void SeekToFirstUsage(Arrangement arrangement, ChordTemplate template)
    {
        FractionalPosition? earliest = null;
        foreach (var level in arrangement.Levels)
        {
            foreach (var c in level.Chords)
            {
                if (c.TemplateId == template.Id && (earliest == null || c.Position < earliest))
                    earliest = c.Position;
            }

            foreach (var h in level.HandShapes)
            {
                if (h.TemplateId == template.Id && (earliest == null || h.Position < earliest))
                    earliest = h.Position;
            }
        }
        if (earliest == null) return;
        var ms = earliest.Value.GetPositionMs(_editor.Beats);
        _editor.ExecuteSeek(ms);
    }

}
