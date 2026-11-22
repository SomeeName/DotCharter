using Avalonia;
using DotCharter.UI.Views.Shell;

namespace DotCharter.Tests.UI;

/// <summary>
///     Two shell rules that must hold once panes float in windows of their own:
///     the gameplay focus watchdog reclaims focus only from the shell's own child
///     windows (the renderer's swapchain HWND), and the highway capture is sized
///     from both screen corners, so the interface scale cannot crop it.
/// </summary>
public sealed class FloatingWindowShellPolicyTests
{
    private const nint Shell = 0x100;
    private const nint Swapchain = 0x200;
    private const nint FloatingPane = 0x300;

    [Fact]
    public void TheWatchdog_ReclaimsFocusFromTheRenderersChildWindow() =>
        Assert.True(MainWindowShellLayout.IsReclaimable(Shell, Swapchain, sameProcess: true, focusedIsShellChild: true));

    /// <summary>A floating pane is this process's own top-level, not a child of the shell: typing there must keep its focus.</summary>
    [Fact]
    public void TheWatchdog_LeavesAFloatingPaneWindowAlone() =>
        Assert.False(MainWindowShellLayout.IsReclaimable(Shell, FloatingPane, sameProcess: true, focusedIsShellChild: false));

    [Theory]
    [InlineData(0, true, true)]
    [InlineData(0x100, true, true)]
    [InlineData(0x200, false, true)]
    public void TheWatchdog_LeavesNoFocusTheShellItselfAndOtherApplicationsAlone(int focused, bool sameProcess, bool child) =>
        Assert.False(MainWindowShellLayout.IsReclaimable(Shell, focused, sameProcess, child));

    /// <summary>
    ///     At 150 % interface scale a 400 x 200 slot (in the scaler's inner units)
    ///     covers 600 x 300 screen pixels; the capture must take all of them.
    /// </summary>
    [Fact]
    public void TheHighwayCapture_SpansTheScaledSlot()
    {
        const double scale = 1.5;
        var screenOrigin = new PixelPoint(1000, 500);
        PixelPoint ToScreen(Point p) => new(screenOrigin.X + 80 + (int)Math.Round(p.X * scale), screenOrigin.Y + 60 + (int)Math.Round(p.Y * scale));

        var region = MainWindowCapture.SlotRegion(new Size(400, 200), ToScreen, screenOrigin);

        Assert.Equal(new PixelRect(80, 60, 600, 300), region);
    }

    [Fact]
    public void TheHighwayCapture_AtIdentity_IsTheSlotItself()
    {
        var region = MainWindowCapture.SlotRegion(new Size(320, 180), p => new PixelPoint(10 + (int)p.X, 20 + (int)p.Y), new PixelPoint(0, 0));

        Assert.Equal(new PixelRect(10, 20, 320, 180), region);
    }
}
