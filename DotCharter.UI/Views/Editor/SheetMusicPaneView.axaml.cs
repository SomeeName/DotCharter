using Avalonia.Controls;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.UI.Views.Editor;

public partial class SheetMusicPaneView : UserControl
{
    public SheetMusicPaneView()
    {
        InitializeComponent();

        // Click/drag-to-seek + select + the editing gestures from the sheet
        // strip. Wired here (not in MainWindow) so it survives the view being
        // re-created when the pane floats or re-docks.
        if (this.FindControl<Controls.Sheet.SheetMusicView>("Sheet") is { } sheet)
        {
            sheet.SeekRequested += ms =>
            {
                if (DataContext is SheetMusicPaneViewModel vm) vm.Seek(ms);
            };
            sheet.SelectRequested += ms =>
            {
                if (DataContext is SheetMusicPaneViewModel vm) vm.SelectAt(ms);
            };
            sheet.SelectSoundsRequested += (notes, chords, additive) =>
            {
                if (DataContext is SheetMusicPaneViewModel vm) vm.SelectSounds(notes, chords, additive);
            };
            sheet.AddNoteRequested += (ms, stringIndex) =>
            {
                if (DataContext is SheetMusicPaneViewModel vm) vm.AddNoteAt(ms, stringIndex);
            };
            sheet.SetFretRequested += fret =>
            {
                if (DataContext is SheetMusicPaneViewModel vm) vm.SetFret(fret);
            };
            sheet.DurationStepRequested += step =>
            {
                if (DataContext is not SheetMusicPaneViewModel vm) return;
                if (step > 0) vm.LengthenDuration();
                else vm.ShortenDuration();
            };
        }
    }
}
