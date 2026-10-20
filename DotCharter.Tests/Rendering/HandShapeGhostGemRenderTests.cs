using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;
using DotCharter.Rendering.Text;
using DotCharter.Rendering.Timeline2D;
using DotCharter.UI.Controls.Layers;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     #785: a hand shape's GHOST gems in the 2D timeline. The snapshot carries each ghost string's
///     held fret and which of them a played gem shares at the bar's start; the layer draws each as a
///     DIMMED gem (the note head's ghost alpha, 80) on its lane at the bar's start, nudged just before
///     it when a played gem is there. No GPU: <see cref="TimelineBatcher" /> is a CPU vertex builder,
///     and a gem is GENERAL geometry (9 floats a vertex: x, y, r, g, b, a, u, v, type).
/// </summary>
public sealed class HandShapeGhostGemRenderTests
{
    private const float TopMargin = 20f;
    private const float LaneHeight = 60f;

    private static List<BeatMarker> Beats(int count = 16) =>
        [.. Enumerable.Range(0, count).Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120 })];

    [Fact]
    public void TheSnapshot_CarriesTheGhostFrets_AndWhichOfThemAPlayedGemShares()
    {
        var arrangement = new Arrangement { StringCount = 6 };
        arrangement.ChordTemplates.Add(new ChordTemplate
        {
            Id = 0, Frets = { [0] = 3, [1] = 5, [2] = 5 }, GhostMask = ChordTemplate.GhostBit(1) | ChordTemplate.GhostBit(2)
        });
        var level = new ChartLevel();
        level.HandShapes.Add(new HandShape { Position = new FractionalPosition(2), EndPosition = new FractionalPosition(4), TemplateId = 0 });
        // A tapped note on string 2 at the bar's start: its ghost is nudged; string 1's is not.
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(2), StringIndex = 2, Fret = 17 });
        arrangement.Levels.Add(level);

        var bar = Snapshot(arrangement).HandShapes[0];

        Assert.Equal(0b110, bar.GhostMask);
        Assert.Equal(5, bar.GhostFret(1));
        Assert.Equal(5, bar.GhostFret(2));
        Assert.Equal(0b100, bar.GhostNudgeMask);
    }

    [Fact]
    public void AGhostGem_IsDrawnDimmedOnItsLane_AtTheBarsStart_AndOnlyWhenTheGripHasOne()
    {
        var ghost = new RenderHandShape { Time = 300, EndTime = 700, TemplateId = 0, GhostMask = 1 << 2, GhostFrets = 5UL << 16 };
        var (batcher, rc) = Render(ghost);
        var laneY = rc.LaneTopFor(2) + LaneHeight / 2f;

        var gem = VerticesNear(batcher, 300, laneY).ToList();
        Assert.NotEmpty(gem);
        Assert.All(gem, a => Assert.InRange(a, 0.01f, 80 / 255f + 0.001f));

        // Without the ghost mark, nothing at all is drawn on that lane (the bar is below the lanes).
        var (plain, _) = Render(ghost with { GhostMask = 0 });
        Assert.Empty(VerticesNear(plain, 300, laneY));
    }

    [Fact]
    public void AGhostThatShares_ItsStringWithAPlayedGem_SitsJustBeforeTheBar()
    {
        var ghost = new RenderHandShape
        {
            Time = 300, EndTime = 700, TemplateId = 0, GhostMask = 1 << 2, GhostFrets = 5UL << 16, GhostNudgeMask = 1 << 2
        };
        var (batcher, rc) = Render(ghost);
        var laneY = rc.LaneTopFor(2) + LaneHeight / 2f;

        Assert.Empty(VerticesNear(batcher, 300, laneY, radius: 3f));
        Assert.NotEmpty(VerticesNear(batcher, 300 - HandShapeLayer.GhostNudgePx, laneY));
    }

    private static FrameSnapshot Snapshot(Arrangement arrangement)
    {
        var level = arrangement.Levels[0];
        var ctx = new SnapshotUpdateContext
        {
            ActiveBeats = Beats(),
            ActiveNotes = level.Notes,
            ActiveChords = level.Chords,
            ActiveAnchors = level.Anchors,
            ActiveHandShapes = level.HandShapes,
            ActiveTemplates = arrangement.ChordTemplates,
            HandShapeFindings = new HashSet<HandShape>()
        };
        var snapshot = new FrameSnapshot();
        FrameSnapshot.BuildSnapshot(0, 5000, 0, snapshot, ctx, []);
        return snapshot;
    }

    private static (TimelineBatcher Batcher, ChartRenderContext Rc) Render(RenderHandShape shape)
    {
        var snapshot = new FrameSnapshot { HandShapes = [shape], HandShapeCount = 1 };
        var batcher = new TimelineBatcher { FontAtlas = Atlas.Value };
        var rc = new ChartRenderContext
        {
            Width = 900,
            Height = 480,
            CurrentZoom = 1.0,
            ViewStartMs = 0,
            ViewEndMs = 900,
            TopMargin = TopMargin,
            BottomMargin = 20f,
            LaneAreaHeight = LaneHeight * 6,
            LaneHeight = LaneHeight,
            StringCount = 6,
            DrawLabels = false,
            Snapshot = snapshot
        };
        new HandShapeLayer().Render(batcher, rc);
        batcher.FinalizeFrame();
        return (batcher, rc);
    }

    /// <summary>The alpha of every general vertex within <paramref name="radius" /> px of a gem
    /// centred at (<paramref name="x" />, <paramref name="y" />).</summary>
    private static IEnumerable<float> VerticesNear(TimelineBatcher batcher, float x, float y, float radius = 14f)
    {
        var buf = batcher.GeneralBuffer;
        for (var v = 0; v < batcher.GeneralVertexCount; v++)
        {
            var i = v * 9;
            if (Math.Abs(buf[i] - x) <= radius && Math.Abs(buf[i + 1] - y) <= radius)
                yield return buf[i + 5];
        }
    }

    private static readonly Lazy<ProbeAtlas> Atlas = new(() =>
    {
        var atlas = new ProbeAtlas();
        atlas.SetChartFont("Default", 14f, isBold: false);
        atlas.SetRenderScale(1f);
        atlas.RebuildIfNeeded();
        return atlas;
    });

    /// <summary>Upload-free atlas: the base owns every metric the batcher reads.</summary>
    private sealed class ProbeAtlas : ChartFontAtlas
    {
        protected override void UploadAtlas(SkiaSharp.SKBitmap bitmap)
        {
        }

        public override void Dispose()
        {
        }
    }
}
