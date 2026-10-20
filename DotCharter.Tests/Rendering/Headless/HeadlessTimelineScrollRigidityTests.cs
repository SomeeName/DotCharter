using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;
using DotCharter.Tone.Audio.Abstractions;
using DotCharter.UI.Controls.Layers;
using DotCharter.UI.Controls.Layers.Helpers;
using DotCharter.UI.Graphics.Vulkan2D;
using DotCharter.UI.Input;

namespace DotCharter.Tests.Rendering.Headless;

/// <summary>
///     #113, on the GPU: zoomed out, the 2D timeline scrolls less than a pixel per
///     frame. With a fractional scroll origin each note mark (a plain 3 px quad,
///     one sample per pixel) snapped to its own pixel on its own frame — the
///     issue's probe measured ~97 marks moving 1 px while ~70 stayed put, in 8 of
///     11 consecutive frames. This renders that passage through the REAL
///     <see cref="NoteLayer" /> and <see cref="WaveformLayer" /> with the origin the
///     renderer draws from (<see cref="TimelineViewMath.SnappedViewStartMs" />)
///     and asserts the frame scrolls RIGIDLY: every mark takes the same
///     whole-pixel step, and each frame is the previous one translated. The exact
///     origin is rendered too, as the control that proves the fixture can see the
///     fault. GPU-gated: <c>DOTCHARTER_GPU_TESTS=1</c>.
/// </summary>
[Collection("HeadlessGpu")]
public class HeadlessTimelineScrollRigidityTests(ITestOutputHelper output)
{
    private const int Width = 900;
    private const int Height = 400;
    private const double Zoom = 0.035; // px per ms: ~5.4 px per sixteenth, Marks LOD
    private const double SpacingMs = 60_000.0 / 97 / 4; // sixteenths at 97 BPM
    private const double FrameMs = 1000.0 / 60.0; // 1x playback at 60 fps: 0.58 px per frame
    private const int Frames = 12;
    private const int StringIndex = 2;

    [Fact]
    public void ZoomedOutSubPixelScrolling_MovesEveryMarkByTheSameWholePixelStep()
    {
        Assert.SkipWhen(!GoldenFrame.GpuTestsEnabled,
            "GPU golden-frame tests are gated: set DOTCHARTER_GPU_TESTS=1 to run them.");

        using var r = new HeadlessVulkanTimelineRenderer(Width, Height);
        var snapshot = Sixteenths(400);

        var snapped = MarkSteps(r, snapshot, snap: true, out var snappedReport);
        var exact = MarkSteps(r, snapshot, snap: false, out var exactReport);
        output.WriteLine("snapped origin:\n" + snappedReport);
        output.WriteLine("exact origin (control):\n" + exactReport);

        Assert.True(exact > 0,
            "control: with the exact origin some frames must move marks by different amounts, or the fixture cannot see #113");
        Assert.Equal(0, snapped);
    }

    [Fact]
    public void ZoomedOutSubPixelScrolling_TranslatesTheWholeFrame()
    {
        Assert.SkipWhen(!GoldenFrame.GpuTestsEnabled,
            "GPU golden-frame tests are gated: set DOTCHARTER_GPU_TESTS=1 to run them.");

        using var r = new HeadlessVulkanTimelineRenderer(Width, Height);
        var snapshot = Sixteenths(400);
        var waveform = Waveform();
        var layers = new ITimelineRenderLayer[] { new WaveformLayer(), new NoteLayer() };

        var snappedBad = FramesNotTranslated(r, snapshot, waveform, layers, snap: true, "rigid");
        var exactBad = FramesNotTranslated(r, snapshot, waveform, layers, snap: false, "exact");
        output.WriteLine($"frames that are NOT a whole-pixel translation of the previous one: " +
                         $"snapped {snappedBad}/{Frames - 1}, exact {exactBad}/{Frames - 1}");

        Assert.True(exactBad > 0, "control: the exact origin should not translate rigidly");
        Assert.Equal(0, snappedBad);
    }

    /// <summary>Frames (after the first) in which the tracked marks did NOT all
    /// move by the same number of pixels.</summary>
    private static int MarkSteps(HeadlessVulkanTimelineRenderer r, FrameSnapshot snapshot, bool snap,
        out string report)
    {
        var lines = new List<string>();
        var mixed = 0;
        Dictionary<int, int>? previous = null;
        for (var f = 0; f < Frames; f++)
        {
            var anchor = Anchor(f);
            var rc = Context(snapshot, ViewStart(anchor, snap), null);
            var px = r.Render(rc, new ITimelineRenderLayer[] { new NoteLayer() });
            if (f < 3) GoldenFrame.Dump(px, Width, Height, $"scroll-rigidity-{(snap ? "snapped" : "exact")}-f{f}");

            var marks = MarkLeftEdges(px, rc, TimelineViewMath.ViewStartMs(anchor, Zoom));
            Assert.True(marks.Count > 100, $"frame {f}: only {marks.Count} marks found on the lane row");
            if (previous != null)
            {
                var steps = marks.Where(m => previous.ContainsKey(m.Key))
                    .GroupBy(m => m.Value - previous[m.Key])
                    .OrderBy(g => g.Key)
                    .Select(g => (Step: g.Key, Count: g.Count()))
                    .ToList();
                if (steps.Count > 1) mixed++;
                lines.Add($"  frame {f}: ideal shift since frame 0 {f * FrameMs * Zoom:F3}px  steps[" +
                          string.Join(", ", steps.Select(s => $"{s.Step}px x{s.Count}")) + "]");
            }

            previous = marks;
        }

        lines.Add($"  frames whose marks moved by DIFFERENT pixel amounts: {mixed}/{Frames - 1}");
        report = string.Join("\n", lines);
        return mixed;
    }

    /// <summary>Frames that are not the previous frame shifted left by the
    /// origin's whole-pixel step (interior only, per-channel tolerance 2).</summary>
    private static int FramesNotTranslated(HeadlessVulkanTimelineRenderer r, FrameSnapshot snapshot,
        WaveformSamples waveform, IReadOnlyList<ITimelineRenderLayer> layers, bool snap, string name)
    {
        var bad = 0;
        byte[]? previous = null;
        var previousStart = 0.0;
        for (var f = 0; f < Frames; f++)
        {
            var start = ViewStart(Anchor(f), snap);
            var px = r.Render(Context(snapshot, start, waveform), layers);
            if (f < 2) GoldenFrame.Dump(px, Width, Height, $"scroll-rigidity-frame-{name}-f{f}");
            if (previous != null)
            {
                var step = (int)Math.Round((start - previousStart) * Zoom);
                if (MismatchedPixels(previous, px, step) > 0) bad++;
            }

            previous = px;
            previousStart = start;
        }

        return bad;
    }

    private static int MismatchedPixels(byte[] previous, byte[] current, int step)
    {
        const int margin = 30;
        const int tolerance = 2;
        var mismatched = 0;
        for (var y = 0; y < Height; y++)
        {
            for (var x = margin; x < Width - margin - step; x++)
            {
                var a = (y * Width + x) * 4;
                var b = (y * Width + x + step) * 4;
                for (var c = 0; c < 3; c++)
                {
                    if (Math.Abs(current[a + c] - previous[b + c]) <= tolerance) continue;
                    mismatched++;
                    break;
                }
            }
        }

        return mismatched;
    }

    /// <summary>Left edge (px) of every whole mark on the lane's centre row, keyed
    /// by the index of the note it draws (the nearest ideal position).</summary>
    private static Dictionary<int, int> MarkLeftEdges(byte[] px, ChartRenderContext rc, double exactStartMs)
    {
        var y = (int)(rc.LaneTopFor(StringIndex) + rc.LaneHeight / 2f);
        var marks = new Dictionary<int, int>();
        var x = 0;
        while (x < Width)
        {
            if (!Lit(px, x, y))
            {
                x++;
                continue;
            }

            var left = x;
            while (x < Width && Lit(px, x, y)) x++;
            if (left <= 1 || x >= Width - 1) continue; // clipped by the frame edge

            var centreMs = exactStartMs + (left + (x - left) / 2.0) / Zoom;
            var note = (int)Math.Round(centreMs / SpacingMs);
            marks[note] = left;
        }

        return marks;
    }

    private static bool Lit(byte[] px, int x, int y)
    {
        var i = (y * Width + x) * 4;
        return Math.Max(px[i], Math.Max(px[i + 1], px[i + 2])) > 60;
    }

    private static double Anchor(int frame) => 30_000 + frame * FrameMs;

    private static double ViewStart(double anchor, bool snap) => snap
        ? TimelineViewMath.SnappedViewStartMs(anchor, Zoom, 1.0)
        : TimelineViewMath.ViewStartMs(anchor, Zoom);

    private static FrameSnapshot Sixteenths(int count)
    {
        var notes = new RenderNote[count];
        for (var i = 0; i < count; i++)
            notes[i] = new RenderNote { Time = i * SpacingMs, StringIndex = StringIndex, Fret = 5 };
        return new FrameSnapshot { StringCount = 6, Notes = notes, NoteCount = count };
    }

    private static WaveformSamples Waveform()
    {
        var peaks = new float[8_000];
        for (var i = 0; i < peaks.Length; i++) peaks[i] = (float)(0.2 + 0.7 * Math.Abs(Math.Sin(i * 0.37)));
        return WaveformSamples.FromBasePeaks(peaks, 10.0);
    }

    private static ChartRenderContext Context(FrameSnapshot snapshot, double viewStartMs, WaveformSamples? waveform) =>
        new()
        {
            Width = Width,
            Height = Height,
            CurrentZoom = Zoom,
            ViewStartMs = viewStartMs,
            ViewEndMs = viewStartMs + Width / Zoom,
            PixelScale = 1.0,
            TopMargin = 20f,
            BottomMargin = 20f,
            LaneAreaHeight = 360f,
            LaneHeight = 60f,
            StringCount = 6,
            DrawLabels = true,
            NoteLod = NoteLevelOfDetail.Marks,
            Snapshot = snapshot,
            WaveformData = waveform
        };
}
