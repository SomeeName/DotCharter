using System.Runtime.CompilerServices;
using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;
using DotCharter.Rendering.Vulkan;
using DotCharter.Tone.Audio.Abstractions;
using DotCharter.UI.Controls.Layers;
using DotCharter.UI.Editing.Tracks;
using DotCharter.UI.Graphics.Vulkan2D;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.Rendering.Headless;

/// <summary>
///     Vulkan 2D timeline goldens: fixed scenes (same
///     <see cref="ChartRenderContext" />, same real layer stack, same batcher
///     geometry) rendered through the composed-sibling Vulkan facade
///     (<see cref="HeadlessVulkanTimelineRenderer" />), asserted structurally
///     (<see cref="GoldenFrame" /> tolerances absorb rasterization/AA LSBs
///     only) plus repeat-render BYTE stability. GPU-gated:
///     <c>DOTCHARTER_GPU_TESTS=1</c>; PNGs are written on failure (and always
///     under <c>DOTCHARTER_GPU_DUMP=1</c>).
///     <para>
///         History: this file was <c>HeadlessTimelineParityTests</c> — the
///         v0.33 GL↔Vulkan parity gate, which rendered every scene through
///         BOTH backends and asserted them structurally close. Parity was
///         PROVEN at the port (commit <c>43866e8d</c>: 0.000% pixel diff on
///         the interleaved streams, coverage deltas within noise); the GL
///         baseline was removed together with the whole OpenGL path, so the
///         same scenes now gate the Vulkan renderer standalone.
///     </para>
/// </summary>
[Collection("HeadlessGpu")]
public class HeadlessTimelineGoldenTests
{
    private const int Width = 900;
    private const int Height = 400;

    private readonly ITestOutputHelper _out;

    public HeadlessTimelineGoldenTests(ITestOutputHelper output)
    {
        _out = output;
        // Enable the validation layer for every Vulkan instance created after
        // this point (honored only when the layer is installed — otherwise
        // Active stays false and the assertion no-ops).
        Environment.SetEnvironmentVariable("DOTCHARTER_VK_VALIDATION", "1");
    }

    private static void AssertNoValidationErrors(string when)
    {
        if (!VulkanValidationDiagnostics.Active) return;
        Assert.True(VulkanValidationDiagnostics.ErrorCount == 0,
            $"Vulkan validation errors after {when}: {VulkanValidationDiagnostics.ErrorCount}. " +
            $"Last: {VulkanValidationDiagnostics.LastError}");
    }

    // ---- fixtures (same shapes as the seed tests) -------------------------

    private static WaveformSamples InvertedVWaveform(int blocks, double baseBlockMs)
    {
        var peaks = new float[blocks];
        var center = (blocks - 1) / 2.0;
        for (var i = 0; i < blocks; i++)
        {
            var t = 1.0 - Math.Abs(i - center) / center;
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
        // Uninitialized TransportViewModel: GridDivisor defaults to 0 so the
        // subdivision path short-circuits without constructing an audio
        // engine — the documented headless trick from the seed tests.
        var transport = (TransportViewModel)RuntimeHelpers.GetUninitializedObject(typeof(TransportViewModel));
        return new ChartRenderContext
        {
            Width = Width,
            Height = Height,
            CurrentZoom = 0.1,
            ViewStartMs = 0,
            ViewEndMs = Width / 0.1,
            TopMargin = 150f,
            BottomMargin = 20f,
            LaneAreaHeight = Height - 170f,
            StringCount = 6,
            DrawLabels = true,
            Snapshot = snap,
            Transport = transport,
            Ribbon = new RibbonLayout(true, true, true, true, true)
        };
    }

    [Fact]
    public void Waveform_RendersStructuredSolidStreamFrame()
    {
        Assert.SkipWhen(!GoldenFrame.GpuTestsEnabled,
            "GPU headless test — set DOTCHARTER_GPU_TESTS=1 to run.");

        // The ribbon-outline scene: opaque core + AA fade quads + gradient
        // fill — exercises the solid stream end to end.
        var wf = InvertedVWaveform(60, 10.0);
        var rc = WaveformContext(wf, 1.0, viewEndMs: 600);
        var layers = new ITimelineRenderLayer[] { new WaveformLayer() };

        using var vk = new HeadlessVulkanTimelineRenderer(Width, Height);
        var px = vk.Render(rc, layers);
        AssertNoValidationErrors("waveform golden frame");

        var nonBlack = GoldenFrame.NonBlackRatio(px);
        var coverage = GoldenFrame.NonBlackRatio(px, 30);
        _out.WriteLine($"[waveform] nonBlack={nonBlack:P2} coverage30={coverage:P2}");
        GoldenFrame.Dump(px, Width, Height, "golden_waveform");

        try
        {
            Assert.True(nonBlack > 0.02, $"Waveform frame rendered (almost) nothing ({nonBlack:P2}).");
            Assert.True(coverage > 0.005,
                $"No stable content above the blend-noise threshold ({coverage:P2}) — fill/stroke missing.");
        }
        catch
        {
            GoldenFrame.Attach(px, Width, Height, "golden_waveform_fail");
            throw;
        }
    }

    [Fact]
    public void MeasurePills_RendersGeneralStreamTextAndGrid()
    {
        Assert.SkipWhen(!GoldenFrame.GpuTestsEnabled,
            "GPU headless test — set DOTCHARTER_GPU_TESTS=1 to run.");

        // The measure-pill scene: beat/measure lines (AA line quads) + pill
        // text — exercises the general stream incl. the Vulkan font atlas.
        var rc = MeasureContext(BeatsSnapshot([120.0, 120.0, 140.0, 160.0]));
        var layers = new ITimelineRenderLayer[] { new BeatGridLayer() };

        using var vk = new HeadlessVulkanTimelineRenderer(Width, Height);
        var px = vk.Render(rc, layers);
        AssertNoValidationErrors("measure-pill golden frame");

        var nonBlack = GoldenFrame.NonBlackRatio(px);
        // The pill row lives in the metadata band — atlas text must land
        // there. (NonBlackRatioInRegion takes NORMALIZED 0..1 coordinates.)
        var bandY0 = Math.Max(0f, rc.Ribbon.MetadataY - 2f) / Height;
        var bandY1 = Math.Min(Height, rc.Ribbon.MetadataY + RibbonLayout.MetadataBandHeight) / (double)Height;
        var bandRatio = GoldenFrame.NonBlackRatioInRegion(px, Width, Height, 0, bandY0, 1.0, bandY1, 30);
        _out.WriteLine($"[pills] nonBlack={nonBlack:P2} metadataBand={bandRatio:P2}");
        GoldenFrame.Dump(px, Width, Height, "golden_pills");

        try
        {
            Assert.True(nonBlack > 0.01, $"Pill frame rendered (almost) nothing ({nonBlack:P2}).");
            Assert.True(bandRatio > 0.005,
                $"Metadata band is empty ({bandRatio:P2}) — pill text / atlas glyphs missing.");
        }
        catch
        {
            GoldenFrame.Attach(px, Width, Height, "golden_pills_fail");
            throw;
        }
    }

    [Fact]
    public void CombinedScene_InterleavedStreams_BothLayersContribute()
    {
        Assert.SkipWhen(!GoldenFrame.GpuTestsEnabled,
            "GPU headless test — set DOTCHARTER_GPU_TESTS=1 to run.");

        // Waveform + beat grid together: solid and general streams interleave
        // (stream switches per draw-command run). Rendering the combined scene
        // and the waveform-only scene from the SAME context proves the second
        // stream actually contributed — a dropped FinalizeFrame run or a
        // broken stream switch renders the two identically (or empty).
        var wf = InvertedVWaveform(200, 45.0);
        var rc = MeasureContext(BeatsSnapshot([120.0, 120.0, 130.0, 140.0, 150.0]));
        rc.WaveformData = wf;
        var combinedLayers = new ITimelineRenderLayer[] { new WaveformLayer(), new BeatGridLayer() };
        var waveformOnly = new ITimelineRenderLayer[] { new WaveformLayer() };

        using var vk = new HeadlessVulkanTimelineRenderer(Width, Height);
        var combined = vk.Render(rc, combinedLayers);
        var solo = vk.Render(rc, waveformOnly);
        AssertNoValidationErrors("combined golden frames");

        var nonBlack = GoldenFrame.NonBlackRatio(combined);
        var diff = GoldenFrame.DifferenceRatio(combined, solo);
        _out.WriteLine($"[combined] nonBlack={nonBlack:P2} diffVsWaveformOnly={diff:P3}");
        GoldenFrame.Dump(combined, Width, Height, "golden_combined");
        GoldenFrame.Dump(solo, Width, Height, "golden_combined_waveform_only");

        try
        {
            Assert.True(nonBlack > 0.02, $"Combined frame rendered (almost) nothing ({nonBlack:P2}).");
            Assert.True(diff > 0.005,
                $"Beat grid layer added nothing over the waveform ({diff:P3}) — interleaved general stream lost.");
        }
        catch
        {
            GoldenFrame.Attach(combined, Width, Height, "golden_combined_fail");
            GoldenFrame.Attach(solo, Width, Height, "golden_combined_waveform_only_fail");
            throw;
        }
    }

    /// <summary>
    ///     The 2D frame must come out of the render pass OPAQUE — and until
    ///     v0.44 nothing in this file could have noticed that it did not. Every
    ///     assertion above reads B, G and R and steps over index +3, which is
    ///     the exact blind spot that let the identical alpha fault ship for
    ///     releases in the 3D pipelines ("that box vignet thing around", owner
    ///     2026-08-01). The pass clears alpha to 1, so any pixel below 255 is a
    ///     hole a blended draw punched in it.
    ///     <para>
    ///     Both scenes, because they exercise different pipelines: the waveform
    ///     is the SOLID stream (whose AA fade quads are the translucent ones),
    ///     the combined scene adds the GENERAL stream and the font atlas.
    ///     </para>
    /// </summary>
    [Fact]
    public void TheRenderedTimelineFrameIsFullyOpaque()
    {
        Assert.SkipWhen(!GoldenFrame.GpuTestsEnabled,
            "GPU headless test — set DOTCHARTER_GPU_TESTS=1 to run.");

        var wf = InvertedVWaveform(200, 45.0);
        var rc = MeasureContext(BeatsSnapshot([120.0, 120.0, 130.0, 140.0, 150.0]));
        rc.WaveformData = wf;

        using var vk = new HeadlessVulkanTimelineRenderer(Width, Height);
        foreach (var (name, layers) in new (string, ITimelineRenderLayer[])[]
                 {
                     ("solid", [new WaveformLayer()]),
                     ("combined", [new WaveformLayer(), new BeatGridLayer()]),
                 })
        {
            var px = vk.Render(rc, layers);
            AssertNoValidationErrors($"opacity golden frame ({name})");

            var hole = GoldenFrame.NonOpaqueBounds(px, Width, Height);
            _out.WriteLine($"[opacity:{name}] nonOpaque={(hole is null ? "none" : hole.ToString())}");

            if (hole is null) continue;

            // Dump what a compositor would actually show, not the raw frame —
            // the raw one looks correct, which is the whole problem.
            GoldenFrame.Attach(GoldenFrame.CompositeOverBackdrop(px, 0, 200, 0), Width, Height,
                $"golden_timeline_alpha_{name}_fail");
            Assert.Fail(
                $"The 2D timeline frame is not opaque ({name}): {hole.Value.Pixels} px below alpha 255, " +
                $"bounds x[{hole.Value.X0}..{hole.Value.X1}] y[{hole.Value.Y0}..{hole.Value.Y1}]. " +
                "Every blended draw must leave the frame's alpha at the pass's clear value — " +
                "see FrameAlphaPolicy.");
        }
    }

    [Fact]
    public void Vulkan_RepeatedRenders_AreByteStable()
    {
        Assert.SkipWhen(!GoldenFrame.GpuTestsEnabled,
            "GPU headless test — set DOTCHARTER_GPU_TESTS=1 to run.");

        // Same input twice through the same renderer must be IDENTICAL —
        // guards the vertex-slot/fence plumbing against cross-frame bleed.
        var wf = InvertedVWaveform(60, 10.0);
        var rc = WaveformContext(wf, 1.0, viewEndMs: 600);
        var layers = new ITimelineRenderLayer[] { new WaveformLayer() };

        using var vk = new HeadlessVulkanTimelineRenderer(Width, Height);
        var a = vk.Render(rc, layers);
        var b = vk.Render(rc, layers);
        AssertNoValidationErrors("repeat-render frames");

        var diff = GoldenFrame.DifferenceRatio(a, b, perChannelTol: 0);
        _out.WriteLine($"[repeat] diff={diff:P4}");
        try
        {
            Assert.True(diff == 0, $"Repeated identical renders differ ({diff:P4}).");
        }
        catch
        {
            GoldenFrame.Attach(a, Width, Height, "golden_repeat_a_fail");
            GoldenFrame.Attach(b, Width, Height, "golden_repeat_b_fail");
            throw;
        }
    }
}
