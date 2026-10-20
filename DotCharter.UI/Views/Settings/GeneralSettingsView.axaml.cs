using Avalonia.Controls;
using DotCharter.UI.Views.Shell;

namespace DotCharter.UI.Views.Settings;

/// <summary>
///     Settings ▸ General on <c>IGeneralSettingsScreen</c>. Its Advanced body is
///     a second hidden gate inside the tab, so it is offered to the prewarm the
///     way Graphics offers its own.
/// </summary>
public partial class GeneralSettingsView : UserControl, IPrewarmableSurfaces
{
    public GeneralSettingsView() => InitializeComponent();

    public IReadOnlyList<Control> HiddenSurfaces() =>
        HiddenSubtreePrewarmer.HiddenNamedDescendants(this, ["GeneralAdvancedBody"]);
}
