using Avalonia.Controls;
using DotCharter.UI.Views.Shell;

namespace DotCharter.UI.Views.Settings;

/// <summary>
///     Settings ▸ Audio on <c>IAudioSettingsScreen</c>. Its Advanced body is a
///     second hidden gate inside the tab, so it is offered to the prewarm the
///     way Graphics' is — without that the first open pays its layout.
/// </summary>
public partial class AudioSettingsView : UserControl, IPrewarmableSurfaces
{
    public AudioSettingsView() => InitializeComponent();

    public IReadOnlyList<Control> HiddenSurfaces() =>
        HiddenSubtreePrewarmer.HiddenNamedDescendants(this, ["AudioAdvancedBody"]);
}
