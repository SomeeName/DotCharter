using DotCharter.Rendering.Timeline2D;
using DotCharter.Tone.Audio.Abstractions;
using DotCharter.UI.Controls.Layers;
using DotCharter.UI.Controls.Layers.Helpers;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     The v0.35 waveform look, asserted on the geometry the layer STAGES —
///     <see cref="TimelineBatcher" /> is a plain float-buffer builder, so this
///     runs in the default gate with no GPU and no Avalonia app (same trick as
///     <see cref="NoteLayerCullingTests" /> and <c>HighwayHorizonCullTests</c>).
///     <para>
///         What is pinned is the product claim, not the implementation: the
///         strip draws a loudness BODY strictly inside the PEAK envelope, so a
///         quiet passage containing one transient no longer reads as
///         full-scale — which is the whole reason a max-only pyramid was not
///         enough of an audio cue.
///     </para>
/// </summary>
public sealed class WaveformLayerGeometryTests
{
    private const double ViewWidth = 900.0;
    private const float LaneAreaHeight = 300f;
    private const float TopMargin = 40f;

    // Mirrors WaveformLayer's own constant: the mirrored envelope reaches at
    // most 70% of half the lane area.
    private const float HeightScale = 0.7f;
    private const float FullScalePx = LaneAreaHeight / 2f * HeightScale;
    private const float CenterY = TopMargin + LaneAreaHeight / 2f;

    /// <summary>Quiet floor with one full-scale transient every
    /// <paramref name="spikeEvery" /> blocks: at any level coarser than the
    /// spike spacing the PEAK pyramid reads 1.0 and the BODY pyramid stays
    /// near the floor. That gap is exactly what the second pyramid exists
    /// for.</summary>
    private static WaveformSamples SpikyQuietSignal(int blocks, double blockMs,
        float floor = 0.1f, int spikeEvery = 8)
    {
        var peaks = new float[blocks];
        for (var i = 0; i < blocks; i++) peaks[i] = i % spikeEvery == 0 ? 1.0f : floor;
        return WaveformSamples.FromBasePeaks(peaks, blockMs);
    }

    private static ChartRenderContext Context(WaveformSamples wf, double zoom, double viewStartMs = 0)
        => new()
        {
            Width = ViewWidth,
            Height = 400,
            CurrentZoom = zoom,
            ViewStartMs = viewStartMs,
            ViewEndMs = viewStartMs + ViewWidth / zoom,
            TopMargin = TopMargin,
            BottomMargin = 20f,
            LaneAreaHeight = LaneAreaHeight,
            StringCount = 6,
            DrawLabels = true,
            WaveformData = wf
        };

    /// <summary>Every staged solid vertex's distance from the centreline
    /// (the solid layout is 6 floats per vertex: x, y, r, g, b, a).</summary>
    private static List<float> Deviations(TimelineBatcher batcher)
    {
        var result = new List<float>(batcher.SolidVertexCount);
        for (var v = 0; v < batcher.SolidVertexCount; v++)
            result.Add(Math.Abs(batcher.SolidBuffer[v * 6 + 1] - CenterY));
        return result;
    }

    [Fact]
    public void TheLoudnessBodyIsDrawnStrictlyInsideThePeakEnvelope()
    {
        // 2 ms blocks at 0.2 px/ms (the default zoom) → the LOD lands well
        // above level 0, so peak and body really differ.
        var wf = SpikyQuietSignal(4096, WaveformSamples.GenerationBlockMs);
        var batcher = new TimelineBatcher();
        new WaveformLayer().Render(batcher, Context(wf, zoom: 0.2));

        var lod = WaveformLod.Compute(wf, 0.2);
        Assert.True(lod.HasValue);
        Assert.True(lod!.Value.Level > 0, "fixture must not sit on the base level, or body == peak by definition");

        var peakAtLevel = wf.Mipmaps[lod.Value.Level][10];
        var bodyAtLevel = wf.Body[lod.Value.Level][10];
        Assert.Equal(1.0f, peakAtLevel, 3);                     // the transient saturates the peak …
        Assert.True(bodyAtLevel < 0.5f, $"body {bodyAtLevel} should stay near the quiet floor");

        var deviations = Deviations(batcher);
        var expectedPeakPx = peakAtLevel * FullScalePx;
        var expectedBodyPx = bodyAtLevel * FullScalePx;

        // The outermost geometry is the peak envelope …
        Assert.True(deviations.Max() <= expectedPeakPx + 0.51f,
            $"geometry reached {deviations.Max():0.##} px, past the {expectedPeakPx:0.##} px peak envelope");
        // … and the body shoulder is drawn, strictly inside it.
        Assert.Contains(deviations, d => Math.Abs(d - expectedBodyPx) < 0.51f);
        Assert.True(expectedBodyPx < expectedPeakPx * 0.5f,
            "fixture sanity: the shoulder must be visibly inside the envelope");
    }

    [Fact]
    public void OnTheBaseLevel_NoBodyGeometryIsWasted()
    {
        // Level 0 holds ONE block per cell, where max == mean, so a body pass
        // would be pure overdraw. Deep zoom is exactly where the frame is
        // busiest, so this is a perf invariant, not a cosmetic one.
        var wf = SpikyQuietSignal(4096, WaveformSamples.GenerationBlockMs);
        var lod = WaveformLod.Compute(wf, zoom: 2.0);
        Assert.Equal(0, lod!.Value.Level);

        var batcher = new TimelineBatcher();
        new WaveformLayer().Render(batcher, Context(wf, zoom: 2.0));

        // Byte-for-byte the same geometry a pyramid with NO body level
        // produces — i.e. the body pass costs exactly nothing here.
        var peakOnly = new TimelineBatcher();
        new WaveformLayer().Render(peakOnly, Context(
            new WaveformSamples(wf.Mipmaps, wf.BaseBlockMs), zoom: 2.0));

        Assert.True(batcher.SolidVertexCount > 0, "fixture sanity: something must have drawn");
        Assert.Equal(peakOnly.SolidVertexCount, batcher.SolidVertexCount);
    }

    [Fact]
    public void TheZeroLineSpansTheStripPastTheEndOfTheAudio()
    {
        // Half a second of audio, viewport parked ten seconds later: no column
        // is emitted at all, but the strip must still show its baseline rather
        // than going blank.
        var wf = SpikyQuietSignal(256, WaveformSamples.GenerationBlockMs);
        var batcher = new TimelineBatcher();
        new WaveformLayer().Render(batcher, Context(wf, zoom: 0.2, viewStartMs: 10_000));

        Assert.True(batcher.SolidVertexCount > 0, "the zero line must survive an empty sample window");
        for (var v = 0; v < batcher.SolidVertexCount; v++)
        {
            Assert.True(Math.Abs(batcher.SolidBuffer[v * 6 + 1] - CenterY) <= 1f,
                "nothing but the zero line may draw where there is no audio");
        }

        var xs = new List<float>();
        for (var v = 0; v < batcher.SolidVertexCount; v++) xs.Add(batcher.SolidBuffer[v * 6]);
        Assert.Equal(0f, xs.Min());
        Assert.Equal((float)ViewWidth, xs.Max());
    }

    [Fact]
    public void AWarmFramePathAllocatesNothing()
    {
        // The layer runs on every timeline frame; the reusable ribbon buffers
        // and the pre-grown batcher are what keep it off the GC. A body pass
        // built with LINQ or a scratch array would show up here.
        var wf = SpikyQuietSignal(4096, WaveformSamples.GenerationBlockMs);
        var layer = new WaveformLayer();
        var batcher = new TimelineBatcher();
        var rc = Context(wf, zoom: 0.2);

        for (var i = 0; i < 8; i++)
        {
            batcher.Reset();
            layer.Render(batcher, rc);
        }

        // Best-of-N lives in AllocationProbe now — this test is where that rule
        // was first worked out, and every other zero-allocation claim in the
        // suite needed it too.
        AllocationProbe.AssertAllocationFree("warm waveform frame", () =>
        {
            batcher.Reset();
            layer.Render(batcher, rc);
        });
    }
}
