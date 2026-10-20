using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;
using DotCharter.Rendering.Headless;
using DotCharter.Tone.Audio.Abstractions;
using DotCharter.UI.Controls.Layers;
using DotCharter.UI.Graphics.Vulkan2D;

namespace DotCharter.Tests.Perf;

/// <summary>
///     B7 (v0.30) baseline measurements over the REAL headless frame paths —
///     the numbers legs b/c/f are measured against. Doubly gated: wall clocks
///     need <c>DOTCHARTER_PERF_GATE=1</c> (dev machine), GPU frames need
///     <c>DOTCHARTER_GPU_TESTS=1</c>. Assertions are SANITY only (a frame
///     rendered, time is positive) — the printed <c>[PerfHarness]</c> lines are
///     the deliverable, never a budget that flakes with driver noise.
///     Run:
///     <code>
///     $env:DOTCHARTER_PERF_GATE="1"; $env:DOTCHARTER_GPU_TESTS="1"
///     dotnet test DotCharter.Tests\DotCharter.Tests.csproj --no-build --filter RenderPerfBaseline
///     </code>
/// </summary>
[Collection("HeadlessGpu")]
public sealed class RenderPerfBaselineTests
{
    private const int Width = 900;
    private const int Height = 400;

    private readonly ITestOutputHelper _out;

    public RenderPerfBaselineTests(ITestOutputHelper output) => _out = output;

    private static void RequirePerfGate()
        => Assert.SkipWhen(Environment.GetEnvironmentVariable("DOTCHARTER_PERF_GATE") != "1",
            "Dev-machine wall-clock baseline — set DOTCHARTER_PERF_GATE=1 to run.");

    private static void RequireGpu()
        => Assert.SkipWhen(Environment.GetEnvironmentVariable("DOTCHARTER_GPU_TESTS") != "1",
            "GPU frame baseline — set DOTCHARTER_GPU_TESTS=1 to run.");

    [Fact]
    public void Timeline2D_WaveformFrame_Baseline()
    {
        RequirePerfGate();
        RequireGpu();

        // The seed golden test's standalone-layer scene: a real waveform
        // through the real layer/batcher pipeline into the offscreen Vulkan
        // renderer — the ONE 2D frame path since the v0.33 OpenGL removal.
        // (The historical GL twin of this baseline died with that path; the
        // GL-vs-Vulkan A/B at the port measured GL ~3.3–3.7 ms vs Vulkan
        // ~0.8–0.9 ms median headless, commit 43866e8d.) The number includes
        // full GPU readback + fence waits, so it is a regression tripwire,
        // never an on-screen fps claim.
        var peaks = new float[600];
        for (var i = 0; i < peaks.Length; i++)
            peaks[i] = (float)(0.15 + 0.8 * Math.Abs(Math.Sin(i * 0.05)));
        var rc = new ChartRenderContext
        {
            Width = Width,
            Height = Height,
            CurrentZoom = 1.0,
            ViewStartMs = 0,
            ViewEndMs = 6000,
            TopMargin = 150f,
            BottomMargin = 20f,
            LaneAreaHeight = Height - 170f,
            StringCount = 6,
            DrawLabels = true,
            WaveformData = WaveformSamples.FromBasePeaks(peaks, 10.0)
        };
        var layers = new ITimelineRenderLayer[] { new WaveformLayer() };

        using var renderer = new HeadlessVulkanTimelineRenderer(Width, Height);
        // Reused readback buffer: the harness measures the FRAME path's
        // allocations, not the test instrument's (2×W×H×4/call before).
        var frame = new byte[Width * Height * 4];
        var result = RenderPerfHarness.Measure(_out, "2D timeline frame (waveform layer, 900x400, Vulkan)",
            warmup: 5, iterations: 60, () => frame = renderer.Render(rc, layers, reusePixels: frame));

        Assert.Equal(Width * Height * 4, frame.Length); // a real frame came back
        Assert.True(result.MedianMs > 0);
        // Steady-state zero-allocation tripwire (v0.33): the reused-readback
        // frame path must allocate nothing per frame — a regression here is
        // steady-state GC during playback. Deterministic, not wall-clock, so it
        // never flakes with driver noise. Keep this intact.
        Assert.True(result.AllocBytesPerIter == 0,
            $"2D timeline frame allocated {result.AllocBytesPerIter} B/frame (expected 0).");
    }

    [Fact]
    public void Highway3D_EmptyBoardFrame_Baseline()
    {
        RequirePerfGate();
        RequireGpu();

        // Board-only snapshot: measures the fixed per-frame cost of the Vulkan
        // path (passes, batches, readback) with no chart content — the floor
        // every instancing/batching win in legs b/c is measured on top of.
        var snapshot = new FrameSnapshot { StringCount = 6 };

        using var renderer = new HeadlessHighwayRenderer();
        var frame = new byte[640 * 360 * 4]; // reused readback — see the 2D note
        var result = RenderPerfHarness.Measure(_out, "3D highway frame (empty board, 640x360)",
            warmup: 5, iterations: 60,
            () => frame = renderer.Render(snapshot, 640, 360, timeMs: 0, reusePixels: frame));

        Assert.Equal(640 * 360 * 4, frame.Length);
        Assert.True(result.MedianMs > 0);
        Assert.True(result.AllocBytesPerIter == 0,
            $"3D empty-board frame allocated {result.AllocBytesPerIter} B/frame (expected 0).");
    }

    [Fact]
    public void Highway3D_DenseChartFrame_Baseline()
    {
        RequirePerfGate();
        RequireGpu();

        // The B3 instancing scenario (v0.30): a dense visible slice — hundreds
        // of gems, half with sustains, 32nd-note gridlines — so per-object
        // batching cost actually shows. The empty-board baseline above is the
        // fixed floor; the DELTA between the two is the per-object cost any
        // instancing/batching rework must shrink.
        var s = new FrameSnapshot
        {
            StringCount = 6, HighwayScrollSpeed = 1.3f, Anchors = new RenderAnchor[5]
        };
        for (var i = 0; i < s.Anchors.Length; i++)
        {
            s.Anchors[i] = new RenderAnchor
            {
                Time = i * 2000, EndTime = (i + 1) * 2000, Fret = (byte)(1 + i * 2), Width = 5
            };
        }

        s.AnchorCount = s.Anchors.Length;

        var beats = new List<RenderBeat>();
        for (var i = 0; i < 320; i++) // 125 ms grid over 40 s — dense gridlines
        {
            beats.Add(new RenderBeat
            {
                Time = i * 125, IsMeasure = i % 16 == 0, Bpm = 120,
                BeatsInMeasure = 4, NoteDenominator = 4, MeasureNumber = i / 16
            });
        }

        s.Beats = beats.ToArray();
        s.BeatCount = beats.Count;

        var notes = new List<RenderNote>();
        for (var i = 0; i < 600; i++) // ~75 notes/s across the visible horizon
        {
            notes.Add(new RenderNote
            {
                Time = 100 + i * 13.0,
                StringIndex = (byte)(i % 6),
                Fret = (byte)(1 + i % 12),
                Sustain = i % 2 == 0 ? 160 : 0 // half the gems drag sustain tails
            });
        }

        s.Notes = notes.ToArray();
        s.NoteCount = notes.Count;

        using var renderer = new HeadlessHighwayRenderer();
        var frame = new byte[640 * 360 * 4];
        var result = RenderPerfHarness.Measure(_out, "3D highway frame (dense chart, 640x360)",
            warmup: 5, iterations: 60,
            () => frame = renderer.Render(s, 640, 360, timeMs: 0, reusePixels: frame));

        Assert.Equal(640 * 360 * 4, frame.Length);
        Assert.True(result.MedianMs > 0);
        Assert.True(result.AllocBytesPerIter == 0,
            $"3D dense-chart frame allocated {result.AllocBytesPerIter} B/frame (expected 0).");
    }
}
