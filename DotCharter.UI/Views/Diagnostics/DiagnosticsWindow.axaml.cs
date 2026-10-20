using System.ComponentModel;
using Avalonia.Controls;
using DotCharter.UI.Diagnostics;
using DotCharter.UI.ViewModels.Diagnostics;

namespace DotCharter.UI.Views.Diagnostics;

public partial class DiagnosticsWindow : Window
{
    public DiagnosticsWindow()
    {
        InitializeComponent();
        // The global UI scale + RTL, shared with MainWindow.
        UI.Views.Shell.WindowUiScale.Attach(this);
        // While this read-only dashboard is open, the 2D timeline reveals its
        // corner FPS readout (otherwise hidden from normal users). Ref-counted
        // in RenderDiagnostics; balanced open/close per window instance.
        Opened += (_, _) => RenderDiagnostics.DashboardOpened();
        Closed += (_, _) => RenderDiagnostics.DashboardClosed();

        DataContextChanged += (_, _) => AttachStartupBar();
    }

    public DiagnosticsWindow(DiagnosticsViewModel viewModel) : this() => DataContext = viewModel;

    /// <summary>
    ///     The startup bar's segment widths are the phase durations as STAR
    ///     weights, and they have to be assigned rather than bound:
    ///     <c>Grid.ColumnDefinitions</c> is a plain CLR property, not an
    ///     <c>AvaloniaProperty</c>, so a <c>{Binding}</c> onto it is not a
    ///     binding at all — it compiles and does nothing.
    ///     <para>
    ///         The seven segment Borders stay in the markup with their colours;
    ///         only the weights come from here, so a phase that took no time
    ///         collapses to nothing instead of claiming a seventh of the bar.
    ///     </para>
    /// </summary>
    private void AttachStartupBar()
    {
        ApplyStartupBar();
        if (DataContext is INotifyPropertyChanged inpc)
        {
            inpc.PropertyChanged -= OnViewModelPropertyChanged;
            inpc.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DiagnosticsViewModel.StartupBarColumns)) ApplyStartupBar();
    }

    private void ApplyStartupBar()
    {
        if (DataContext is not DiagnosticsViewModel vm) return;
        var bar = this.FindControl<Grid>("StartupBar");
        if (bar == null) return;

        var weights = vm.StartupBarColumns;
        if (weights.Count == 0) return;

        // Assigned column by column: replacing the collection would orphan the
        // seven declared Borders, which address their columns by INDEX.
        for (var i = 0; i < bar.ColumnDefinitions.Count && i < weights.Count; i++)
            bar.ColumnDefinitions[i].Width = weights[i].Width;
    }
}
