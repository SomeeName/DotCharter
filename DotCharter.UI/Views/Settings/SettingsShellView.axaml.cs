using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DotCharter.UI.Controls;

namespace DotCharter.UI.Views.Settings;

/// <summary>
///     The Settings page's chrome, bound to
///     <see cref="DotCharter.Presentation.Contracts.Settings.ISettingsShellScreen" />.
///     The one thing past the constructor is keyboard FOCUS: Ctrl+F from
///     anywhere on the page puts the caret in the search box, which no view
///     model can do. It holds no state and calls nothing on the contract.
/// </summary>
public partial class SettingsShellView : UserControl
{
    public SettingsShellView()
    {
        InitializeComponent();
        // TUNNEL, so it beats a focused TextBox/NumericUpDown swallowing the
        // chord — "find" is what Ctrl+F means on a page of ~250 settings.
        AddHandler(KeyDownEvent, OnPageKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>
    ///     Whether the page's tunnel takes this chord for the search box. Never while
    ///     an armed keybind box owns the keyboard: this tunnel runs before the box's
    ///     own, so Ctrl+F and Ctrl+Shift+F could not be captured as bindings.
    /// </summary>
    internal static bool ClaimsFindChord(Key key, KeyModifiers modifiers, bool keybindCaptureArmed) =>
        key == Key.F && modifiers.HasFlag(KeyModifiers.Control) && !keybindCaptureArmed;

    private void OnPageKeyDown(object? sender, KeyEventArgs e)
    {
        if (!ClaimsFindChord(e.Key, e.KeyModifiers, e.Source is KeybindCaptureBox { IsCapturing: true })) return;
        if (this.FindControl<TextBox>("SettingsSearchBox") is not { } box) return;
        box.Focus();
        box.SelectAll();
        e.Handled = true;
    }
}
