using Avalonia.Controls;
using Avalonia.Data;
using DotCharter.UI.Controls;

namespace DotCharter.UI.Views.Editor;

public partial class Preview3DView : UserControl
{
    public Preview3DView()
    {
        InitializeComponent();

        // The 3D viewport is ALWAYS the Avalonia-composed host — there is no native
        // child-HWND swapchain host, so there is no airspace workaround. Only one viewport control instance ever exists
        // (this view is itself a single instance across layout toggles/pop-out).
        ViewportHost.Children.Add(CreateViewport());
    }

    private static Preview3DComposedCanvas CreateViewport()
    {
        var canvas = new Preview3DComposedCanvas { Name = "Viewport3D" };
        canvas.Bind(Preview3DComposedCanvas.EditorProperty, new Binding("ChartEditor"));
        canvas.Bind(Preview3DComposedCanvas.TransportProperty, new Binding("Transport"));
        canvas.Bind(Preview3DComposedCanvas.LivePlayProperty, new Binding("LivePlay"));
        canvas.Bind(Preview3DComposedCanvas.CapoProperty, new Binding("ChartEditor.ActiveArrangement.Capo"));
        canvas.Bind(Preview3DComposedCanvas.GameplayPresentationProperty, new Binding("IsGameplayPresentation"));
        return canvas;
    }
}
