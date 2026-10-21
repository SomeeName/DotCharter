using DotCharter.Rendering.Data;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Tests.UI.Linking;
using DotCharter.UI.Controls.Layers;
using DotCharter.UI.Editing.Linking;
using DotCharter.UI.Editing.Tracks;
using DotCharter.UI.Graphics.Vulkan2D;
using static DotCharter.Tests.Core.Linking.LinkFixture;

namespace DotCharter.Tests.Rendering.Headless;

/// <summary>
///     Linked measures through the real 2D layers and the offscreen Vulkan renderer: the LINKS
///     row draws a chip in the group colour at each placement, and hovering a chip outlines the
///     group's measures in the note lanes — every placement of it, none of another group's.
///     <c>DOTCHARTER_GPU_DUMP=1</c> writes both frames. GPU-gated: <c>DOTCHARTER_GPU_TESTS=1</c>.
/// </summary>
[Collection("HeadlessGpu")]
public class HeadlessLinkTimelineTests
{
    private const int Width = 1600;
    private const int Height = 420;

    [Fact]
    public void Hovering_a_chip_outlines_every_placement_of_its_group_and_nothing_else()
    {
        Assert.SkipWhen(!GoldenFrame.GpuTestsEnabled, "GPU headless test — set DOTCHARTER_GPU_TESTS=1 to run.");

        var t = LinkedMeasuresHarness.WithRhythm();
        var h = t.Links;
        var a = h.Link((h.Lead, 1), (h.Lead, 4), (h.Rhythm, 1), (h.Rhythm, 4));
        var b = h.Link((h.Lead, 7), (h.Rhythm, 7));
        var colorA = LinkLaneModel.ColorOf(h.Project, a);

        using var r = new HeadlessVulkanTimelineRenderer(Width, Height);
        var plain = r.Render(Context(t), Layers());
        t.Editor.LinkHighlight.SetHovered(a.Id);
        var hovered = r.Render(Context(t), Layers());
        GoldenFrame.Dump(plain, Width, Height, "tl_links_row");
        GoldenFrame.Dump(hovered, Width, Height, "tl_links_hover");

        var rc = Context(t);
        var chipRow = (int)(rc.Ribbon.BarTop(rc.Ribbon.LinkY) + rc.Ribbon.Bar * 0.5f);
        foreach (var measure in new[] { 1, 4 })
        {
            Assert.True(Count(plain, colorA, X(rc, measure) + 2, X(rc, measure) + 8, chipRow - 2, chipRow + 2) > 4,
                $"no group-A chip at measure {measure}");
        }

        var lanes = ((int)rc.TopMargin + 4, (int)(rc.TopMargin + rc.LaneAreaHeight) - 4);
        foreach (var measure in new[] { 1, 4 })
        {
            Assert.Equal(0, Count(plain, colorA, X(rc, measure) - 1, X(rc, measure) + 2, lanes.Item1, lanes.Item2));
            Assert.True(Count(hovered, colorA, X(rc, measure) - 1, X(rc, measure) + 2, lanes.Item1, lanes.Item2) > 100,
                $"the hovered group's measure {measure} has no outline");
        }

        Assert.Equal(0, Count(hovered, LinkLaneModel.ColorOf(h.Project, b), X(rc, 7) - 1, X(rc, 7) + 2, lanes.Item1, lanes.Item2));
    }

    private static ITimelineRenderLayer[] Layers() =>
        [new BackgroundLayer { IncludeBeatGrid = false }, new BeatGridLayer(), new LaneLayer(), new NoteLayer(), new LinkLayer()];

    private static int X(ChartRenderContext rc, int measure) =>
        (int)((At(measure, 0).GetPositionMs(rc.Project!.Beats) - rc.ViewStartMs) * rc.CurrentZoom);

    /// <summary>Pixels within a few levels of <paramref name="c" /> in the box — BGRA8, top row first.</summary>
    private static int Count(byte[] bgra, Avalonia.Media.Color c, int x0, int x1, int y0, int y1)
    {
        var n = 0;
        for (var y = Math.Max(0, y0); y < Math.Min(Height, y1); y++)
        {
            for (var x = Math.Max(0, x0); x < Math.Min(Width, x1); x++)
            {
                var i = (y * Width + x) * 4;
                if (Math.Abs(bgra[i] - c.B) < 24 && Math.Abs(bgra[i + 1] - c.G) < 24 && Math.Abs(bgra[i + 2] - c.R) < 24)
                    n++;
            }
        }

        return n;
    }

    private static ChartRenderContext Context(LinkedMeasuresHarness t)
    {
        var project = t.Links.Project;
        var arrangement = t.Links.Lead;
        var level = DotCharter.Tests.Services.State.Linking.LinkHarness.Hardest(arrangement);
        var endMs = At(10, 0).GetPositionMs(project.Beats);
        var zoom = Width / endMs;
        var ctx = new SnapshotUpdateContext
        {
            ActiveBeats = project.Beats,
            ActiveNotes = level.Notes,
            ActiveChords = level.Chords,
            ActiveAnchors = level.Anchors,
            ActiveHandShapes = level.HandShapes,
            ActiveTemplates = arrangement.ChordTemplates
        };
        var snapshot = new FrameSnapshot { StringCount = 6 };
        FrameSnapshot.BuildSnapshot(0, endMs, 0, snapshot, ctx, []);
        var ribbon = RibbonLaneRules.Compose(new RibbonVisibility(), new RibbonContent(0, false, false, false, HasLinks: true));
        return new ChartRenderContext
        {
            Width = Width,
            Height = Height,
            CurrentZoom = zoom,
            ViewStartMs = 0,
            ViewEndMs = endMs,
            TopMargin = ribbon.TotalHeight + 6f,
            BottomMargin = 30f,
            LaneAreaHeight = 300f,
            LaneHeight = 50f,
            StringCount = 6,
            DrawLabels = true,
            Snapshot = snapshot,
            Editor = t.Editor,
            Transport = t.Transport,
            Project = project,
            Arrangement = arrangement,
            Ribbon = ribbon
        };
    }
}
