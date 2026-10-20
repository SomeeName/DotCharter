using DotCharter.Rendering.Diagnostics;

namespace DotCharter.UI.Diagnostics;

/// <summary>
///     Read-only publication point for the two renderers' most recent frame-time
///     summaries, so the Session Health dashboard can surface them without
///     reaching into the live control instances (which own their
///     <see cref="FrameTimeRing" /> on their render thread). Each renderer
///     already <c>Summarize()</c>s ~1 Hz for its HUD; it publishes that same
///     value here in the same step. Writes are last-writer-wins struct
///     assignments from the render thread; the dashboard reads a snapshot — no
///     locking needed for a diagnostic display.
/// </summary>
public static class RenderDiagnostics
{
    // ---- diagnostics-overlay gate ------------------------------------------
    // The 2D timeline's corner "FPS: NNN" readout is a diagnostic, not chart
    // content, so normal users never see it: the renderer draws it only while
    // a Session Health dashboard window is open (it's non-modal and read-only,
    // so having it open == the user is actively watching diagnostics). This
    // mirrors how the 3D render-stats line is gated on the detection monitor.
    // Ref-counted: written on the UI thread from the window's open/close,
    // read per frame on the render thread.
    private static int _dashboardOpenCount;

    /// <summary>True while at least one Session Health dashboard window is
    /// open. Gates the 2D timeline's FPS readout.</summary>
    public static bool DashboardVisible => System.Threading.Volatile.Read(ref _dashboardOpenCount) > 0;

    /// <summary>Session Health window opened (call from the UI thread).</summary>
    public static void DashboardOpened() => System.Threading.Interlocked.Increment(ref _dashboardOpenCount);

    /// <summary>Session Health window closed (call from the UI thread).</summary>
    public static void DashboardClosed() => System.Threading.Interlocked.Decrement(ref _dashboardOpenCount);

    /// <summary>Latest 2D timeline (<c>ChartRenderer</c>) frame summary.</summary>
    public static FrameTimeSummary Timeline2D { get; private set; }

    /// <summary>Latest 3D preview (<c>Preview3DComposedCanvas</c>) frame summary.</summary>
    public static FrameTimeSummary Preview3D { get; private set; }

    /// <summary>Composed-path present latency (0.16): frame-submit →
    /// composition-update-completed, ~1 Hz summaries from the present pump.
    /// All-zero while the native path is active (it has no equivalent
    /// signal — present completion is invisible behind vkQueuePresentKHR).</summary>
    public static FrameTimeSummary Preview3DPresentLatency { get; private set; }

    public static void PublishTimeline2D(FrameTimeSummary summary) => Timeline2D = summary;

    public static void PublishPreview3D(FrameTimeSummary summary) => Preview3D = summary;

    public static void PublishPreview3DPresentLatency(FrameTimeSummary summary) => Preview3DPresentLatency = summary;

    /// <summary>Composed-path present-INTERVAL distribution + stddev (0.16):
    /// the time between consecutive composition-update completions. A
    /// constant latency is compensated by the led render clock
    /// (<c>VisualLeadTiming</c>) — interval JITTER is the metric that
    /// is not compensable and must stay low.</summary>
    public static FrameTimeSummary Preview3DPresentInterval { get; private set; }

    public static double Preview3DPresentIntervalStdDevMs { get; private set; }

    public static void PublishPreview3DPresentInterval(FrameTimeSummary summary, double stdDevMs)
    {
        Preview3DPresentInterval = summary;
        Preview3DPresentIntervalStdDevMs = stdDevMs;
    }
}
