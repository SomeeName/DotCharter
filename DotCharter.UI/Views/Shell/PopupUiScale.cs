using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.VisualTree;
using DotCharter.UI.Services;

namespace DotCharter.UI.Views.Shell;

/// <summary>
///     <b>The interface scale for popups Avalonia does not hand one to.</b>
///
///     <para>
///         The global scale is a <c>LayoutTransformControl</c> around the shell
///         (<see cref="WindowUiScale.ScalerName" />), and an overlay popup host
///         is NOT inside it: it lives in the window's <c>PopupOverlayLayer</c>,
///         a sibling of the content presenter. Avalonia 12 handles most of this
///         itself — <c>OverlayPopupHost</c> carries a <c>Transform</c> property
///         and Avalonia assigns it from the anchor's accumulated transform.
///     </para>
///
///     <para>
///         <b>It only does so for some popups.</b> Measured headlessly on
///         Avalonia 12.1.2 at a 1.5x shell scale: a
///         <c>ComboBox</c> dropdown's host gets
///         <c>Transform = {M11:1.5 M22:1.5}</c>, its content measures 1.5 and
///         it is anchored exactly. A <c>Flyout</c>, a <c>MenuFlyout</c> and a
///         <c>ToolTip</c> get <b>no transform</b> and measure <b>1.0</b> — a
///         100 % menu beside 150 % content, which is the regression Auto would
///         have shipped on the owner's 1440p monitor.
///     </para>
///
///     <para>
///         <b>A STYLE cannot fix it and that is the load-bearing detail.</b> A
///         <c>Style</c> setter on <c>OverlayPopupHost.Transform</c> was tried
///         first and does nothing, at window scope or app scope: Avalonia
///         ASSIGNS that property — including assigning nothing — as a LOCAL
///         value while the host is being positioned, and a local value beats
///         every style. So the fix has to run per instance, after positioning.
///     </para>
///
///     <para>
///         The hook is <see cref="FollowShellScaleProperty" />, an attached
///         <c>bool</c> set on every <c>Popup</c> by one style in
///         <c>Styles/Chrome.axaml</c>. On each open it finds the host above the
///         popup's child and fills in the transform <b>only if the host has
///         none</b> — so a ComboBox keeps the exact factor Avalonia computed
///         from its own anchor and only the hosts that were handed nothing fall
///         through here. Forcing the global factor on every host instead would
///         break a popup anchored inside a nested transform.
///     </para>
///
///     <para>
///         <b>ONE shared, MUTATED instance.</b> The scale can change while a
///         popup is open or after a flyout has been PREWARMED
///         (<c>MenuFlyoutPrewarmer</c> realizes flyouts at whatever scale is in
///         force, and Auto re-derives on a window resize — a maximise from a
///         small window is exactly that). Handing out a new instance per change
///         would leave an already-assigned host on the old factor for ever;
///         writing the factors in place makes every host that references it
///         follow, open or not.
///     </para>
///
///     <para>
///         <b>Known imprecision, stated rather than hidden:</b> a popup anchored
///         inside a NESTED transform (the rig's <c>RigBoardZoomHost</c>,
///         <c>RigScaleToFit</c>, the launch hero's scaler) that falls through to
///         this fallback gets the SHELL's factor, not shell x nested — 1.35
///         where 1.08 would be exact at board zoom 0.8. It was 1.0 before, so
///         this is strictly better everywhere and exact for the shell's own
///         menus, flyouts and tooltips.
///     </para>
///
///     <para>
///         <c>PopupRoot</c> — the separate-HWND path under the native-3D opt-out
///         — is NOT covered: it is a <c>WindowBase</c>, not a
///         <c>ContentControl</c>, and has no <c>Transform</c> property at all.
///     </para>
/// </summary>
public static class PopupUiScale
{
    private static readonly ScaleTransform Shared = new(1.0, 1.0);

    /// <summary>Set on every <c>Popup</c> by one style; the changed handler is
    /// the whole wiring. An attached property rather than a direct hook because
    /// there is no other way to reach every popup the framework creates.</summary>
    public static readonly AttachedProperty<bool> FollowShellScaleProperty =
        AvaloniaProperty.RegisterAttached<Popup, bool>(
            "FollowShellScale", typeof(PopupUiScale));

    /// <summary>Reads <see cref="FollowShellScaleProperty" />.</summary>
    public static bool GetFollowShellScale(Popup popup) => popup.GetValue(FollowShellScaleProperty);

    /// <summary>Writes <see cref="FollowShellScaleProperty" />.</summary>
    public static void SetFollowShellScale(Popup popup, bool value) =>
        popup.SetValue(FollowShellScaleProperty, value);

    static PopupUiScale()
    {
        Apply(UiScaleController.Scale);
        UiScaleController.ScaleChanged += Apply;
        FollowShellScaleProperty.Changed.AddClassHandler<Popup>(OnFollowChanged);
    }

    /// <summary>The shared transform. Always the same INSTANCE — its factors
    /// are written in place. Exposed for the tests, which have to prove the
    /// instance is shared rather than replaced.</summary>
    public static ScaleTransform Transform
    {
        get
        {
            Apply(UiScaleController.Scale);
            return Shared;
        }
    }

    private static void OnFollowChanged(Popup popup, AvaloniaPropertyChangedEventArgs e)
    {
        popup.Opened -= OnPopupOpened;
        if (e.GetNewValue<bool>()) popup.Opened += OnPopupOpened;
    }

    private static void OnPopupOpened(object? sender, EventArgs e)
    {
        if (sender is not Popup popup) return;
        // The host is the popup child's visual ancestor. Popup.Host is internal
        // in Avalonia 12, and reflecting into it would break silently on an
        // upgrade; the ancestor walk is public API and is what the headless
        // probe confirmed the shape of.
        if (popup.Child?.GetVisualAncestors().OfType<OverlayPopupHost>().FirstOrDefault() is not { } host)
            return;

        // ONLY when Avalonia handed it nothing. A host that already carries a
        // transform got it from its own anchor and is exact; overwriting it
        // with the shell factor would be wrong inside a nested transform.
        if (host.Transform is not null) return;

        Apply(UiScaleController.Scale);
        host.Transform = Shared;
    }

    /// <summary>Writes a factor into the shared instance. A no-op for a value
    /// already in force, so a scale change that is not one does not invalidate
    /// every open popup.</summary>
    private static void Apply(double scale)
    {
        var clamped = UiScaleController.Clamp(scale);
        if (Math.Abs(Shared.ScaleX - clamped) < 0.0001) return;
        Shared.ScaleX = clamped;
        Shared.ScaleY = clamped;
    }
}
