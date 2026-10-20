namespace DotCharter.Services.Infrastructure.Shell;

/// <summary>
///     The main window's native handle, for the few OS calls that want a parent
///     to centre a modal on — an ASIO driver's own control panel is the one that
///     exists today.
///     <para>
///         It is a seam rather than a direct lookup because the alternative is a
///         view model reaching into the Avalonia application lifetime, which
///         costs that view model the whole UI framework as a dependency. The
///         desktop implementation lives in <c>DotCharter.UI/Platform/</c> with
///         the rest of the OS adapters.
///     </para>
/// </summary>
public interface IMainWindowHandle
{
    /// <summary>
    ///     The handle, or <see cref="IntPtr.Zero" /> when there is no window to
    ///     parent to. Zero is a legitimate answer every caller must accept: the
    ///     OS call then centres on the screen instead of the window, which is
    ///     the behaviour a headless or not-yet-shown app has always had.
    /// </summary>
    IntPtr Value { get; }
}
