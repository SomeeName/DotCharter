using DotCharter.UI.Constants;

namespace DotCharter.UI.Input;

/// <summary>
///     Pure viewport math for the decoupled 2D timeline (0.16 6.6). The view
///     is described by ONE anchor value: the chart time (ms) displayed at the
///     fixed anchor column <see cref="ChartEditorConstants.PlayheadX" />.
///     While Follow is ON that anchor IS the playhead time (pixel-identical to
///     the historical locked viewport); while Follow is OFF it is
///     <c>ProjectState.TimelineViewAnchorMs</c>, panned by wheel/middle-drag.
///     No Avalonia/service state — unit-tested in TimelineViewMathTests.
/// </summary>
public static class TimelineViewMath
{
    /// <summary>
    ///     How far the live audio clock may sit from the coalesced UI playhead
    ///     before the renderer stops trusting it. `PositionChanged` fires at up
    ///     to 250 Hz and reaches bindings coalesced at ≤60 Hz, so the UI value
    ///     ALWAYS lags a little; a bigger gap means a seek is in flight and the
    ///     UI value is the one the rest of the app agrees on.
    /// </summary>
    public const double MaxAudioClockLagMs = 50.0;

    /// <summary>
    ///     The playhead time a frame actually DRAWS: the live audio clock when
    ///     it is close enough to the coalesced UI value, plus the visual lead
    ///     while playing (the composited 2D timeline eats the same DWM commit →
    ///     scanout latency the 3D highway does).
    /// </summary>
    /// <remarks>
    ///     Pure so the hit-test side can be proven to agree with it. The renderer
    ///     publishes the result to <c>ProjectState.TimelineRenderedAnchorMs</c>
    ///     each frame while playing, and
    ///     <c>ProjectState.TimelineViewAnchorEffectiveMs</c> hands that exact
    ///     value to every hit-tester — otherwise the two differ by the
    ///     de-coalescing gap plus the lead (up to ~100 ms, i.e. 100 px at
    ///     1 px/ms) for the whole duration of playback, and clicks land on the
    ///     wrong chart time.
    /// </remarks>
    public static double ResolveRenderPlayheadMs(
        double uiTimeMs, double audioTimeMs, bool isPlaying, double visualLeadMs, double playbackSpeed = 1.0)
    {
        if (!isPlaying) return uiTimeMs;

        var exact = Math.Abs(audioTimeMs - uiTimeMs) < MaxAudioClockLagMs * SpeedFactor(playbackSpeed)
            ? audioTimeMs
            : uiTimeMs;
        // The lead is wall ms added to a song-ms clock: scale it by the speed
        // (linearly, unlike the lag budget above — a slower song genuinely
        // wants a smaller lead). See VisualLeadTiming.LeadScale.
        return exact + visualLeadMs * Controls.VisualLeadTiming.LeadScale(playbackSpeed);
    }

    /// <summary>
    ///     <see cref="MaxAudioClockLagMs" /> is a WALL-CLOCK budget (the ≤60 Hz
    ///     publish interval plus dispatcher latency) but the two clocks it
    ///     compares are SONG time, and song time runs <c>speed</c> times faster
    ///     than the wall. Applied as-is at 2× the budget is exceeded by the
    ///     ordinary de-coalescing gap every few frames, so the renderer falls
    ///     back to the stale coalesced value and the WHOLE viewport steps
    ///     back ~50 song-ms and forward again (notes,
    ///     bars and markers all jitter at 2×). Never below 1: the budget is
    ///     sized for the worst-case gap at 1×, and a slower song does not
    ///     shrink dispatcher latency.
    /// </summary>
    public static double SpeedFactor(double playbackSpeed) =>
        double.IsFinite(playbackSpeed) && playbackSpeed > 1.0 ? playbackSpeed : 1.0;

    /// <summary>
    ///     THE time↔screen anchor for a frame: the drawn playhead while Follow
    ///     is on, the pan anchor while it is off. Mirrors
    ///     <c>ProjectState.TimelineViewAnchorEffectiveMs</c>'s branch so the two
    ///     cannot drift apart.
    /// </summary>
    public static double ResolveViewAnchorMs(
        double renderPlayheadMs, bool followsPlayhead, double panAnchorMs) =>
        followsPlayhead ? renderPlayheadMs : panAnchorMs;

    /// <summary>Horizontal pan distance per mouse-wheel notch, in screen px.
    /// Pixels (not ms) so a notch moves the content the same visual distance
    /// at every zoom level.</summary>
    public const double WheelPanNotchPx = 160.0;

    /// <summary>Left edge of the visible window for a given anchor time.</summary>
    public static double ViewStartMs(double anchorMs, double pixelsPerMs) => anchorMs - ChartEditorConstants.PlayheadX / pixelsPerMs;

    /// <summary>
    ///     The left edge a 2D timeline frame DRAWS with: <see cref="ViewStartMs" />
    ///     moved by at most half a device pixel so that the frame's scroll offset,
    ///     <c>viewStart × pixelsPerMs × renderScale</c>, is a WHOLE device pixel.
    ///     <para>
    ///         Every layer places an element at <c>(time − viewStart) × zoom</c> and
    ///         the 2D pipeline rasterizes at one sample per pixel, so each element
    ///         lands on whichever pixel its own fractional edge falls on. Zoomed
    ///         out the view scrolls less than a pixel per frame, and with a
    ///         fractional origin every note crossed a pixel boundary on a DIFFERENT
    ///         frame: neighbours' spacing ticked 5 / 6 / 4 px and a regular run of
    /// notes stopped scrolling as one strip. With a whole-pixel
    ///         origin each element's sub-pixel phase is a constant of its time, so
    ///         every element moves by the same integer step each frame.
    ///     </para>
    ///     <para>
    ///         Only the DRAWING snaps. The anchor stays exact — hit-testing maps
    ///         through <c>ProjectState.TimelineViewAnchorEffectiveMs</c>, and the
    ///         two disagree by under half a device pixel.
    ///     </para>
    /// </summary>
    /// <param name="anchorMs">The exact view anchor (<see cref="ResolveViewAnchorMs" />).</param>
    /// <param name="pixelsPerMs">The zoom, in layout units per ms.</param>
    /// <param name="renderScale">Device pixels per layout unit (<c>VisualPixelScale.For</c>).</param>
    public static double SnappedViewStartMs(double anchorMs, double pixelsPerMs, double renderScale)
    {
        var exact = ViewStartMs(anchorMs, pixelsPerMs);
        var devicePxPerMs = pixelsPerMs * renderScale;
        if (!double.IsFinite(exact) || !double.IsFinite(devicePxPerMs) || devicePxPerMs <= 0) return exact;
        return Math.Round(exact * devicePxPerMs) / devicePxPerMs;
    }

    /// <summary>
    ///     Clamps a pan anchor to the chart bounds. With no known length
    ///     (<paramref name="totalLengthMs" /> &lt;= 0) only the lower bound
    ///     applies — panning before t=0 is never useful.
    /// </summary>
    public static double ClampAnchorMs(double anchorMs, double totalLengthMs)
    {
        return totalLengthMs > 0
            ? Math.Clamp(anchorMs, 0, totalLengthMs)
            : Math.Max(0, anchorMs);
    }

    /// <summary>
    ///     Pans the anchor by a screen-pixel delta (positive = show later
    ///     content), clamped to the chart bounds.
    /// </summary>
    public static double PanByPixels(double anchorMs, double deltaPx, double pixelsPerMs, double totalLengthMs)
    {
        if (pixelsPerMs <= 0) return anchorMs;
        return ClampAnchorMs(anchorMs + deltaPx / pixelsPerMs, totalLengthMs);
    }

    /// <summary>
    ///     Pans by wheel notches. Wheel up (positive delta) scrolls toward
    ///     earlier time, matching horizontal-scroll convention.
    /// </summary>
    public static double PanByWheel(double anchorMs, double deltaNotches, double pixelsPerMs, double totalLengthMs) => PanByPixels(anchorMs, -deltaNotches * WheelPanNotchPx, pixelsPerMs, totalLengthMs);

    /// <summary>
    ///     Recomputes the anchor for a zoom change so the chart time under
    ///     <paramref name="screenX" /> stays put (zoom "around" that pixel).
    ///     With <paramref name="screenX" /> at the anchor column itself this
    ///     is an exact no-op.
    /// </summary>
    public static double ZoomKeepingScreenX(double anchorMs, double screenX, double oldPixelsPerMs,
        double newPixelsPerMs, double totalLengthMs)
    {
        if (oldPixelsPerMs <= 0 || newPixelsPerMs <= 0) return anchorMs;
        var offsetPx = screenX - ChartEditorConstants.PlayheadX;
        var timeUnderPointerMs = anchorMs + offsetPx / oldPixelsPerMs;
        return ClampAnchorMs(timeUnderPointerMs - offsetPx / newPixelsPerMs, totalLengthMs);
    }

    /// <summary>
    ///     Default zoom-anchor screen X while NOT following: the viewport
    ///     center when the width is known, else the fixed anchor column
    ///     (which is always in view).
    /// </summary>
    public static double DefaultZoomAnchorScreenX(double viewportWidthPx) => viewportWidthPx > 0 ? viewportWidthPx / 2 : ChartEditorConstants.PlayheadX;
}
