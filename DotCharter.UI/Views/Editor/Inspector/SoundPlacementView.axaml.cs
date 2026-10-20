using Avalonia.Controls;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.UI.Views.Editor.Inspector;

/// <summary>The Sound tab's Placement section for one note.</summary>
public partial class SoundPlacementView : UserControl
{
    public SoundPlacementView() => InitializeComponent();

    // The Length box's spinner: one grid divisor step, through the SAME command the bulk
    // verbs' -/+ and the ] / [ keys use.
    private void OnNoteLengthSpin(object? sender, SpinEventArgs e)
    {
        if (DataContext is not InspectorViewModel vm) return;
        vm.StepSustainCommand.Execute(e.Direction == SpinDirection.Increase ? "1" : "-1");
    }
}
