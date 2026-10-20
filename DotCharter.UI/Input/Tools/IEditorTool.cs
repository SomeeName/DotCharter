using Avalonia.Input;

namespace DotCharter.UI.Input.Tools;

public interface IEditorTool
{
    void OnPointerPressed(PointerPressedEventArgs e, ToolContext context);
    void OnPointerMoved(PointerEventArgs e, ToolContext context);
    void OnPointerReleased(PointerReleasedEventArgs e, ToolContext context);
    void OnPointerExited(PointerEventArgs e, ToolContext context);
}
