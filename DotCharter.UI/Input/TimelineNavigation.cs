namespace DotCharter.UI.Input;

/// <summary>
///     Thin state-mutation helpers over <see cref="ProjectState" /> for the
///     decoupled 2D-timeline viewport (0.16 6.6). All math lives in the pure
///     <see cref="TimelineViewMath" />; these just apply it to the shared
///     state so wheel pan, keybind zoom, toolbar zoom and Ctrl+wheel zoom
///     behave identically. Pans NEVER seek the transport (a native seek
///     flushes mixer queues — see the minimap seek-throttling rationale).
/// </summary>
public static class TimelineNavigation
{
    /// <summary>
    ///     Pans the viewport by wheel notches (positive delta = wheel up =
    ///     toward earlier time). Unlocks Follow first, freezing the view at
    ///     the playhead so the transition is seamless.
    /// </summary>
    public static void PanByWheel(ProjectState ps, double deltaNotches)
    {
        ps.TimelineFollowsPlayhead = false;
        ps.TimelineViewAnchorMs = TimelineViewMath.PanByWheel(
            ps.TimelineViewAnchorMs, deltaNotches, ps.PixelsPerMs, ps.EffectiveTimelineLengthMs);
    }

    /// <summary>
    ///     Applies a zoom factor to <see cref="ProjectState.PixelsPerMs" />.
    ///     While Follow is ON this is exactly the historical behaviour (the
    ///     view stays anchored on the playhead column). While OFF the pan
    ///     anchor is recomputed so the zoom holds <paramref name="anchorScreenX" />
    ///     steady — the pointer position for Ctrl+wheel, else the viewport
    ///     center — instead of the (possibly far off-screen) playhead.
    /// </summary>
    public static void ApplyZoom(ProjectState ps, double factor, double? anchorScreenX = null)
    {
        var oldPpm = ps.PixelsPerMs;
        var newPpm = oldPpm * factor;
        if (oldPpm <= 0 || newPpm <= 0) return;

        if (!ps.TimelineFollowsPlayhead)
        {
            var screenX = anchorScreenX
                          ?? TimelineViewMath.DefaultZoomAnchorScreenX(ps.EditorUi.TimelineViewportWidthPx);
            ps.TimelineViewAnchorMs = TimelineViewMath.ZoomKeepingScreenX(
                ps.TimelineViewAnchorMs, screenX, oldPpm, newPpm, ps.EffectiveTimelineLengthMs);
        }

        ps.PixelsPerMs = newPpm;
    }
}
