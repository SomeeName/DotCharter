using Avalonia;

namespace DotCharter.UI.Input.Tools.EventInteractionGestures;

/// <summary>
/// Drag a Section/Phrase event point (or a multi-selection of them)
/// horizontally. The previews live on
/// <see cref="DotCharter.Services.Editing.Selection.ISelectionService.PreviewEventPoints"/> during the drag
/// and are flushed each move; on release the actual move is committed
/// through <see cref="IEventEditingService.CommitEventDragAndDrop"/>.
/// </summary>
public sealed class EventPointDragGesture : IEventGesture
{
    private readonly IEventEditingService _eventEditingService;
    private readonly IAppLogger _logger;
    private readonly DragState _dragState = new();
    private readonly GestureDragThreshold _threshold = new();

    private List<EventPoint> _originalEventPoints = [];

    public EventPointDragGesture(IEventEditingService eventEditingService, IAppLogger logger)
    {
        _eventEditingService = eventEditingService;
        _logger = logger;
    }

    public bool IsActive => _dragState.IsDragging;
    public bool HasDragged => _threshold.HasDragged;

    public void Start(TrackEditorContext track, EventPoint hitEp, double startMouseMs, Point pressOrigin)
    {
        Track = track;
        _originalEventPoints = track.Selection.SelectedEventPoints
            .Select(ep => new EventPoint { Position = ep.Position, Phrase = ep.Phrase, Section = ep.Section })
            .ToList();
        _dragState.Start(startMouseMs, hitEp.Position);
        _threshold.Arm(pressOrigin);
    }

    public void OnMoved(ToolContext context)
    {
        if (!IsActive || Track == null) return;
        // A CLICK is not a drag. Without this, one pixel of tremor while
        // selecting a marker re-snapped it to the grid — and an imported marker
        // is off-grid, so the jump was visible and undoable. See
        // GestureDragThreshold.
        if (!_threshold.Observe(context.Position)) return;

        var sel = Track.Selection;
        sel.PreviewEventPoints.Clear();
        var delta = ClampedDelta(context, _originalEventPoints);
        foreach (var orig in _originalEventPoints)
        {
            sel.PreviewEventPoints.Add(new EventPoint
            {
                Position = orig.Position + delta,
                Phrase = orig.Phrase,
                Section = orig.Section
            });
        }
    }

    /// <summary>The delta shortened so the EARLIEST marker lands on zero — never
    /// a per-marker floor, which piles everything that went negative onto beat 0
    /// and silently reorders the run. See <see cref="DragBounds" />.</summary>
    private FractionalPosition ClampedDelta(ToolContext context, IReadOnlyList<EventPoint> payload) =>
        DragBounds.ClampDeltaToStart(_dragState.GetDelta(context),
            DragBounds.Earliest(payload.Select(ep => ep.Position)));

    public void Commit(ToolContext context)
    {
        if (!IsActive || Track == null) { Reset(); return; }

        var sel = Track.Selection;
        var oldStates = sel.SelectedEventPoints
            .Select(ep => new EventPoint { Position = ep.Position, Phrase = ep.Phrase, Section = ep.Section })
            .ToList();
        var delta = ClampedDelta(context, oldStates);
        var newStates = oldStates.Select(ep => new EventPoint
        {
            Position = ep.Position + delta,
            Phrase = ep.Phrase,
            Section = ep.Section
        }).ToList();

        if (_eventEditingService.CommitEventDragAndDrop(oldStates, newStates))
        {
            _logger.Info(AppLogCategory.Events,
                $"Committed event point drag. Count={newStates.Count}, delta={delta}.");
        }

        Reset();
    }

    public void Cancel(ToolContext context) => Reset();

    private void Reset()
    {
        _originalEventPoints.Clear();
        Track?.Selection.PreviewEventPoints.Clear();
        Track = null;
        _dragState.Stop();
        _threshold.Reset();
    }

    public TrackEditorContext? Track { get; private set; }
}
