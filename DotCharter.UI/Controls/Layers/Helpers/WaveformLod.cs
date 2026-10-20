namespace DotCharter.UI.Controls.Layers.Helpers;

/// <summary>
///     One frame's waveform level-of-detail decision: which mipmap level the
///     layer reads, how wide one emitted column is, and whether the silhouette
///     ribbon is worth drawing.
/// </summary>
/// <param name="Level">Index into <see cref="WaveformSamples.Mipmaps" /> AND
/// <see cref="WaveformSamples.Body" /> — the two pyramids are the same shape,
/// so ONE level indexes both.</param>
/// <param name="BlockMs">Time span of one cell at <paramref name="Level" />.</param>
/// <param name="StepWidthPx">On-screen width of one emitted column.</param>
/// <param name="DrawOutline">Whether a column is wide enough that the stepped
/// silhouette needs the joined stroke ribbon to read as a curve.</param>
public readonly record struct WaveformLevelOfDetail(
    int Level,
    double BlockMs,
    float StepWidthPx,
    bool DrawOutline);

/// <summary>
///     Pure waveform LOD math, unit-tested in <c>WaveformLodTests</c>. Split
///     out of <c>WaveformLayer</c> for the same reason <see cref="NoteLod" />
///     is: it is the part of a renderer change that can be proven in the
///     default (GPU-free) gate.
/// </summary>
public static class WaveformLod
{
    /// <summary>
    ///     Target on-screen width of one emitted waveform column, in pixels.
    ///     <para>This is the knob that trades detail against per-frame batcher
    ///     calls. It only takes effect when the pyramid's base block is fine
    ///     enough: a coarse base block (say 20 ms) makes the picker bottom out at level 0 for any zoom
    ///     above ~0.125 px/ms, so the real column width would be 20 ms × zoom (4 px
    ///     at the default zoom, 40 px zoomed in) no matter what this said.</para>
    ///     <para><b>It is deliberately exactly half of
    ///     <see cref="OutlineMinStepPx" />.</b> Levels are octaves, so "the
    ///     first level at least this wide" always lands in
    ///     <c>[target, 2×target)</c> — which is <c>[1.5, 3)</c>, i.e. below the
    ///     outline threshold — and the 6-quad stroke ribbon a fat column needs
    ///     switches OFF everywhere except where the pyramid has bottomed out at
    ///     level 0. That is why 2.5× the columns of the old 2.5 px target costs
    ///     LESS at the editing zooms rather than more (measured: 0.82× at
    ///     0.2 px/ms, 0.93× at 0.05).</para>
    /// </summary>
    public const double TargetColumnPx = OutlineMinStepPx / 2.0;

    /// <summary>
    ///     Below this column width the silhouette ribbon is skipped: the two AA
    ///     strokes (3 quads and a sqrt each) overlap the fill and add ~6× their
    ///     visible value. Above it the columns are wide enough that the fill's
    ///     own staircase edge is visible and the ribbon is what makes it read as
    ///     a curve — which now only happens at the deepest zoom, where the
    ///     pyramid has bottomed out at level 0.
    /// </summary>
    public const float OutlineMinStepPx = 3.0f;

    /// <summary>
    ///     Picks the level whose cell is at least <see cref="TargetColumnPx" />
    ///     wide on screen. Returns <c>null</c> when there is nothing to draw
    ///     (no data, or a non-positive zoom).
    /// </summary>
    public static WaveformLevelOfDetail? Compute(WaveformSamples? samples, double zoom)
    {
        if (samples is not { HasData: true } || zoom <= 0) return null;

        var level = samples.PickLevel(TargetColumnPx / zoom);
        var blockMs = samples.BaseBlockMs * (1 << level);
        var stepWidthPx = (float)(blockMs * zoom);

        return new WaveformLevelOfDetail(level, blockMs, stepWidthPx,
            stepWidthPx >= OutlineMinStepPx);
    }

    /// <summary>
    ///     Stroke thickness for the silhouette ribbon at a given column width —
    ///     the historical <c>exp(0.2·step)</c> curve, capped so the deepest zoom
    ///     does not grow a heavy band that swallows the shape.
    /// </summary>
    public static float StrokeThickness(float stepWidthPx, float maxThickness)
    {
        var thickness = stepWidthPx >= 2.0f ? (float)Math.Exp(stepWidthPx * 0.2) : 1.0f;
        return thickness > maxThickness ? maxThickness : thickness;
    }
}
