using Avalonia.Controls;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.UI.Views.Editor.Inspector;

/// <summary>The Placement page's position card for one note: its fields stage, the footer's Apply writes.</summary>
public partial class SoundPlacementView : UserControl
{
    public SoundPlacementView() => InitializeComponent();

    // The Sustain box's spinner: one grid step, staged like a typed length.
    private void OnNoteLengthSpin(object? sender, SpinEventArgs e)
    {
        if (DataContext is not InspectorViewModel vm) return;
        vm.Staged.Placement.StepSustain(e.Direction == SpinDirection.Increase ? 1 : -1);
    }
}
