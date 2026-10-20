using Avalonia;
using Avalonia.Controls;

namespace DotCharter.UI.Platform;

/// <summary>
///     Stops Avalonia re-opening a tooltip WITHOUT ITS DELAY once one has
///     already been shown — the "hover once, then sweep the toolbar and
///     everything stutters" report.
///     <para>
///         <b>The mechanism.</b> <c>ToolTipService.OnTipControlChanged</c> runs
///         on every raw pointer MOVE that lands on a different tipped control.
///         Its delay decision is, verbatim:
///     </para>
///     <code>
///     if (betweenShowDelay >= 0 &amp;&amp; (closedPreviousTip ||
///         (DateTime.UtcNow.Ticks - _lastTipCloseTime) &lt;= betweenShowDelay * TimeSpan.TicksPerMillisecond))
///         showDelay = 0;   // Open(newValue) — SYNCHRONOUSLY, inside input processing
///     </code>
///     <para>
///         <c>closedPreviousTip</c> is true whenever the pointer leaves a control
///         whose tip is open, so from the FIRST tooltip onwards every further
///         tipped control the pointer crosses opens with no delay at all. And a
///         tooltip open is not cheap: <c>ToolTip.Open</c> sets
///         <c>Popup.IsOpen = true</c> and <c>Close</c> DISPOSES the popup's host,
///         so the host is rebuilt from scratch on every open — measured headless
///         (Debug, <c>r2r=no</c>, and on the CHEAP <c>OverlayPopupHost</c>) at
///         <b>4.0-8.7 ms and 391 KB per open/close cycle</b>. In the real app
///         <c>OverlayPopups</c> is always on (the 3D preview is composed, so the airspace rule under
///         the native 3D HWND — see <c>DotCharter.UI/AGENTS.md</c> — does not apply), so the host is the
///         overlay host rather than a <c>PopupRoot</c> (a native HWND with its own ANGLE/D3D
///         render target, created and destroyed per hover). The delay guard below
///         stands on the two measured reasons that follow. <b>Thirty</b> tipped controls sit in
///         the editor toolbar alone (four more on the menu bar, four on the
///         status rail).
///     </para>
///     <para>
///         <b>That the per-open popup HWND hitches the 2D timeline is not a
///         hypothesis — this repo measured it already.</b> See
///         <c>Program.cs</c>'s <c>OverlayPopups</c> comment: "the per-open HWND +
///         GPU-surface creation measurably hitched the 2D timeline … NOT
///         default-on: with the native Vulkan 3D swapchain an in-window popup
///         that overlaps the 3D panel renders BEHIND it (airspace)". So the
///         cure cannot be to move popups into the window — it has to be to stop
///         creating one per hover.
///     </para>
///     <para>
///         <b>"Then why not host tooltips in-window anyway?"</b> Two measured
///         reasons, beyond the airspace one. The headless test platform has no
///         popup impl, so every tooltip there is already an
///         <c>OverlayPopupHost</c> — i.e. it IS the overlay-popups
///         world, and it can be measured. (a) An overlay-hosted tooltip does not
///         track a sweep: hovering seven tipped controls in a row opened the
///         first and then only <b>2 of the next 6</b>, because the open
///         tooltip's own overlay is what the next hit test lands on, so the
///         service alternately drops and re-latches the tip. (b) It is not even
///         cheap: while one is open, each pointer move cost <b>~5 ms and
///         ~230 KB</b> net of the harness floor, because the overlay layer puts
///         that work inside the WINDOW's layout — the same UI thread the 2D
///         timeline's <c>RequestAnimationFrame</c> is queued on. Avalonia's
///         per-control <c>ToolTip.ShouldUseOverlayLayer</c> would inherit both.
///         The "in-window for everything" route is the composed 3D path —
///         the only 3D path, with <c>OverlayPopups</c> always on — and it is not
///         a substitute for this guard.
///     </para>
///     <para>
///         <b>The lever.</b> A NEGATIVE <c>BetweenShowDelay</c> is the only input
///         that makes the branch above unreachable — <c>0</c> does not, because
///         <c>closedPreviousTip</c> short-circuits the time comparison. So every
///         tooltip, first or chained, waits its own <c>ShowDelay</c>; a pointer
///         that is still travelling opens nothing, and the show timer is
///         restarted on each control change.
///     </para>
///     <para>
///         The property does not inherit and its default cannot be overridden
///         (<c>RegisterAttached&lt;ToolTip, Control, int&gt;</c> already claims
///         the metadata for <c>Control</c>, so
///         <c>OverrideDefaultValue&lt;Control&gt;</c> throws), which is why this
///         is one subscription to <c>ToolTip.TipProperty</c> — the same signal
///         Avalonia's own <c>ToolTipService</c> keys on — rather than a setter
///         repeated at ~575 markup sites.
///     </para>
/// </summary>
public static class ToolTipHoverPolicy
{
    /// <summary>
    ///     Written into <c>ToolTip.BetweenShowDelay</c>. Must stay NEGATIVE:
    ///     <c>0</c> re-enables the instant chained open through
    ///     <c>closedPreviousTip</c>.
    /// </summary>
    public const int ChainedOpenDisabled = -1;

    /// <summary>
    ///     Set to <c>1</c> to run WITHOUT the guard, i.e. with Avalonia's stock
    ///     chaining, so the stutter can be A/B'd in the real app in one sitting.
    ///     Same family as <c>DOTCHARTER_NO_SURFACE_PREWARM</c>; inert unless set.
    /// </summary>
    public const string DisableEnvVar = "DOTCHARTER_NO_TOOLTIP_HOVER_GUARD";

    private static readonly Lock Gate = new();
    private static IDisposable? _subscription;

    /// <summary>
    ///     True when this <c>BetweenShowDelay</c> still lets Avalonia open a
    ///     chained tooltip with no delay, i.e. when the guard has work to do.
    ///     Pure — this is the exact <c>betweenShowDelay &gt;= 0</c> test from
    ///     <c>ToolTipService.OnTipControlChanged</c>.
    /// </summary>
    public static bool AllowsInstantChainedOpen(int betweenShowDelay) => betweenShowDelay >= 0;

    /// <summary>Pure half of the A/B seam. Only an exact <c>"1"</c> disables the
    /// guard — an empty or stray value must never silently ship the stutter.
    /// </summary>
    public static bool IsDisabledBy(string? envValue) => envValue == "1";

    /// <summary>
    ///     Installs the guard for the process. Idempotent; call it before any
    ///     view is built (<c>App.Initialize</c>) so no tip assignment is missed.
    /// </summary>
    public static void Apply()
    {
        // A/B seam for the owner: `set DOTCHARTER_NO_TOOLTIP_HOVER_GUARD=1`
        // brings the stutter back so the two can be compared on one machine.
        if (IsDisabledBy(Environment.GetEnvironmentVariable(DisableEnvVar))) return;

        lock (Gate)
        {
            _subscription ??= ToolTip.TipProperty.Changed.AddClassHandler<Control>(
                static (control, _) => Guard(control));
        }
    }

    /// <summary>Uninstalls the class handler. <b>Tests only.</b></summary>
    /// <remarks>
    ///     <see cref="Apply" /> registers a handler on
    ///     <c>ToolTip.TipProperty.Changed</c>, which is a PROCESS-GLOBAL Avalonia
    ///     property observable: once installed it fires for every control in the
    ///     process, on whatever thread raised the change, for the life of the
    ///     process. In the app that is exactly right — it is called once from
    ///     <c>App.Initialize</c> and every tip assignment is on the UI thread. In
    ///     a test run it is a leak that outlives the class that installed it:
    ///     <c>LocalizationService.SetConfiguredCulture</c> raises
    ///     <c>PropertyChanged("Item[]")</c> from whatever xunit worker calls it, a
    ///     live <c>{localization:Loc}</c> tooltip binding re-evaluates and writes
    ///     <c>ToolTip.Tip</c>, and <see cref="Guard" />'s
    ///     <c>SetBetweenShowDelay</c> then throws
    ///     <c>InvalidOperationException: The calling thread cannot access this
    ///     object</c> — taking down every case in whichever class was writing the
    ///     culture. Measured: 19 cases of one class in one default-gate
    ///     run out of three.
    /// </remarks>
    internal static void ResetForTesting()
    {
        lock (Gate)
        {
            _subscription?.Dispose();
            _subscription = null;
        }
    }

    private static void Guard(Control control)
    {
        // A cleared tip has nothing to guard, and re-adding one guards it again.
        if (ToolTip.GetTip(control) is null) return;
        // An author who set the delay explicitly wins — including this method on
        // a second pass, which is what makes the guard idempotent per control.
        if (control.IsSet(ToolTip.BetweenShowDelayProperty)) return;
        if (!AllowsInstantChainedOpen(ToolTip.GetBetweenShowDelay(control))) return;
        ToolTip.SetBetweenShowDelay(control, ChainedOpenDisabled);
    }
}
