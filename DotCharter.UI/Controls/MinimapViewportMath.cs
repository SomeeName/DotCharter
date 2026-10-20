namespace DotCharter.UI.Controls;

/// <summary>
///     The view-window rectangle the song map draws over itself — pure, so the
///     one number it gets wrong is assertable without a visual tree.
///     <para>
///         <b>The visible duration belongs to the TIMELINE, not to the map.</b>
///         <c>MinimapOverviewControl.DrawViewport</c> derived it as
///         <c>minimapWidth / PixelsPerMs</c>, which is only the same number
///         while the two controls happen to be the same width — and they are
///         not: the song map strip spends its first 118 px on the SONG MAP
///         caption cell (<c>MainWindow.axaml</c>), so the box was drawn
///         <c>118 / PixelsPerMs</c> ms too NARROW at every zoom, and grew
///         relatively worse the more the window was shrunk. Any second host
///         (a docked or floating map, a narrower Fire panel) makes it worse
///         again. <c>ChartRenderer</c> already publishes the real width to
///         <c>EditorUiState.TimelineViewportWidthPx</c> for exactly this class
///         of question; this is the second reader.
///     </para>
/// </summary>
internal static class MinimapViewportMath
{
    /// <summary>
    ///     Which width the view-window box must be measured in: an explicitly
    ///     plumbed timeline width wins, then the width
    ///     <c>ChartRenderer</c> last published, and only then the map's own
    ///     width (a detached control with no timeline behind it — the old
    ///     behaviour, kept as the last resort rather than as the rule).
    /// </summary>
    public static double ResolveTimelineWidth(double explicitWidth, double publishedWidth, double minimapWidth)
    {
        if (explicitWidth > 0) return explicitWidth;
        if (publishedWidth > 0) return publishedWidth;
        return minimapWidth;
    }

    /// <summary>
    ///     The view-window box in MAP pixels. <paramref name="scale" /> is the
    ///     map's px-per-ms (its own width over the song length);
    ///     <paramref name="pixelsPerMs" /> is the TIMELINE's zoom.
    /// </summary>
    public static (double X, double Width) ViewportBox(
        double viewAnchorMs,
        double playheadX,
        double pixelsPerMs,
        double timelineWidthPx,
        double scale)
    {
        var zoom = pixelsPerMs > 0 ? pixelsPerMs : 0.2;
        var viewStartMs = viewAnchorMs - playheadX / zoom;
        var visibleDurationMs = timelineWidthPx / zoom;
        return (viewStartMs * scale, Math.Max(4, visibleDurationMs * scale));
    }

    /// <summary>
    ///     The song map's cached static layer, in DEVICE pixels: its logical size times the window's
    ///     render scaling. Building it at the logical size and 96 DPI and then drawing it stretched to the
    ///     screen's scaling would blur every section name, note tick and waveform line
    ///     in the map on a scaled display.
    /// </summary>
    public static (int Width, int Height) StaticLayerPixels(double width, double height, double renderScaling)
    {
        var s = renderScaling > 0 && double.IsFinite(renderScaling) ? renderScaling : 1.0;
        return (Math.Max(1, (int)Math.Ceiling(width * s)), Math.Max(1, (int)Math.Ceiling(height * s)));
    }

    /// <summary>
    ///     <paramref name="logical" /> moved onto the nearest whole DEVICE pixel. The map's labels sit
    ///     at section-start × scale + 3 — a fraction of a pixel almost always — so every glyph
    ///     stem would straddle two pixel columns at half strength each,
    ///     which reads as blur and a doubled edge.
    /// </summary>
    public static double SnapToPixel(double logical, double renderScaling)
    {
        var s = renderScaling > 0 && double.IsFinite(renderScaling) ? renderScaling : 1.0;
        return Math.Round(logical * s) / s;
    }
}
