namespace DotCharter.UI.Services;

/// <summary>
///     Process-wide source of truth for the global UI scale (an accessibility
///     setting). The shell (`MainWindow`) subscribes and applies the value as a
///     <c>LayoutTransform</c> on its root; Settings pushes live previews here.
///     Mirrors the static-with-event pattern used by <c>ChartTheme</c> so the
///     shell and the settings view model stay decoupled.
///     <para>
///         The setting has two ARMS. <b>Manual</b> is the original
///         behaviour: a factor the user picked, persisted in
///         <c>AppConfig.UiScale</c>. <b>Auto</b> (<see cref="IsAuto" />,
///         persisted in <c>AppConfig.UiScaleAuto</c> and the DEFAULT) derives
///         the factor from the shell's client height through
///         <see cref="AutoUiScale" />. Either way <see cref="Scale" /> is the
///         one applied number every consumer reads.
///     </para>
/// </summary>
public static class UiScaleController
{
    // Kept in step with the settings row's range. Below ~0.75 chrome starts
    // clipping; above ~2.0 the shell does not fit typical displays. Auto uses
    // a NARROWER floor of 1.0 — see AutoUiScale.MinScale.
    /// <summary>The smallest factor the MANUAL arm accepts.</summary>
    public const double MinScale = 0.75;

    /// <summary>The largest factor either arm accepts.</summary>
    public const double MaxScale = 2.0;

    /// <summary>The applied factor. 1.0 is a true no-op (the shell leaves its
    /// layout transform null).</summary>
    public static double Scale { get; private set; } = 1.0;

    /// <summary>
    ///     True while the scale is DERIVED from the shell window's client
    ///     height rather than picked by hand.
    ///     <para>
    ///         This is a MODE, not a scale. <see cref="Scale" /> stays the
    ///         applied factor under both arms, which is what lets every
    ///         consumer — the shell's transform, the two Vulkan surfaces' pixel
    ///         sizing, the settings readout — go on reading one number.
    ///     </para>
    /// </summary>
    public static bool IsAuto { get; private set; }

    /// <summary>Raised on the caller's thread (always the UI thread in practice)
    /// with the new clamped scale whenever it actually changes.</summary>
    public static event Action<double>? ScaleChanged;

    /// <summary>Raised when <see cref="IsAuto" /> flips, so a second door onto
    /// the setting (Play Mode's gameplay modal) moves the Settings row's choice
    /// with it. Never raised for a no-op.</summary>
    public static event Action<bool>? AutoModeChanged;

    /// <summary>
    /// Drops every subscriber, for <c>App.ReleaseForIsolatedTeardown</c> (test
    /// hosts that boot an app per dispatch). A <c>MainWindow</c> subscribes in its
    /// CTOR, so one a case built but never showed is in no window list and would
    /// root its whole tree here for the rest of the process.
    /// </summary>
    internal static void DropSubscribersForIsolatedTeardown()
    {
        ScaleChanged = null;
        AutoModeChanged = null;
    }

    /// <summary>Bounds a MANUAL factor. Non-finite answers 1.0 rather than
    /// propagating a NaN into a layout transform.</summary>
    public static double Clamp(double scale) => Math.Clamp(double.IsFinite(scale) ? scale : 1.0, MinScale, MaxScale);

    /// <summary>Applies a factor without touching the mode. This is the raw
    /// channel — a LOAD or a live preview of an already-decided value. A user
    /// GESTURE goes through <see cref="SetManual" />, which also leaves
    /// Auto.</summary>
    public static void Set(double scale)
    {
        var clamped = Clamp(scale);
        if (Math.Abs(clamped - Scale) < 0.0001) return;
        Scale = clamped;
        ScaleChanged?.Invoke(clamped);
    }

    /// <summary>
    ///     A factor the user picked: applies it AND leaves Auto.
    ///     <para>
    ///         Separate from <see cref="Set" /> because the two callers are not
    ///         the same event. Settings' and Play Mode's load paths push the
    ///         persisted value through <see cref="Set" /> and must not thereby
    ///         cancel the mode that value was persisted under; a slider drag
    ///         must.
    ///     </para>
    /// </summary>
    public static void SetManual(double scale)
    {
        SetAutoMode(false);
        Set(scale);
    }

    /// <summary>Switches the mode. Turning Auto ON does not itself change
    /// <see cref="Scale" /> — the shell answers <see cref="AutoModeChanged" />
    /// by re-deriving from its current client height, which is the only place
    /// that knows it.</summary>
    public static void SetAutoMode(bool auto)
    {
        if (IsAuto == auto) return;
        IsAuto = auto;
        AutoModeChanged?.Invoke(auto);
    }

    /// <summary>
    ///     The shell's size-changed / first-layout hook. A no-op unless
    ///     <see cref="IsAuto" />.
    ///     <para>
    ///         <b>This cannot feed back.</b> The derived factor moves the
    ///         shell's layout TRANSFORM, which changes the size of the content
    ///         INSIDE the window; the window's own client size is untouched, so
    ///         applying a scale never produces the size-changed event that
    ///         would re-enter here. The hysteresis in
    ///         <see cref="AutoUiScale.Next" /> is about a user dragging the
    ///         window edge across a step boundary, not about a loop.
    ///     </para>
    /// </summary>
    public static void ApplyAutoForHeight(double clientHeightDip)
    {
        if (!IsAuto) return;
        Set(AutoUiScale.Next(clientHeightDip, Scale));
    }
}
