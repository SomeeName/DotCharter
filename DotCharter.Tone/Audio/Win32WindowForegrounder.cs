using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace DotCharter.Tone.Audio;

/// <summary>
/// Best-effort helper to bring a freshly-created VST3 plugin editor
/// window to the foreground on Windows.
///
/// Background: JUCE's <c>Component::toFront(true)</c> calls
/// <c>SetForegroundWindow</c> on the JUCE message thread — which is not
/// the Avalonia UI thread, and therefore not the foreground thread, so
/// Windows silently downgrades the request to a taskbar flash (because
/// of the "foreground lock" introduced in Win98 / formalised in Win2k).
/// The plugin window appears, but behind our main window — making users
/// think the editor is broken.
///
/// Workaround: after <c>ShowPluginEditor</c> returns, schedule (with a
/// small delay so the JUCE message loop has time to create the HWND)
/// an enumeration of top-level windows belonging to our process. We
/// filter out the Avalonia main window by its class name prefix and
/// then call <c>AllowSetForegroundWindow(ASFW_ANY)</c> +
/// <c>SetForegroundWindow</c> / <c>BringWindowToTop</c> from the
/// caller's context, which (on the UI thread) holds the foreground
/// lock and is allowed to grant it to another window in the same
/// process.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class Win32WindowForegrounder
{
    private const uint AsfwAny = unchecked((uint)-1);
    private const int SwShow = 5;
    private const int SwRestore = 9;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint dwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    /// <summary>
    /// Snapshot the set of top-level HWNDs that belong to this process
    /// right now. For diffing against post-show enumeration so we can
    /// pick out the freshly-created plugin window even if its class
    /// name overlaps with something else.
    /// </summary>
    public static HashSet<IntPtr> SnapshotProcessTopLevelWindows()
    {
        if (!OperatingSystem.IsWindows()) return [];
        var pid = (uint)Environment.ProcessId;
        var result = new HashSet<IntPtr>();
        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out var owner);
            if (owner == pid && IsWindowVisible(hWnd)) result.Add(hWnd);
            return true;
        }, IntPtr.Zero);
        return result;
    }

    /// <summary>
    /// Schedule a best-effort foreground-bringup for any windows that
    /// appeared in this process since <paramref name="before"/> was
    /// snapshotted. Safe to call from any thread.
    /// </summary>
    public static void BringNewWindowsToFront(HashSet<IntPtr> before, int delayMs = 120)
    {
        if (!OperatingSystem.IsWindows()) return;
        _ = Task.Run(async () =>
        {
            // Give JUCE's MessageManager a moment to create the HWND.
            await Task.Delay(delayMs).ConfigureAwait(false);

            // Grant foreground to anyone in our process (covers JUCE's
            // message thread which would otherwise be blocked).
            try { AllowSetForegroundWindow(AsfwAny); } catch { /* ignored */ }

            var after = SnapshotProcessTopLevelWindows();
            after.ExceptWith(before);
            foreach (var hWnd in after)
            {
                // Filter out anything that looks like an Avalonia window
                // (class name typically starts with "Avalonia").
                var sb = new StringBuilder(256);
                GetClassName(hWnd, sb, sb.Capacity);
                var cls = sb.ToString();
                if (cls.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase)) continue;

                try { ShowWindow(hWnd, SwRestore); } catch { /* ignored */ }
                try { ShowWindow(hWnd, SwShow); } catch { /* ignored */ }
                try { BringWindowToTop(hWnd); } catch { /* ignored */ }
                try { SetForegroundWindow(hWnd); } catch { /* ignored */ }
            }
        });
    }
}
