using Avalonia;

namespace DotCharter.UI.Platform;

/// <summary>
///     Maps the appconfig <c>"Win32CompositionMode"</c> string onto the Avalonia
///     <see cref="Win32CompositionMode" /> preference list handed to
///     <c>Win32PlatformOptions.CompositionMode</c>.
///     <para>
///         <b>The default MUST stay a transparency-capable mode.</b>
///         <see cref="Win32CompositionMode.LowLatencyDxgiSwapChain" /> presents every
///         top-level HWND through <c>CreateSwapChainForHwnd</c> with
///         <c>DXGI_ALPHA_MODE_IGNORE</c> (Avalonia
///         <c>Win32.DirectX.DxgiRenderTarget</c>), and its surface factory reports
///         <c>RequiresNoRedirectionBitmap == false</c>, which makes
///         <c>WindowImpl._defaultTransparencyLevel</c> resolve to
///         <see cref="Avalonia.Controls.WindowTransparencyLevel.None" />. Avalonia's own
///         remarks on the enum member say the mode is only recommended "if there is no
///         need for the transparency ... offered by WinUIComposition".
///     </para>
///     <para>
///         That cost is NOT limited to acrylic/blur window effects — it applies to every
///         POPUP window too, and Avalonia hosts ComboBox dropdowns, flyouts, menus and
///         tooltips in their own <c>PopupImpl</c> HWNDs whenever
///         <c>Win32PlatformOptions.OverlayPopups</c> is off — it is always on, since the
///         3D preview is composed (with a native Vulkan 3D HWND the default would have to be off,
///         because an in-window popup rendered behind it — the
///         airspace rule in <c>DotCharter.UI/AGENTS.md</c>). Under an opaque popup host
///         every pixel the theme leaves transparent — the gutter outside a rounded
///         <c>FlyoutPresenter</c>/<c>MenuFlyoutPresenter</c>, the ComboBox popup border's
///         margin, the area a <c>BoxShadow</c> feathers into — is presented as flat
///         BLACK instead of blending with what is behind the popup.
///     </para>
///     <para>
///         This project has paid for that twice: <c>b9592ce5</c> ("ToolTip … black halo")
///         removed the same setting, and <c>85fb67e5</c> reinstated it
///         as the default for a 2D-timeline pacing win, which brought the black halo back
///         on every dropdown and flyout. The pacing win is still available — as an
///         explicit opt-in via <see cref="LowLatencyDxgiValue" /> — but it is not the
///         default, because a black frame around every dropdown is a worse defect than a
///         100 fps (rather than 160-200 fps) timeline.
///     </para>
/// </summary>
internal static class Win32CompositionModeSelector
{
    /// <summary>
    ///     appconfig <c>"Win32CompositionMode"</c> value that opts into the low-latency
    ///     DXGI swapchain. Trades transparent popup hosts for render-timer pacing.
    /// </summary>
    public const string LowLatencyDxgiValue = "LowLatencyDxgiSwapChain";

    /// <summary>
    ///     appconfig <c>"Win32CompositionMode"</c> value that names the default
    ///     explicitly. Accepted so configs written before the default flipped keep
    ///     selecting the mode they asked for.
    /// </summary>
    public const string WinUiCompositionValue = "WinUIComposition";

    /// <summary>
    ///     Universal tail every preference list ends with, so an unsupported first
    ///     choice can never brick rendering.
    /// </summary>
    private static readonly Win32CompositionMode[] Fallbacks =
    [
        Win32CompositionMode.WinUIComposition,
        Win32CompositionMode.DirectComposition,
        Win32CompositionMode.RedirectionSurface
    ];

    /// <summary>
    ///     Resolves the composition-mode preference list. Anything other than
    ///     <see cref="LowLatencyDxgiValue" /> (including <see langword="null" />, blank
    ///     and unrecognised values) selects the transparency-capable default.
    /// </summary>
    public static Win32CompositionMode[] Select(string? configuredMode)
    {
        if (!string.Equals(configuredMode?.Trim(), LowLatencyDxgiValue,
                StringComparison.OrdinalIgnoreCase))
        {
            return [.. Fallbacks];
        }

        return [Win32CompositionMode.LowLatencyDxgiSwapChain, .. Fallbacks];
    }

    /// <summary>
    ///     True when the resolved list would give every top-level (and therefore every
    ///     popup) an opaque host — i.e. the theme's rounded/shadowed popup chrome will
    ///     render its transparent gutter as black. Used for the startup diagnostic.
    /// </summary>
    public static bool IsOpaquePopupHost(IReadOnlyList<Win32CompositionMode> modes)
        => modes.Count > 0 && modes[0] == Win32CompositionMode.LowLatencyDxgiSwapChain;
}
