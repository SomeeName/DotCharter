namespace DotCharter.UI.Input.Tools;

public class DragState
{
    public bool IsDragging { get; private set; }
    public bool HasDragged { get; set; }
    public double StartMs { get; private set; }
    public FractionalPosition ReferencePosition { get; private set; }

    public void Start(double startMs, FractionalPosition referencePosition)
    {
        StartMs = startMs;
        ReferencePosition = referencePosition;
        IsDragging = true;
        HasDragged = false;
    }

    public void Stop()
    {
        IsDragging = false;
        HasDragged = false;
    }

    public FractionalPosition GetDelta(ToolContext context)
    {
        var rawDeltaMs = context.ExactTimeMs - StartMs;
        var origMs = ReferencePosition.GetPositionMs(context.ViewModel.ChartEditor.Beats);
        var snappedPos = context.ViewModel.ChartEditor.SnapToGrid(origMs + rawDeltaMs);
        return snappedPos - ReferencePosition;
    }
}
