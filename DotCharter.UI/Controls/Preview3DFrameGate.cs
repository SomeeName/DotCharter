using DotCharter.Core.Graphics;

namespace DotCharter.UI.Controls;

/// <summary>
///     Pure per-iteration render decision for the 3D preview's free-running
///     render thread (0.14 P6 idle gating + 0.14.2 fps cap). Mirrors the 2D
///     <see cref="ChartRenderer" /> discipline (continuous
///     RequestNextFrameRendering only while playing / interacting): when the
///     transport is stopped and neither the clock, the control size, the camera
///     easing nor any wake event reports a change, the loop parks at a short
///     poll and renders only a low trickle as a safety net for state without a
///     wake signal (settings toggles, in-place text edits). A user max-fps cap
///     (Settings → Graphics) additionally spaces out frames that WOULD render —
///     a cap wait is not an idle park: the elapsed time is a genuine frame
///     interval and must stay visible to frame timing. Kept pure (no
///     control/service deps) so the gate is unit-testable —
///     <c>Preview3DFrameGateTests</c>.
/// </summary>
internal static class Preview3DFrameGate
{
    /// <summary>What the render loop should do with this iteration.</summary>
    internal enum FrameDecision
    {
        /// <summary>Nothing changed — park at the idle poll (idle time is
        /// excluded from frame timing).</summary>
        Park,

        /// <summary>Something wants to render but the max-fps cap says it is
        /// too early — short sleep and re-check, WITHOUT consuming wake flags
        /// or treating the elapsed time as idle.</summary>
        WaitForFpsCap,

        Render
    }

    /// <summary>
    ///     How often the parked loop re-checks its wake signals. Short enough
    ///     that the first frame after a seek/edit renders with no visible lag.
    /// </summary>
    internal const int IdlePollMs = 4;

    /// <summary>
    ///     Fastest allowed idle trickle interval (10 Hz — the
    ///     <see cref="GraphicsSettingsPolicy.MaxIdleFps" /> bound): the
    ///     safety-net re-render for changes that have no wake signal wired.
    ///     Must stay ≤ 10 Hz — the whole point is not composing full frames
    ///     while nothing happens. Settings → Graphics can only lower it.
    /// </summary>
    internal const int IdleTrickleMs = 100;

    /// <summary>
    ///     How long after the last WOKEN frame (playing, a wake request, a clock
    ///     or size change, camera easing) the trickle keeps the configured rate.
    ///     Signal-less changes come out of user activity, so they land inside
    ///     this window at the configured rate.
    /// </summary>
    internal const int TrickleSettleMs = 2000;

    /// <summary>
    ///     The trickle once the loop has been parked past
    ///     <see cref="TrickleSettleMs" />: 1 Hz, the
    /// <see cref="GraphicsSettingsPolicy.MinIdleFps" /> floor. Every
    ///     trickle frame is a full present, and on the composed path Avalonia
    ///     snapshots each present into a NEW texture the size of the pane — a
    ///     paused editor at 10 Hz re-created one ~9 times a second forever.
    /// </summary>
    internal const int SettledTrickleMs = 1000 / GraphicsSettingsPolicy.MinIdleFps;

    /// <summary>
    ///     The safety-net interval: the configured rate (clamped to 1..10 Hz)
    ///     for <see cref="TrickleSettleMs" /> after the last woken frame, then
    ///     <see cref="SettledTrickleMs" /> — never faster than configured.
    /// </summary>
    internal static double TrickleIntervalMs(int idleFps, double msSinceLastWake)
    {
        var configured = 1000.0 / GraphicsSettingsPolicy.ClampIdleFps(idleFps);
        return msSinceLastWake < TrickleSettleMs ? configured : Math.Max(configured, SettledTrickleMs);
    }

    /// <summary>The trickle rate the loop is RUNNING, for the HUD — not the configured one.</summary>
    internal static int TrickleHz(int idleFps, double msSinceLastWake) =>
        (int)Math.Round(1000.0 / TrickleIntervalMs(idleFps, msSinceLastWake));

    /// <summary>
    ///     Full decision: idle gate first, then the max-fps cap.
    ///     <paramref name="maxFps" /> ≤ 0 = uncapped; positive values arrive
    ///     pre-floored by <see cref="GraphicsSettingsPolicy.ClampMaxFps" />
    ///     (≥ 15), which keeps every cap interval shorter than the slowest
    ///     trickle interval — the cap can therefore never starve the trickle.
    ///     <paramref name="idleFps" /> is clamped to 1..10 Hz;
    ///     <paramref name="msSinceLastWake" /> relaxes it to 1 Hz once the loop
    ///     has been parked past <see cref="TrickleSettleMs" />
    ///     (<see cref="TrickleIntervalMs" />). 0 keeps the configured rate.
    /// </summary>
    internal static FrameDecision Decide(
        bool isPlaying,
        bool renderRequested,
        bool timeChanged,
        bool sizeChanged,
        bool cameraAnimating,
        double msSinceLastRender,
        int maxFps,
        int idleFps = GraphicsSettingsPolicy.DefaultIdleFps,
        double msSinceLastWake = 0) =>
        Decide(IsWake(isPlaying, renderRequested, timeChanged, sizeChanged, cameraAnimating),
            msSinceLastRender, maxFps, idleFps, msSinceLastWake);

    /// <summary>
    ///     The same decision over a precomputed <see cref="IsWake" />: the render
    ///     loop needs that bool twice — here, and to restart its settle clock on a
    ///     woken frame only.
    /// </summary>
    internal static FrameDecision Decide(bool woke, double msSinceLastRender, int maxFps, int idleFps,
        double msSinceLastWake)
    {
        if (!woke && msSinceLastRender < TrickleIntervalMs(idleFps, msSinceLastWake)) return FrameDecision.Park;

        var capped = GraphicsSettingsPolicy.ClampMaxFps(maxFps);
        if (capped > 0 && msSinceLastRender < 1000.0 / capped) return FrameDecision.WaitForFpsCap;

        return FrameDecision.Render;
    }

    /// <summary>
    ///     A reason to render other than the trickle. The driver restarts its
    ///     settle clock on a frame that rendered for one of these, so the
    ///     trickle's own frames never keep it at the configured rate.
    /// </summary>
    internal static bool IsWake(bool isPlaying, bool renderRequested, bool timeChanged, bool sizeChanged,
        bool cameraAnimating) =>
        isPlaying || renderRequested || timeChanged || sizeChanged || cameraAnimating;

    /// <summary>How long a cap-blocked iteration still has to wait. Only
    /// meaningful after <see cref="FrameDecision.WaitForFpsCap" />.</summary>
    internal static double MsUntilNextCappedFrame(double msSinceLastRender, int maxFps)
    {
        var capped = GraphicsSettingsPolicy.ClampMaxFps(maxFps);
        if (capped <= 0) return 0;
        return Math.Max(0, 1000.0 / capped - msSinceLastRender);
    }

    /// <summary>Uncapped decision at the default 10 Hz trickle — the original
    /// P6 gate. Kept as the simple entry point for the base invariants.</summary>
    internal static bool ShouldRender(
        bool isPlaying,
        bool renderRequested,
        bool timeChanged,
        bool sizeChanged,
        bool cameraAnimating,
        double msSinceLastRender)
    {
        return Decide(isPlaying, renderRequested, timeChanged, sizeChanged, cameraAnimating,
            msSinceLastRender, 0) == FrameDecision.Render;
    }
}
