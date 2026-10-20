namespace DotCharter.UI.Controls.Layers.Helpers;

/// <summary>
///     Screen-X culling for the 2D note path.
/// </summary>
/// <remarks>
///     <para>
///         Every other 2D layer already culls — <c>BackgroundLayer</c> (bookmarks,
///         annotations, beat/measure lines, subdivisions), <c>EventLayer</c> (tones,
///         event points), <c>WaveformLayer</c> and <c>PlayheadLayer</c>. The note
///         layers did not, and the frame snapshot is built with a ±5000 ms buffer
///         (<c>RenderingConstants.VisibleRangeBufferMs</c>), so the waste ratio is
///         <c>(viewSpanMs + 10000) / viewSpanMs</c>: 2.0× at the default 0.2 px/ms,
///         6.2× at 1.0 and 11.4× at 2.0.
///     </para>
///     <para>
///         This is NOT overdraw — the viewport/scissor is set to the render extent,
///         so an off-screen quad is clipped before rasterization and costs zero
///         fill. The cost is CPU float writes into the batcher, the host-visible
///         upload and vertex processing: a Full-LOD note is ~666 floats, so at
///         1 px/ms on a 12-notes/s chart ~120 off-screen notes per frame is
///         ~320 KB of buffer writes, ~46 MB/s at 144 fps.
///     </para>
///     <para>
///         The snapshot buffer itself stays: it is load-bearing for sustains that
///         start off-screen to the left, and for the sticky-prefix consumers.
///     </para>
/// </remarks>
public static class NoteCulling
{
    /// <summary>
    ///     Slack in pixels around the viewport. Sized for the widest decoration a
    ///     sound draws outside its own <c>[x, x + width]</c> span: the bend label
    ///     box, the slide destination label and the gem's selection halo.
    /// </summary>
    public const float MarginPx = 120f;

    /// <summary>
    ///     True when nothing a sound spanning <c>[x, x + width]</c> can draw could
    ///     land inside the viewport. Callers return early; the geometry would be
    ///     scissor-clipped anyway.
    /// </summary>
    public static bool IsOffScreen(float x, float width, double viewportWidth) =>
        x + width < -MarginPx || x > viewportWidth + MarginPx;
}
