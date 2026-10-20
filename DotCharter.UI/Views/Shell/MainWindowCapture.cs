// The shell's PrintWindow self-capture: the pause/fail backdrop and the
// one-key highway screenshot.
//
// PrintWindow (PW_RENDERFULLCONTENT) because it reads back the DWM-composed
// window INCLUDING the composed 3D preview's GPU-interop surface, which
// Avalonia's own render-to-bitmap does not draw. Owned by MainWindow,
// constructed in its ctor.

using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace DotCharter.UI.Views.Shell;

internal sealed class MainWindowCapture(MainWindow window)
{
    private readonly MainWindow _window = window;

    // ---- Pause backdrop capture ------------------------------------------
    // Runs once per pause (~15 MB, freed on resume), never during playback:
    // the pause menu is a different tab, so this still IS the highway behind
    // it.

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hWnd, out Win32Rect rect);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(nint hWnd, nint hdc, uint flags);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hWnd, nint hdc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(nint hdc);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint hdc, nint obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint obj);

    [DllImport("gdi32.dll")]
    private static extern nint CreateDIBSection(nint hdc, ref BitmapInfo bmi, uint usage,
        out nint bits, nint section, uint offset);

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
        // no color table for 32bpp BI_RGB
    }

    internal Bitmap? CaptureSelfBitmap()
    {
        try
        {
            var hwnd = _window.TryGetPlatformHandle()?.Handle ?? 0;
            if (hwnd == 0) return null;
            if (!GetWindowRect(hwnd, out var r)) return null;
            var w = r.Right - r.Left;
            var h = r.Bottom - r.Top;
            if (w <= 0 || h <= 0) return null;

            // Crop the window title row off the top — the REAL title bar
            // still renders above the pause overlay, so keeping it in the
            // backdrop showed a duplicated bar.
            var cropTop = Math.Min(h - 1, (int)Math.Round(40 * _window.RenderScaling));
            return CaptureWindowRegion(0, cropTop, w, h - cropTop);
        }
        catch
        {
            // A missing backdrop only costs the pause menu its pretty
            // background — never the pause itself.
            return null;
        }
    }

    /// <summary>PrintWindow capture of the 3D highway slot (v0.32 screenshot
    /// key). Null when the window/slot isn't ready.</summary>
    internal WriteableBitmap? CaptureHighwaySlotBitmap()
    {
        try
        {
            var hwnd = _window.TryGetPlatformHandle()?.Handle ?? 0;
            if (hwnd == 0 || !GetWindowRect(hwnd, out var wr)) return null;
            var origin = VisualExtensions.PointToScreen(
                _window.Preview3DSlot, new Point(0, 0));
            return CaptureWindowRegion(
                origin.X - wr.Left,
                origin.Y - wr.Top,
                (int)Math.Round(_window.Preview3DSlot.Bounds.Width * _window.RenderScaling),
                (int)Math.Round(_window.Preview3DSlot.Bounds.Height * _window.RenderScaling));
        }
        catch
        {
            return null; // a missed capture only costs the screenshot
        }
    }

    /// <summary>PrintWindow self-capture of a sub-rect of this window (physical
    /// pixels, window-relative) — see the file header for why not Avalonia's
    /// render-to-bitmap.</summary>
    private WriteableBitmap? CaptureWindowRegion(
        int srcX, int srcY, int regionW, int regionH)
    {
        var hwnd = _window.TryGetPlatformHandle()?.Handle ?? 0;
        if (hwnd == 0) return null;
        if (!GetWindowRect(hwnd, out var r)) return null;
        var w = r.Right - r.Left;
        var h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0) return null;

        // Clamp the requested region to the window surface.
        srcX = Math.Clamp(srcX, 0, w - 1);
        srcY = Math.Clamp(srcY, 0, h - 1);
        regionW = Math.Clamp(regionW, 1, w - srcX);
        regionH = Math.Clamp(regionH, 1, h - srcY);

        var screenDc = GetDC(0);
        var memDc = CreateCompatibleDC(screenDc);
        var bmi = new BitmapInfo
        {
            Size = Marshal.SizeOf<BitmapInfo>(),
            Width = w,
            Height = -h, // top-down
            Planes = 1,
            BitCount = 32
        };
        var dib = CreateDIBSection(memDc, ref bmi, 0 /* DIB_RGB_COLORS */, out var bits, 0, 0);
        if (dib == 0)
        {
            DeleteDC(memDc);
            ReleaseDC(0, screenDc);
            return null;
        }

        var old = SelectObject(memDc, dib);
        var ok = PrintWindow(hwnd, memDc, 2 /* PW_RENDERFULLCONTENT */);
        WriteableBitmap? wb = null;
        if (ok)
        {
            wb = new WriteableBitmap(
                new PixelSize(regionW, regionH), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Opaque);
            using var fb = wb.Lock();
            var srcStride = w * 4;
            var rowBytes = regionW * 4;
            for (var y = 0; y < regionH; y++)
            {
                unsafe
                {
                    System.Buffer.MemoryCopy(
                        (void*)(bits + (y + srcY) * srcStride + srcX * 4),
                        (void*)(fb.Address + y * fb.RowBytes),
                        fb.RowBytes, rowBytes);
                }
            }
        }

        SelectObject(memDc, old);
        DeleteObject(dib);
        DeleteDC(memDc);
        ReleaseDC(0, screenDc);
        return wb;
    }
}
