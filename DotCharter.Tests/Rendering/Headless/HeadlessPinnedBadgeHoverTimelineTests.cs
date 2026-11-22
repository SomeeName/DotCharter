using Avalonia.Media;
using DotCharter.Rendering.Data;
using DotCharter.UI.Editing.Tracks;
using DotCharter.UI.Controls.Layers;
using DotCharter.UI.Graphics.Vulkan2D;

namespace DotCharter.Tests.Rendering.Headless;

/// <summary>
///     The sticky tone / section / phrase badges (#482) through the REAL event layer and the
///     offscreen Vulkan renderer: a hovered badge is painted in its hover look (it opens the event
///     editor on click), an idle one is not. <c>DOTCHARTER_GPU_DUMP=1</c> writes both frames (the
///     PR's before/after). GPU-gated: <c>DOTCHARTER_GPU_TESTS=1</c>.
/// </summary>
[Collection("HeadlessGpu")]
public class HeadlessPinnedBadgeHoverTimelineTests
{
    private const int Width = 900;
    private const int Height = 150;
    private const double Zoom = 0.1;
    private const double ViewStartMs = 10_000;

    [Fact]
    public void AHoveredStickyBadge_IsPaintedInItsHoverLook()
    {
        Assert.SkipWhen(!GoldenFrame.GpuTestsEnabled, "GPU headless test — set DOTCHARTER_GPU_TESTS=1 to run.");

        var registry = new PinnedLabelHitRegistry();
        var owner = new object();
        using var r = new HeadlessVulkanTimelineRenderer(Width, Height);
        var layers = new ITimelineRenderLayer[] { new EventLayer(registry, owner) };

        var before = r.Render(Context(Snapshot()), layers);
        registry.SetHover(owner, PinnedLabelHitRegistry.PinnedLabelKind.Tone);
        var after = r.Render(Context(Snapshot()), layers);
        GoldenFrame.Dump(before, Width, Height, "tl_sticky_badge_hover_before");
        GoldenFrame.Dump(after, Width, Height, "tl_sticky_badge_hover_after");

        var hover = EventLayer.OnLane(Color.FromArgb(240, 34, 156, 117));
        Assert.Equal(0, Count(before, hover));
        Assert.True(Count(after, hover) > 100, "the hovered sticky tone badge kept its plain look");
    }

    private static int Count(byte[] bgra, Color c)
    {
        var n = 0;
        for (var i = 0; i < bgra.Length; i += 4)
        {
            if (Math.Abs(bgra[i] - c.B) < 6 && Math.Abs(bgra[i + 1] - c.G) < 6 && Math.Abs(bgra[i + 2] - c.R) < 6) n++;
        }

        return n;
    }

    private static List<BeatMarker> Beats() =>
        [.. Enumerable.Range(0, 64).Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120 })];

    private static FrameSnapshot Snapshot()
    {
        var beats = Beats();
        var ctx = new SnapshotUpdateContext
        {
            ActiveBeats = beats,
            ActiveNotes = [],
            ActiveChords = [],
            ActiveAnchors = [],
            ActiveHandShapes = [],
            EventPoints =
            [
                new EventPoint { Position = TimelineMath.PositionFromMs(6_000, beats), Section = "chorus", Phrase = "chorus" }
            ],
            Tones = [new ToneChange { Position = TimelineMath.PositionFromMs(8_000, beats), ToneName = "Distortion" }],
            SelectedTones = [],
            LyricPhrases = [],
            Showlights = []
        };
        var snapshot = new FrameSnapshot { StringCount = 6 };
        FrameSnapshot.BuildSnapshot(ViewStartMs, ViewStartMs + Width / Zoom, ViewStartMs, snapshot, ctx, []);
        return snapshot;
    }

    private static ChartRenderContext Context(FrameSnapshot snapshot) => new()
    {
        Width = Width,
        Height = Height,
        CurrentZoom = Zoom,
        ViewStartMs = ViewStartMs,
        ViewEndMs = ViewStartMs + Width / Zoom,
        CurrentTimeMs = ViewStartMs,
        StringCount = 6,
        DrawLabels = true,
        RendererPixelsPerMs = Zoom,
        Ribbon = new RibbonLayout(RibbonLanes.All),
        Snapshot = snapshot
    };
}
