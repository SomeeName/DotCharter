using Avalonia.Controls;
using Avalonia.VisualTree;
using DotCharter.UI.Controls;
using DotCharter.UI.Views.Shell;

namespace DotCharter.UI.Views.Settings;

/// <summary>
///     Settings ▸ Keybinds, bound to <see cref="DotCharter.Presentation.Contracts.Settings.IKeybindsSettingsScreen" /> and nothing else.
///     Each of its 140 rows carries two pills, the chord and the MIDI trigger, and the one of the mode NOT on screen is a
///     hidden subtree per row — offered to the prewarm, or the first Keyboard ⇄ MIDI switch pays 140 rows of first layout.
/// </summary>
public partial class KeybindsSettingsView : UserControl, IPrewarmableSurfaces
{
    public KeybindsSettingsView() => InitializeComponent();

    public IReadOnlyList<Control> HiddenSurfaces() =>
    [
        .. HiddenSubtreePrewarmer.HiddenNamedDescendants(this, ["MidiInputState"]),
        .. this.GetVisualDescendants().OfType<Control>()
            .Where(c => !c.IsVisible && (c is KeybindCaptureBox || c.Classes.Contains("MidiCell")))
    ];
}
