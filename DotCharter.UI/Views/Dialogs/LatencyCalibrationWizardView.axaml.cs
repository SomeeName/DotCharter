using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using DotCharter.Presentation.Contracts.Dialogs;

namespace DotCharter.UI.Views.Dialogs;

public partial class LatencyCalibrationWizardView : UserControl
{
    private ILatencyWizardScreen? _vm;

    public LatencyCalibrationWizardView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        _vm?.PropertyChanged -= OnVmPropertyChanged;
        _vm = DataContext as ILatencyWizardScreen;
        _vm?.PropertyChanged += OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Pull keyboard focus into the dialog when a tap step opens, so Space/Enter
        // reach DialogGrid_OnKeyDown (with a dialog open the global play/pause chord
        // stands down, so Space is free to mean "tap").
        if (e.PropertyName is nameof(ILatencyWizardScreen.IsTapStep)) FocusForTapStep();
    }

    private void FocusForTapStep()
    {
        if (_vm is not { IsTapStep: true }) return;
        var grid = this.FindControl<Grid>("DialogGrid");
        Dispatcher.UIThread.Post(() => grid?.Focus(), DispatcherPriority.Input);
    }

    private void DialogGrid_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is not { IsTapStep: true }) return;
        if (e.Key is not (Key.Space or Key.Enter)) return;
        _vm.TapCommand.Execute(null);
        e.Handled = true;
    }
}
