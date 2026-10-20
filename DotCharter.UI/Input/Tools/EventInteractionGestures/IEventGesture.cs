namespace DotCharter.UI.Input.Tools.EventInteractionGestures;

/// <summary>
/// Per-gesture state machine owned by <see cref="EventInteractionTool"/>.
/// Each gesture is mutually exclusive — at most one is active at a time —
/// and lives only between an <see cref="IsActive"/>=true Start call and the
/// next <see cref="Commit"/> or <see cref="Cancel"/>.
///
/// Contract:
/// • Implementations MUST set <see cref="IsActive"/>=false in Commit/Cancel.
/// • <see cref="HasDragged"/> follows the standard "move was actually
///   observed during the gesture" semantic — it decides between
///   Commit (drag) and Cancel (click).
/// • <see cref="OnMoved"/> may be called many times and must be allocation-
///   light (it runs on the UI thread per pointer event).
/// </summary>
public interface IEventGesture
{
    bool IsActive { get; }
    bool HasDragged { get; }
    void OnMoved(ToolContext context);
    void Commit(ToolContext context);
    void Cancel(ToolContext context);
}
