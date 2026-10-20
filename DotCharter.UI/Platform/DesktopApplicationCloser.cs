using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using DotCharter.Services.Infrastructure.Shell;

namespace DotCharter.UI.Platform;

/// <summary>
///     <see cref="IApplicationCloser" /> for the desktop: closes the main window
///     so shutdown takes the same path as a normal exit (<c>MainWindow.OnClosing</c>
///     → clean shutdown / TerminateNow). The detached updater survives either
///     way and waits on this PID.
/// </summary>
public sealed class DesktopApplicationCloser : IApplicationCloser
{
    public void Close()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
        {
            window.Close();
            return;
        }

        Environment.Exit(0);
    }
}
