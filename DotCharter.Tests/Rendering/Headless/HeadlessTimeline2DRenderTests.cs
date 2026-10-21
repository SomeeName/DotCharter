using System.Runtime.CompilerServices;
using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;
using DotCharter.Tone.Audio.Abstractions;
using DotCharter.UI.Controls.Layers;
using DotCharter.UI.Editing.Tracks;
using DotCharter.UI.Graphics.Vulkan2D;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.Rendering.Headless;

/// <summary>
///     Seed golden-frame tests for the 2D timeline: each test runs REAL
///     layers through the REAL batcher into the offscreen Vulkan renderer
///     (<see cref="HeadlessVulkanTimelineRenderer" />) and asserts STRUCTURAL
///     properties (<see cref="GoldenFrame" />). GPU-gated:
///     <c>DOTCHARTER_GPU_TESTS=1</c>.
///     <para>
///         History: these started as the OpenGL seed tests
///         (<c>HeadlessOpenGlRenderTests</c>). GL↔Vulkan output parity was
///         proven at the v0.33 port (commit <c>43866e8d</c>: 0.000% pixel
///         diff on the interleaved streams), so when the OpenGL path was
///         removed the same scenes and assertions moved onto the Vulkan
///         facade unchanged — the assertion surface is identical.
///     </para>
/// </summary>
[Collection("HeadlessGpu")]
public class HeadlessTimeline2DRenderTests
{
    private const int Width = 900;
    private const int Height = 400;

    private readonly ITestOutputHelper _out;

    public HeadlessTimeline2DRenderTests(ITestOutputHelper output) => _out = output;

    /// <summary>Inverted-V waveform: rises to a single central peak then falls,
    /// so the top silhouette has one sharp direction change (the join the ribbon
    /// must not gap).</summary>
    private static WaveformSamples InvertedVWaveform(int blocks, double baseBlockMs)
    {
        var peaks = new float[blocks];
        var center = (blocks - 1) / 2.0;
        for (var i = 0; i < blocks; i++)
        {
            var t = 1.0 - Math.Abs(i - center) / center; // 0 at ends, 1 at centre
            peaks[i] = (float)(0.12 + 0.85 * t);
        }

        return WaveformSamples.FromBasePeaks(peaks, baseBlockMs);
    }

    private static ChartRenderContext WaveformContext(WaveformSamples wf, double zoom, double viewEndMs)
    {
        return new ChartRenderContext
        {
            Width = Width,
            Height = Height,
            CurrentZoom = zoom,
            ViewStartMs = 0,
            ViewEndMs = viewEndMs,
            TopMargin = 150f,
            BottomMargin = 20f,
            LaneAreaHeight = Height - 170f,
            StringCount = 6,
            DrawLabels = true,
            WaveformData = wf
        };
    }

    // Column has any waveform/scene content (not the near-black timeline clear)
    // within [minY, maxY). The row bound is load-bearing for the ribbon test:
    // since v0.35 the layer draws a ZERO LINE across the full viewport width, so
    // a whole-column scan reports content everywhere and any gap assertion over
    // it is vacuous. Scanning only above the centreline keeps the question
    // "is the top silhouette continuous?" answerable.
    private static bool ColumnHasContent(byte[] bgra, int x, int threshold = 22,
        int minY = 0, int maxY = Height)
    {
        for (var y = minY; y < maxY; y++)
        {
            var i = (y * Width + x) * 4;
            if (Math.Max(bgra[i], Math.Max(bgra[i + 1], bgra[i + 2])) > threshold) return true;
        }

        return false;
    }

    // Count of visibly purple stroke pixels (the silhouette ribbon is the
    // brightest purple; the translucent fill is much dimmer). B dominates in the
    // waveform theme purples.
    private static int StrongStrokePixels(byte[] bgra, int blueMin = 95)
    {
        var n = 0;
        for (var i = 0; i < bgra.Length; i += 4)
        {
            if (bgra[i] > blueMin && bgra[i] > bgra[i + 2]) // B strong and B > R (purple/blue, not grey)
                n++;
        }

        return n;
    }

    [Fact]
    public void Waveform_RibbonOutline_ContinuousAcrossDirectionChange()
    {
        Assert.SkipWhen(!GoldenFrame.GpuTestsEnabled,
            "GPU headless test — set DOTCHARTER_GPU_TESTS=1 to run.");

        // BaseBlockMs 10 @ zoom 1.0 → stepWidthPx 10 (>= WaveformLod
        // .OutlineMinStepPx) → outline drawn, fat stroke; level 0 (10 >=
        // WaveformLod.TargetColumnPx). 60 blocks = 600ms of waveform.
        const double blockMs = 10.0;
        const double zoom = 1.0;
        var wf = InvertedVWaveform(60, blockMs);
        var rc = WaveformContext(wf, zoom, viewEndMs: 60 * blockMs);

        using var r = new HeadlessVulkanTimelineRenderer(Width, Height);
        var px = r.Render(rc, new ITimelineRenderLayer[] { new WaveformLayer() });
        GoldenFrame.Dump(px, Width, Height, "vk_waveform_ribbon");

        // The silhouette between its first and last visible column must be
        // gapless: the peak's direction change is INTERIOR, so a broken join
        // would show up as a background hole between two content columns. The
        // shape tapers to near-zero amplitude at its ends, so we bound the check
        // to [firstContent, lastContent] rather than the whole time span.
        // ABOVE the centreline only — the zero line would otherwise answer for
        // every column (see ColumnHasContent).
        const int aboveCentre = 258; // wfCenter is round(150 + 230/2) = 265
        var spanEnd = (int)(60 * blockMs * zoom); // 600
        int first = -1, last = -1;
        for (var x = 0; x < Math.Min(spanEnd, Width); x++)
        {
            if (ColumnHasContent(px, x, maxY: aboveCentre))
            {
                if (first < 0) first = x;
                last = x;
            }
        }

        var interiorGaps = 0;
        for (var x = first; x <= last; x++)
        {
            if (!ColumnHasContent(px, x, maxY: aboveCentre))
                interiorGaps++;
        }

        var stroke = StrongStrokePixels(px);
        _out.WriteLine($"[ribbon] content=[{first},{last}] interiorGaps={interiorGaps} strokePx={stroke}");

        try
        {
            Assert.True(first >= 0 && last - first > 300, "Waveform silhouette did not render across the board.");
            Assert.True(stroke > 400, $"Ribbon outline missing/too weak (strong-stroke px {stroke}).");
            Assert.True(interiorGaps == 0,
                $"Silhouette has {interiorGaps} interior gap column(s) — the joined ribbon must stay continuous across the direction change.");
        }
        catch
        {
            GoldenFrame.Attach(px, Width, Height, "vk_waveform_ribbon_fail");
            throw;
        }
    }

    [Fact]
    public void Waveform_LodGate_OutlineFlipsAtThreshold()
    {
        Assert.SkipWhen(!GoldenFrame.GpuTestsEnabled,
            "GPU headless test — set DOTCHARTER_GPU_TESTS=1 to run.");

        // Same shape, same zoom, same viewport — only the base block width
        // changes, straddling the documented drawOutline threshold
        // (stepWidthPx = blockMs*zoom >= 3.0). Both keep LOD level 0
        // (blockMs >= 2.5/zoom = 2.5), so the ONLY layer-output change is the
        // silhouette ribbon appearing/disappearing.
        const double zoom = 1.0;
        const double onBlockMs = 4.0; // stepWidthPx 4.0 >= 3 → outline ON
        const double offBlockMs = 2.6; // stepWidthPx 2.6 <  3 → outline OFF
        var onWf = InvertedVWaveform(200, onBlockMs);
        var offWf = InvertedVWaveform(200, offBlockMs);

        using var r = new HeadlessVulkanTimelineRenderer(Width, Height);
        var onPx = r.Render(WaveformContext(onWf, zoom, Width / zoom),
            new ITimelineRenderLayer[] { new WaveformLayer() });
        var offPx = r.Render(WaveformContext(offWf, zoom, Width / zoom),
            new ITimelineRenderLayer[] { new WaveformLayer() });
        GoldenFrame.Dump(onPx, Width, Height, "vk_lod_outline_on");
        GoldenFrame.Dump(offPx, Width, Height, "vk_lod_outline_off");

        var onStroke = StrongStrokePixels(onPx);
        var offStroke = StrongStrokePixels(offPx);
        var diff = GoldenFrame.DifferenceRatio(onPx, offPx);
        _out.WriteLine($"[lod] onStroke={onStroke} offStroke={offStroke} diff={diff:P2}");

        try
        {
            Assert.True(GoldenFrame.NonBlackRatio(offPx) > 0.02, "Below-threshold frame drew no waveform.");
            Assert.True(diff > 0.02, $"LOD threshold did not change layer output (diff {diff:P2}).");
            Assert.True(onStroke > offStroke * 2,
                $"Outline did not switch on across the threshold (on {onStroke} vs off {offStroke}).");
        }
        catch
        {
            GoldenFrame.Attach(onPx, Width, Height, "vk_lod_on_fail");
            GoldenFrame.Attach(offPx, Width, Height, "vk_lod_off_fail");
            throw;
        }
    }

    private static FrameSnapshot BeatsSnapshot(double[] bpms)
    {
        var s = new FrameSnapshot { StringCount = 6 };
        var beats = new List<RenderBeat>();
        for (var m = 0; m < bpms.Length; m++)
        {
            beats.Add(new RenderBeat
            {
                Time = m * 2000, IsMeasure = true, Bpm = bpms[m],
                BeatsInMeasure = 4, NoteDenominator = 4, MeasureNumber = m + 1
            });
        }

        s.Beats = beats.ToArray();
        s.BeatCount = beats.Count;
        return s;
    }

    private static ChartRenderContext MeasureContext(FrameSnapshot snap)
    {
        // GridDivisor defaults to 0 on an uninitialized instance (the field
        // initializer '= 4' never runs), so DrawSubdivisions short-circuits at
        // its 'GridDivisor <= 1' guard — no audio engine is constructed and no
        // NRE occurs. The measure-pill path never touches rc.Editor.
        var transport = (TransportViewModel)RuntimeHelpers.GetUninitializedObject(typeof(TransportViewModel));
        return new ChartRenderContext
        {
            Width = Width,
            Height = Height,
            CurrentZoom = 0.06, // 120 px/measure: pills draw, but unchanged BPM is deduplicated
            ViewStartMs = 0,
            ViewEndMs = Width / 0.06,
            TopMargin = 150f,
            BottomMargin = 20f,
            LaneAreaHeight = Height - 170f,
            StringCount = 6,
            DrawLabels = true,
            Snapshot = snap,
            Transport = transport,
            Ribbon = new RibbonLayout(RibbonLanes.All)
        };
    }

    // Non-background content within the measure-metadata band (the pill row).
    private static int MetadataBandContent(byte[] bgra, float metadataY, int threshold = 30)
    {
        var y0 = Math.Max(0, (int)metadataY - 2);
        var y1 = Math.Min(Height, (int)metadataY + (int)RibbonLayout.MetadataBandHeight);
        var n = 0;
        for (var y = y0; y < y1; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var i = (y * Width + x) * 4;
                if (Math.Max(bgra[i], Math.Max(bgra[i + 1], bgra[i + 2])) > threshold) n++;
            }
        }

        return n;
    }

    [Fact]
    public void MeasurePills_BpmChangeDedup_ChangesTextRegion()
    {
        Assert.SkipWhen(!GoldenFrame.GpuTestsEnabled,
            "GPU headless test — set DOTCHARTER_GPU_TESTS=1 to run.");

        // At this narrow measure width, only the first visible pill shows "120 BPM"; the rest
        // are number-only (dedup). Changing BPM: the change measure ALSO shows
        // its BPM segment — so more text is drawn in the metadata band.
        var constSnap = BeatsSnapshot([120.0, 120.0, 120.0, 120.0]);
        var varSnap = BeatsSnapshot([120.0, 120.0, 140.0, 160.0]);

        using var r = new HeadlessVulkanTimelineRenderer(Width, Height);
        var constRc = MeasureContext(constSnap);
        var varRc = MeasureContext(varSnap);
        var layer = new BeatGridLayer();

        var constPx = r.Render(constRc, new ITimelineRenderLayer[] { layer });
        var varPx = r.Render(varRc, new ITimelineRenderLayer[] { layer });
        GoldenFrame.Dump(constPx, Width, Height, "vk_pills_const");
        GoldenFrame.Dump(varPx, Width, Height, "vk_pills_var");

        var constBand = MetadataBandContent(constPx, constRc.Ribbon.MetadataY);
        var varBand = MetadataBandContent(varPx, varRc.Ribbon.MetadataY);
        _out.WriteLine($"[pills] constBand={constBand} varBand={varBand}");

        try
        {
            // Both draw pills (the number is always shown).
            Assert.True(constBand > 100, $"Constant-BPM pills drew almost nothing ({constBand}).");
            // The BPM-change chart draws extra BPM text → measurably more content.
            Assert.True(varBand > constBand * 1.12,
                $"BPM-change dedup produced no extra pill text (const {constBand} vs var {varBand}).");
        }
        catch
        {
            GoldenFrame.Attach(constPx, Width, Height, "vk_pills_const_fail");
            GoldenFrame.Attach(varPx, Width, Height, "vk_pills_var_fail");
            throw;
        }
    }
}
