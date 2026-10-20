using Avalonia.Controls;
using Avalonia.Threading;
using DotCharter.UI.ViewModels.PlayMode;

namespace DotCharter.UI.Views.PlayMode.Screens;

public partial class PauseMenuView : UserControl
{
    private PlayModeViewModel? _vm;

    public PauseMenuView()
    {
        InitializeComponent();
        // The menu lives in a deferred host that is built once and then only
        // shown and hidden, so focus moves on every open, not on construction:
        // a screen-reader user learns the menu opened by landing in it.
        // Resume is the primary action, so it takes the focus.
        DataContextChanged += (_, _) =>
        {
            _vm?.PropertyChanged -= OnViewModelPropertyChanged;
            _vm = DataContext as PlayModeViewModel;
            if (_vm == null) return;
            _vm.PropertyChanged += OnViewModelPropertyChanged;
            if (_vm.IsPausedScreen) FocusResume();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _vm?.PropertyChanged -= OnViewModelPropertyChanged;
            _vm = null;
        };
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayModeViewModel.IsPausedScreen) && _vm!.IsPausedScreen) FocusResume();
    }

    private void FocusResume() => Dispatcher.UIThread.Post(() => this.FindControl<Button>("PauseResume")?.Focus(),
        DispatcherPriority.Input);
}
