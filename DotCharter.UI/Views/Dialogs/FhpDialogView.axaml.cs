using Avalonia.Controls;
using Avalonia.Input;

namespace DotCharter.UI.Views.Dialogs;

public partial class FhpDialogView : UserControl
{
    public FhpDialogView() => InitializeComponent();

    private void NumericUpDown_OnKeyDown(object? sender, KeyEventArgs e)
    {
        // Always allow basic navigation and control keys
        if (e.Key is Key.Back or Key.Delete or Key.Tab or Key.Enter or
            Key.Left or Key.Right or Key.Up or Key.Down)
        {
            return;
        }

        // Check if the pressed key is a number (Top row numbers OR Numpad numbers)
        var isNumber = e.Key is >= Key.D0 and <= Key.D9 or >= Key.NumPad0 and <= Key.NumPad9;

        // If it is NOT a number, flag the event as "Handled" so the input box ignores it completely
        if (!isNumber) e.Handled = true;
    }
}
