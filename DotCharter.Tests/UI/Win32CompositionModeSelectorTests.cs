using Avalonia;
using DotCharter.UI.Platform;

namespace DotCharter.Tests.UI;

/// <summary>
///     Pins the Win32 composition-mode default.
///     <para>
///         <see cref="Win32CompositionMode.LowLatencyDxgiSwapChain" /> presents every
///         top-level HWND through an opaque DXGI swapchain
///         (<c>CreateSwapChainForHwnd</c> + <c>DXGI_ALPHA_MODE_IGNORE</c>), and Avalonia
///         hosts ComboBox dropdowns / flyouts / menus / tooltips in their own popup
///         HWNDs while <c>OverlayPopups</c> is off. Making it the DEFAULT therefore
///         paints a black frame around every rounded, shadowed popup — a defect this
///         project shipped twice (<c>b9592ce5</c> removed it, <c>85fb67e5</c> put it
///         back). These tests are the guard against a third time.
///     </para>
/// </summary>
public class Win32CompositionModeSelectorTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("WinUIComposition")]
    [InlineData("winuicomposition")]
    [InlineData("DirectComposition")]
    [InlineData("RedirectionSurface")]
    [InlineData("not-a-mode")]
    public void AnythingButTheExplicitOptIn_PicksATransparencyCapableModeFirst(string? configured)
    {
        var modes = Win32CompositionModeSelector.Select(configured);

        Assert.Equal(Win32CompositionMode.WinUIComposition, modes[0]);
        Assert.DoesNotContain(Win32CompositionMode.LowLatencyDxgiSwapChain, modes);
        Assert.False(Win32CompositionModeSelector.IsOpaquePopupHost(modes));
    }

    [Theory]
    [InlineData("LowLatencyDxgiSwapChain")]
    [InlineData("lowlatencydxgiswapchain")]
    [InlineData("  LowLatencyDxgiSwapChain  ")]
    public void TheExplicitOptIn_PutsTheDxgiSwapchainFirst(string configured)
    {
        var modes = Win32CompositionModeSelector.Select(configured);

        Assert.Equal(Win32CompositionMode.LowLatencyDxgiSwapChain, modes[0]);
        Assert.True(Win32CompositionModeSelector.IsOpaquePopupHost(modes));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("LowLatencyDxgiSwapChain")]
    public void EveryListEndsInTheUniversalFallbacks(string? configured)
    {
        var modes = Win32CompositionModeSelector.Select(configured);

        // An unsupported first choice must never brick rendering.
        Assert.Equal(
            [
                Win32CompositionMode.WinUIComposition,
                Win32CompositionMode.DirectComposition,
                Win32CompositionMode.RedirectionSurface
            ],
            modes[^3..]);
    }

    [Fact]
    public void SelectReturnsAFreshArray_SoACallerCannotMutateTheSharedFallbacks()
    {
        var first = Win32CompositionModeSelector.Select(null);
        first[0] = Win32CompositionMode.RedirectionSurface;

        Assert.Equal(Win32CompositionMode.WinUIComposition,
            Win32CompositionModeSelector.Select(null)[0]);
    }
}
