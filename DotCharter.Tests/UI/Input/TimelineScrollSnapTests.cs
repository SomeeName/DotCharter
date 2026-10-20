using DotCharter.Rendering.Timeline2D;
using DotCharter.Tone.Audio.Abstractions;
using DotCharter.UI.Constants;
using DotCharter.UI.Controls.Layers;
using DotCharter.UI.Controls.Layers.Helpers;
using DotCharter.UI.Input;

namespace DotCharter.Tests.UI.Input;

/// <summary>
///     #113: zoomed out, the 2D timeline scrolls less than a device pixel per
///     frame, and with a fractional scroll origin every element snapped to its
///     own pixel on its own frame, so a regular run of notes ticked apart
///     (5 / 6 / 4 px gaps) instead of scrolling as one strip. The drawn view
///     start now snaps to a whole device pixel
///     (<see cref="TimelineViewMath.SnappedViewStartMs" />); these are the pure
///     halves of that guarantee. The GPU half — the real rasterized frame — is
///     <c>Rendering/Headless/HeadlessTimelineScrollRigidityTests</c>.
/// </summary>
public class TimelineScrollSnapTests
{
    private const double FrameMs = 1000.0 / 60.0;

    [Theory]
    [InlineData(0.035, 1.0)]
    [InlineData(0.035, 1.25)]
    [InlineData(0.5, 1.5)]
    [InlineData(1.7, 2.0)]
    public void TheSnappedOrigin_IsAWholeDevicePixel_WithinHalfAPixelOfTheExactOne(double zoom, double scale)
    {
        for (var anchor = 0.0; anchor < 60_000; anchor += 123.456)
        {
            var exact = TimelineViewMath.ViewStartMs(anchor, zoom);
            var snapped = TimelineViewMath.SnappedViewStartMs(anchor, zoom, scale);

            var originPx = snapped * zoom * scale;
            Assert.Equal(Math.Round(originPx), originPx, 6);
            Assert.True(Math.Abs((snapped - exact) * zoom * scale) <= 0.5 + 1e-9,
                $"anchor {anchor}: the snap moved the view {(snapped - exact) * zoom * scale:F3} device px");
        }
    }

    [Fact]
    public void TheAnchor_StaysExact_OnlyTheDrawingSnaps()
    {
        // Hit-testing maps a pointer through the EXACT anchor
        // (ProjectState.TimelineViewAnchorEffectiveMs → ViewStartMs). The snap is a
        // drawing-only adjustment, so the time under a pointer at the playhead
        // column is still exactly the anchor, and a drawn element sits within half
        // a device pixel of where that mapping puts it.
        const double zoom = 0.035;
        const double anchor = 12_345.678;
        var exactStart = TimelineViewMath.ViewStartMs(anchor, zoom);
        Assert.Equal(anchor, exactStart + ChartEditorConstants.PlayheadX / zoom, 9);

        var snappedStart = TimelineViewMath.SnappedViewStartMs(anchor, zoom, 1.0);
        const double noteMs = 13_000;
        var drawnX = (noteMs - snappedStart) * zoom;
        var mappedX = (noteMs - exactStart) * zoom;
        Assert.True(Math.Abs(drawnX - mappedX) <= 0.5 + 1e-9);
    }

    [Theory]
    [InlineData(0.0, 1.0)]
    [InlineData(-1.0, 1.0)]
    [InlineData(0.5, 0.0)]
    [InlineData(double.NaN, 1.0)]
    [InlineData(0.5, double.PositiveInfinity)]
    public void DegenerateInputs_FallBackToTheExactStart(double zoom, double scale)
    {
        var exact = TimelineViewMath.ViewStartMs(5_000, zoom);
        var snapped = TimelineViewMath.SnappedViewStartMs(5_000, zoom, scale);
        Assert.Equal(exact, snapped);
    }

    /// <summary>
    ///     Pixel-rounding stands in for the one-sample rasterizer: each element
    ///     lands on <c>round(x)</c> device pixels. Over consecutive 60 fps frames
    ///     at the issue's zoom, every element must take the SAME whole-pixel step
    ///     each frame. The exact (unsnapped) origin is the control: it must show
    ///     mixed steps, or the fixture is not sensitive to the fault.
    /// </summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void ConsecutiveSubPixelScrollSteps_MoveEveryElementByTheSameWholePixelCount(double scale)
    {
        const double zoom = 0.035;
        const double spacingMs = 60_000.0 / 97 / 4; // sixteenths at 97 BPM
        // The 0.123 ms keeps every note off an EXACT half-pixel: there the stand-in
        // below is decided by 1e-13 of arithmetic noise, where the GPU's 1/256
        // sub-pixel vertex snap decides it the same way every frame.
        var times = Enumerable.Range(0, 200).Select(i => 3_000.123 + i * spacingMs).ToArray();

        Assert.Equal(0, FramesWithMixedSteps(times, zoom, scale, snap: true));
        Assert.True(FramesWithMixedSteps(times, zoom, scale, snap: false) > 0,
            "control: the unsnapped origin should move elements by different amounts");
    }

    private static int FramesWithMixedSteps(double[] times, double zoom, double scale, bool snap)
    {
        var mixed = 0;
        long[]? previous = null;
        for (var f = 0; f < 30; f++)
        {
            var anchor = 10_000 + f * FrameMs;
            var start = snap
                ? TimelineViewMath.SnappedViewStartMs(anchor, zoom, scale)
                : TimelineViewMath.ViewStartMs(anchor, zoom);
            // Floor(x + 0.5): a pixel-centre test, like the rasterizer's. Math.Round
            // rounds halves to EVEN, which would alternate with the origin's parity.
            var px = times.Select(t => (long)Math.Floor((t - start) * zoom * scale + 0.5)).ToArray();
            if (previous != null && px.Zip(previous, (a, b) => a - b).Distinct().Count() > 1) mixed++;
            previous = px;
        }

        return mixed;
    }

    /// <summary>
    ///     The waveform rounds its column edges itself. At a fractional pixel
    ///     scale, rounding in LAYOUT units against a device-pixel origin puts each
    ///     column's rounding back out of step with the scroll, so the real
    ///     <see cref="WaveformLayer" /> must emit edges that are whole device pixels
    ///     and that move as one set between two frames.
    /// </summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void TheWaveform_MovesItsColumnsAsOneStrip(double scale)
    {
        const double zoom = 0.035;
        var peaks = new float[20_000];
        for (var i = 0; i < peaks.Length; i++) peaks[i] = (float)(0.2 + 0.7 * Math.Abs(Math.Sin(i * 0.37)));
        var samples = WaveformSamples.FromBasePeaks(peaks, 10.0);
        Assert.False(WaveformLod.Compute(samples, zoom)!.Value.DrawOutline,
            "fixture: the outline ribbon's AA fringe sits off the pixel grid on purpose; keep it off here");

        var layer = new WaveformLayer();
        HashSet<long>? previous = null;
        double previousOriginPx = 0;
        for (var f = 0; f < 12; f++)
        {
            var anchor = 40_000 + f * FrameMs;
            var start = TimelineViewMath.SnappedViewStartMs(anchor, zoom, scale);
            var batcher = new TimelineBatcher();
            layer.Render(batcher, WaveformContext(samples, zoom, start, scale));
            var edges = DevicePixelEdges(batcher, scale);

            var originPx = Math.Round(start * zoom * scale);
            if (previous != null)
            {
                // Frame f's interior edges are frame f-1's, shifted by the origin step.
                var step = (long)(originPx - previousOriginPx);
                foreach (var e in edges.Where(e => e is > 20 and < 1000))
                    Assert.Contains(e + step, previous);
            }

            previous = edges;
            previousOriginPx = originPx;
        }
    }

    private static HashSet<long> DevicePixelEdges(TimelineBatcher batcher, double scale)
    {
        const int floatsPerSolidVertex = 6; // x, y, r, g, b, a
        var edges = new HashSet<long>();
        for (var v = 0; v < batcher.SolidVertexCount; v++)
        {
            var devX = batcher.SolidBuffer[v * floatsPerSolidVertex] * scale;
            var whole = Math.Round(devX);
            Assert.True(Math.Abs(devX - whole) < 1e-3, $"waveform edge at {devX:F4} device px is not on the pixel grid");
            edges.Add((long)whole);
        }

        return edges;
    }

    private static ChartRenderContext WaveformContext(WaveformSamples samples, double zoom, double startMs,
        double scale)
    {
        return new ChartRenderContext
        {
            Width = 1000 / scale,
            Height = 400,
            CurrentZoom = zoom,
            ViewStartMs = startMs,
            ViewEndMs = startMs + 1000 / scale / zoom,
            PixelScale = scale,
            TopMargin = 40f,
            BottomMargin = 20f,
            LaneAreaHeight = 340f,
            StringCount = 6,
            WaveformData = samples
        };
    }

    [Fact]
    public void ChartRenderer_DrawsFromTheSnappedOrigin_AndFeedsTheLodItsPreviousLevel()
    {
        var path = UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Controls", "ChartRenderer.cs"));
        var src = UiSourceTree.ReadAllText(path);

        Assert.Contains("var viewStartMs = TimelineViewMath.SnappedViewStartMs(viewAnchorMs, currentZoom, renderScaling);", src);
        Assert.Contains("rc.PixelScale = renderScaling;", src);
        Assert.Contains("NoteLod.Compute(viewEndMs - viewStartMs, currentZoom, lodColumns, rc.NoteLod)", src);
    }
}
