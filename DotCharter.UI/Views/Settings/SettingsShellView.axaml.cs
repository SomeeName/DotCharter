using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

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

    private void OnPageKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.F || !e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        if (this.FindControl<TextBox>("SettingsSearchBox") is not { } box) return;
        box.Focus();
        box.SelectAll();
        e.Handled = true;
    }
}
