using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.UI.ViewModels.CommandPalette;

namespace DotCharter.UI.Views.CommandPalette;

public partial class CommandPaletteView : UserControl
{
    private CommandPaletteViewModel? _vm;

    public CommandPaletteView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Scrim.AddHandler(PointerPressedEvent, OnScrimPressed, RoutingStrategies.Tunnel);
        ResultList.Tapped += OnResultTapped;
        HookViewModel(DataContext as CommandPaletteViewModel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Scrim.RemoveHandler(PointerPressedEvent, OnScrimPressed);
        ResultList.Tapped -= OnResultTapped;
        HookViewModel(null);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
        => HookViewModel(DataContext as CommandPaletteViewModel);

    private void HookViewModel(CommandPaletteViewModel? vm)
    {
        if (ReferenceEquals(_vm, vm)) return;
        _vm?.PropertyChanged -= OnViewModelPropertyChanged;
        _vm = vm;
        if (_vm != null)
        {
            _vm.PropertyChanged += OnViewModelPropertyChanged;
            if (_vm.IsOpen) FocusSearch();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CommandPaletteViewModel.IsOpen) && _vm is { IsOpen: true })
            FocusSearch();
    }

    private void FocusSearch() => Dispatcher.UIThread.Post(() =>
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    });

    private void OnScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        // Only a click on the scrim background (not the card) dismisses.
        if (ReferenceEquals(e.Source, Scrim)) _vm?.Close();
    }

    private void OnResultTapped(object? sender, TappedEventArgs e)
    {
        // Execute only when a row (item container) was actually tapped — a tap
        // on the ListBox padding leaves the selection alone and no-ops.
        if (e.Source is Visual v &&
            (v is ListBoxItem || v.GetVisualAncestors().OfType<ListBoxItem>().Any()))
        {
            _vm?.ExecuteSelected();
        }
    }
}
