using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace DotCharter.UI.Views.Dialogs;

public partial class ChartFromAudioDialogView : UserControl
{
    public ChartFromAudioDialogView()
    {
        InitializeComponent();

        // the note that closing leaves a separation running is the point of the strip,
        // and in a short window the body scrolls. When the note first lays out — a run just
        // started, or the dialog opened onto one — scroll its whole strip into view.
        SeparationKeepsRunningNote.PropertyChanged += (_, e) =>
        {
            if (e.Property != BoundsProperty || e.GetOldValue<Rect>().Height > 0 || e.GetNewValue<Rect>().Height <= 0) return;
            Dispatcher.UIThread.Post(() => StemPanel.BringIntoView());
        };
    }
}
