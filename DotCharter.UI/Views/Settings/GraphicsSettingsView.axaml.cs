using Avalonia.Controls;
using DotCharter.UI.Views.Shell;

namespace DotCharter.UI.Views.Settings;

/// <summary>
///     Settings ▸ Graphics on <c>IGraphicsSettingsScreen</c>. Its Advanced body is
///     a second hidden gate inside the tab, so it is offered to the prewarm
///     like the old page's were — without that the first open pays its layout.
/// </summary>
public partial class GraphicsSettingsView : UserControl, IPrewarmableSurfaces
{
    public GraphicsSettingsView() => InitializeComponent();

    public IReadOnlyList<Control> HiddenSurfaces() =>
        HiddenSubtreePrewarmer.HiddenNamedDescendants(this, ["GraphicsAdvancedBody"]);
}
