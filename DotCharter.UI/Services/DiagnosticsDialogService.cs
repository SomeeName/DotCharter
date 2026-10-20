using Avalonia.Controls.ApplicationLifetimes;
using DotCharter.UI.ViewModels.Diagnostics;
using DotCharter.UI.Views.Diagnostics;

namespace DotCharter.UI.Services;

/// <summary>
///     Shows the read-only Session Health dashboard window. Kept out of the view
///     models so they never
///     construct Avalonia windows or resolve services directly.
/// </summary>
public interface IDiagnosticsDialogService
{
    void ShowDiagnostics();
}

public sealed class DiagnosticsDialogService : IDiagnosticsDialogService
{
    private readonly Func<DiagnosticsViewModel> _viewModelFactory;

    /// <param name="viewModelFactory">Builds the dashboard's view model (a transient)
    /// each time a NEW window opens; never called while one is open.</param>
    public DiagnosticsDialogService(Func<DiagnosticsViewModel> viewModelFactory) => _viewModelFactory = viewModelFactory;

    /// <summary>The one open dashboard. Asking again brings it forward instead of
    /// stacking another window on it.</summary>
    internal DiagnosticsWindow? Open { get; private set; }

    public void ShowDiagnostics()
    {
        if (Open is { } open)
        {
            if (open.WindowState == Avalonia.Controls.WindowState.Minimized)
                open.WindowState = Avalonia.Controls.WindowState.Normal;
            open.Activate();
            return;
        }

        var viewModel = _viewModelFactory();
        var window = new DiagnosticsWindow(viewModel);
        Open = window;
        window.Closed += (_, _) => Open = null;

        var owner = Avalonia.Application.Current?.ApplicationLifetime
            is IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow
                : null;

        // Non-modal so the dashboard can be watched while the app is used.
        if (owner is not null) window.Show(owner);
        else window.Show();
    }
}
