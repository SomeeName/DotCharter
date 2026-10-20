using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using DotCharter.Services.Infrastructure.Shell;

namespace DotCharter.UI.Platform;

/// <summary>
///     <see cref="IMainWindowHandle" /> for the desktop. Best-effort by
///     contract: before the window is shown, under a non-desktop lifetime, or
///     on a platform with no native handle there is nothing to return, and
///     <see cref="IntPtr.Zero" /> is what every caller is told to expect.
/// </summary>
public sealed class DesktopMainWindowHandle : IMainWindowHandle
{
    public IntPtr Value
    {
        get
        {
            if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime
                { MainWindow: { } window })
            {
                return IntPtr.Zero;
            }

            return window.TryGetPlatformHandle() is { } handle ? handle.Handle : IntPtr.Zero;
        }
    }
}
