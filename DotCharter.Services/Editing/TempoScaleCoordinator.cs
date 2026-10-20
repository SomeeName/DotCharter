using DotCharter.Services.Editing.Tempo;
using DotCharter.Services.State;
using DotCharter.Localization;
namespace DotCharter.Services.Editing;

/// <summary>
/// Applies double-time / half-time tempo scaling to whatever the user has
/// selected. Computes the tightest enclosing measure-aligned range from the
/// selection and delegates the actual beat/event remapping to
/// <see cref="IBeatScaleService"/>.
/// </summary>
public sealed class TempoScaleCoordinator
{
    private readonly IBeatScaleService _beatScaleService;
    private readonly IHistoryService _historyManager;
    private readonly ProjectState _projectState;
    private readonly ISelectionService _selectionManager;
    private readonly IAppLogger _logger;
    private readonly ILocalizationService _localization;

    public TempoScaleCoordinator(
        IBeatScaleService beatScaleService,
        IHistoryService historyManager,
        ProjectState projectState,
        ISelectionService selectionManager,
        IAppLogger logger,
        ILocalizationService localization)
    {
        _beatScaleService = beatScaleService;
        _historyManager = historyManager;
        _projectState = projectState;
        _selectionManager = selectionManager;
        _logger = logger;
        _localization = localization;
    }

    public void ApplyDoubleTime() => Apply(2.0);
    public void ApplyHalfTime() => Apply(0.5);

    private void Apply(double factor)
    {
        var project = _projectState.ActiveProject;
        if (project == null || project.Beats.Count < 2)
        {
            _logger.Info(AppLogCategory.Editing, "Tempo scale skipped: no active project or insufficient beats.");
            return;
        }

        var (startMs, endMs) = ComputeSelectionTimeRange(project.Beats);
        if (startMs < 0 || endMs <= startMs)
        {
            _logger.Info(AppLogCategory.Editing,
                $"Tempo scale skipped: no usable selection range (selectedNotes={_selectionManager.SelectedNotes.Count}, selectedChords={_selectionManager.SelectedChords.Count}).");
            _projectState.SetMessage(_localization["Status_TempoScale_NoSelection"]);
            return;
        }

        // Snap to enclosing measure boundaries so musical structure is preserved.
        (startMs, endMs) = SnapToMeasureBounds(project.Beats, startMs, endMs);

        var action = _beatScaleService.BuildScaleAction(project, startMs, endMs, factor);
        if (action == null) return;

        if (_historyManager.Do(action))
        {
            // ReSharper disable once CompareOfFloatsByEqualityOperator -- sentinel: Apply() is called with exact literal 2.0 or 0.5
            var label = factor == 2.0 ? "double-time" : "half-time";
            _logger.Info(AppLogCategory.Editing,
                $"Applied {label} tempo scale: range=[{startMs:0.##},{endMs:0.##}] ms.");
            _projectState.SetMessage(_localization[
                // ReSharper disable once CompareOfFloatsByEqualityOperator -- sentinel: same as above
                factor == 2.0 ? "Status_TempoScale_DoubleApplied" : "Status_TempoScale_HalfApplied"]);
        }
    }

    private (double start, double end) ComputeSelectionTimeRange(IList<BeatMarker> beats)
    {
        var any = false;
        var min = double.MaxValue;
        var max = double.MinValue;

        void Consume(double t)
        {
            if (t < min) min = t;
            if (t > max) max = t;
            any = true;
        }

        foreach (var n in _selectionManager.SelectedNotes)
        {
            Consume(n.Position.GetPositionMs(beats));
            Consume(n.EndPosition.GetPositionMs(beats));
        }
        foreach (var c in _selectionManager.SelectedChords)
        {
            Consume(c.Position.GetPositionMs(beats));
            Consume(c.EndPosition.GetPositionMs(beats));
        }
        foreach (var h in _selectionManager.SelectedHandShapes)
        {
            Consume(h.Position.GetPositionMs(beats));
            Consume(h.EndPosition.GetPositionMs(beats));
        }

        return any ? (min, max) : (-1, -1);
    }

    /// <summary>
    /// Expands [startMs..endMs] outward to the nearest enclosing measure
    /// boundaries from the supplied beat-marker list. Visible for testing.
    /// </summary>
    public static (double start, double end) SnapToMeasureBounds(IList<BeatMarker> beats, double startMs, double endMs)
    {
        var snappedStart = startMs;
        var snappedEnd = endMs;
        for (var i = 0; i < beats.Count; i++)
        {
            if (!beats[i].IsMeasure) continue;
            if (beats[i].Time <= startMs) snappedStart = beats[i].Time;
            if (beats[i].Time >= endMs) { snappedEnd = beats[i].Time; break; }
        }
        return (snappedStart, snappedEnd);
    }
}
