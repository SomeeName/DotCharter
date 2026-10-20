using System.Runtime.InteropServices;

namespace DotCharter.UI.Platform;

/// <summary>
///     Resolves the refresh rate (Hz) of the monitor a window currently sits
///     on. Used by the 3D render loop to translate the fps cap's "0" into
///     "the display's own rate": presentation paths that never block on vsync
///     (the composed compositor path always; the native swapchain under
///     Mailbox/Immediate) would otherwise free-run hundreds of fps of frames
///     the panel can never show. Win32-only, deliberately (the app is
///     Windows-first and the render loop already is).
/// </summary>
internal static class DisplayRefresh
{
    private const int EnumCurrentSettings = -1;

    /// <summary>
    ///     Refresh rate of the monitor nearest to <paramref name="hwnd" />
    ///     (the primary display when the handle is 0), clamped to a sane
    ///     [24, 480] band. Falls back to 60 when the query fails — a
    ///     conservative floor, never a free-run. Safe from any thread.
    /// </summary>
    public static int ForWindowOrDefault(nint hwnd, int fallback = 60)
    {
        try
        {
            // hwnd 0 → the PRIMARY monitor. Do NOT pass a null device name to
            // EnumDisplaySettingsW as "primary": despite the docs, that call
            // returns FALSE on multi-adapter Win11, which
            // silently degraded every probe-less caller to the 60 fps fallback
            // — the 3D loop then capped at ~63 fps on a 280 Hz panel.
            // MonitorFromWindow(0, MONITOR_DEFAULTTOPRIMARY) resolves the real
            // primary device name instead.
            var monitor = hwnd != 0
                ? MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */)
                : MonitorFromWindow(0, 1 /* MONITOR_DEFAULTTOPRIMARY */);
            if (monitor == 0) return fallback;

            var info = new MONITORINFOEXW { cbSize = Marshal.SizeOf<MONITORINFOEXW>() };
            if (!GetMonitorInfoW(monitor, ref info) || string.IsNullOrEmpty(info.szDevice))
                return fallback;

            var mode = new DEVMODEW { dmSize = (ushort)Marshal.SizeOf<DEVMODEW>() };
            if (EnumDisplaySettingsW(info.szDevice, EnumCurrentSettings, ref mode)
                && mode.dmDisplayFrequency is > 1)
            {
                return Math.Clamp((int)mode.dmDisplayFrequency, 24, 480);
            }
        }
        catch
        {
            // Any interop failure degrades to the fallback below.
        }

        return fallback;
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(nint hMonitor, ref MONITORINFOEXW info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(string? deviceName, int modeNum, ref DEVMODEW devMode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEXW
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODEW
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }
}
