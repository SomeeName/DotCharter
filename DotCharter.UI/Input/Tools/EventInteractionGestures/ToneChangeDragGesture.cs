using Avalonia;
using DotCharter.Services.Editing.Tones;

namespace DotCharter.UI.Input.Tools.EventInteractionGestures;

/// <summary>
/// Drag a tone change (and every selected tone change with it) along the Tone row. The
/// live positions follow the grid-snapped pointer through the pure <see cref="ToneChangeDrag" />,
/// which clamps at zero and refuses a stack or a swap; the release pushes ONE undo step and a
/// cancel (Esc, capture loss, a click) puts every change back.
/// </summary>
public sealed class ToneChangeDragGesture : IEventGesture
{
    private readonly IHistoryService _historyManager;
    private readonly IAppLogger _logger;
    private readonly DragState _dragState = new();
    private readonly GestureDragThreshold _threshold = new();

    private ToneChangeDrag? _drag;

    public ToneChangeDragGesture(IHistoryService historyManager, IAppLogger logger)
    {
        _historyManager = historyManager;
        _logger = logger;
    }

    public bool IsActive => _dragState.IsDragging;
    public bool HasDragged => _threshold.HasDragged;

    public TrackEditorContext? Track { get; private set; }

    public void Start(TrackEditorContext track, ToneChange hitTone, double startMouseMs, Point pressOrigin)
    {
        Track = track;
        var moving = track.Selection.SelectedTones.Contains(hitTone)
            ? track.Selection.SelectedTones.ToList()
            : [hitTone];
        _drag = new ToneChangeDrag(track.Tones, moving);
        _dragState.Start(startMouseMs, hitTone.Position);
        _threshold.Arm(pressOrigin);
    }

    public void OnMoved(ToolContext context)
    {
        if (!IsActive || _drag == null) return;
        if (!_threshold.Observe(context.Position)) return;

        _drag.Preview(_dragState.GetDelta(context), context.ViewModel.ChartEditor.Beats);
    }

    public void Commit(ToolContext context)
    {
        if (!IsActive || _drag == null) { Reset(); return; }

        var count = _drag.Count;
        var delta = _drag.Applied;
        if (_drag.Commit() is { } action && _historyManager.Do(action))
            _logger.Info(AppLogCategory.Events, $"Committed tone change drag. Count={count}, delta={delta}.");

        Reset();
    }

    public void Cancel(ToolContext context)
    {
        _drag?.Restore();
        Reset();
    }

    private void Reset()
    {
        _drag = null;
        Track = null;
        _dragState.Stop();
        _threshold.Reset();
    }
}
