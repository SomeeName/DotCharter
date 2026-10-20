using Avalonia.Media;
using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;
using DotCharter.Rendering.Timeline2D;
using DotCharter.UI.Constants;
using DotCharter.UI.Controls.Layers;
using DotCharter.UI.Controls.Layers.Helpers;
using DotCharter.UI.Controls.Themes;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     The 2D timeline's half of the bulk-edit preview (#485): a chord or a
///     hand-shape bar a pending bulk chord edit would change wears corner brackets
///     in the EditTarget colour. The layers only READ the snapshot
///     (<see cref="ChordFlags.EditTarget" />, <see cref="RenderHandShape.IsEditTarget" />),
///     so these set it directly; <see cref="EditTargetRenderDataTests" /> pins where
///     it comes from. No GPU: <see cref="TimelineBatcher" /> is a CPU vertex builder.
/// </summary>
public sealed class EditTargetLayerTests
{
    private const double ChordTimeMs = 400;
    private const float LaneHeight = 60f;
    private const float TopMargin = 20f;
    private const float LaneAreaHeight = 360f;

    private static float BracketLeft => (float)ChordTimeMs - ChartEditorConstants.RenderMetrics.ChordPanelOffset
                                        - NoteLayer.EditTargetPad;

    private static float BracketTop => TopMargin + LaneHeight - NoteLayer.EditTargetPad; // string 1's lane top

    private static float BarY => TopMargin + LaneAreaHeight + ChartEditorConstants.RenderMetrics.HandShapeBarYOffset;

    [Theory]
    [InlineData(NoteLevelOfDetail.Full)]
    [InlineData(NoteLevelOfDetail.Marks)]
    public void AnEditTargetChord_WearsTheBrackets_AtEveryDensity(NoteLevelOfDetail lod)
    {
        var marked = RenderChord(ChordFlags.EditTarget, lod);

        Assert.Equal(Rgb(ChartTheme.EditTargetColor), Rgb(TopmostAt(marked, BracketLeft + 0.5f, BracketTop + 4f)));
        Assert.Equal(Rgb(ChartTheme.EditTargetColor), Rgb(TopmostAt(marked, BracketLeft + 4f, BracketTop + 0.5f)));
    }

    [Fact]
    public void AChordWithoutTheFlag_HasNoBrackets()
    {
        var plain = RenderChord(ChordFlags.None, NoteLevelOfDetail.Full);

        Assert.NotEqual(Rgb(ChartTheme.EditTargetColor), Rgb(TopmostAt(plain, BracketLeft + 0.5f, BracketTop + 4f)));
    }

    [Fact]
    public void AnEditTargetBar_WearsTheBrackets_AndAPreviewBarNever()
    {
        var marked = RenderBar(isEditTarget: true, asPreview: false);
        var plain = RenderBar(isEditTarget: false, asPreview: false);
        var preview = RenderBar(isEditTarget: true, asPreview: true);
        var x = 100f - NoteLayer.EditTargetPad + 0.5f;
        var y = BarY - NoteLayer.EditTargetPad + 3f;

        Assert.Equal(Rgb(ChartTheme.EditTargetColor), Rgb(TopmostAt(marked, x, y)));
        Assert.NotEqual(Rgb(ChartTheme.EditTargetColor), Rgb(TopmostAt(plain, x, y)));
        Assert.NotEqual(Rgb(ChartTheme.EditTargetColor), Rgb(TopmostAt(preview, x, y)));
    }

    /// <summary>
    ///     The AI chart pass review (#506): an anchor it would ADD is drawn in the EditTarget
    ///     colour, on top; the live anchor it replaces is drawn FADED in its own colour; an
    ///     anchor it leaves alone is drawn as ever.
    /// </summary>
    [Fact]
    public void AProposedAnchor_IsDrawnInTheEditTargetColour_OverTheFadedOneItReplaces()
    {
        var snapshot = new FrameSnapshot
        {
            Anchors =
            [
                new RenderAnchor { Time = 100, EndTime = 300, Fret = 5, Width = 4 },
                new RenderAnchor { Time = 300, EndTime = 600, Fret = 3, Width = 4, IsEditTarget = true }
            ],
            AnchorCount = 2,
            ProposedAnchors = [new RenderAnchor { Time = 500, EndTime = 800, Fret = 7, Width = 4, IsEditTarget = true }],
            ProposedAnchorCount = 1
        };
        var batcher = new TimelineBatcher();
        var rc = Context(snapshot, NoteLevelOfDetail.Full);
        rc.FhpY = 10f;
        new FretAnchorLayer().Render(batcher, rc);
        var midY = rc.FhpY + ChartEditorConstants.RenderMetrics.AnchorTailHeight * 0.5f;
        var tickY = midY - FretAnchorLayer.TickHeight * 0.5f + 1f; // above the track, inside the tick

        var kept = TopmostAt(batcher, 101f, tickY);
        var replaced = TopmostAt(batcher, 301f, tickY);
        var proposed = TopmostAt(batcher, 501f, tickY);

        Assert.Equal(Rgb(ChartTheme.AnchorColor), Rgb(kept));
        Assert.Equal(Rgb(ChartTheme.AnchorColor), Rgb(replaced));
        Assert.True(replaced.A < kept.A, $"the replaced anchor is not faded ({replaced.A} vs {kept.A})");
        Assert.Equal(Rgb(ChartTheme.EditTargetColor), Rgb(proposed));
        Assert.Equal(kept.A, proposed.A);
    }

    [Fact]
    public void TheEditTargetHue_IsNotTheSelectionsTheWarningsOrTheArpeggios()
    {
        var target = Rgb(ChartTheme.EditTargetColor);
        Assert.NotEqual(target, Rgb(ChartTheme.WhiteSmokeColor));
        Assert.NotEqual(target, Rgb(ChartTheme.HandShapeWarningColor));
        Assert.NotEqual(target, Rgb(ChartTheme.HandShapeArpeggioColor));
    }

    private static TimelineBatcher RenderChord(ChordFlags flags, NoteLevelOfDetail lod)
    {
        var snapshot = new FrameSnapshot
        {
            StringCount = 6,
            ChordNotes =
            [
                new RenderChordNote { StringIndex = 1, Fret = 3 },
                new RenderChordNote { StringIndex = 2, Fret = 5 },
                new RenderChordNote { StringIndex = 3, Fret = 5 }
            ],
            ChordNoteCount = 3,
            Chords = [new RenderChord { Time = ChordTimeMs, NoteOffset = 0, NoteCount = 3, TemplateId = 1, Flags = flags }],
            ChordCount = 1
        };
        var batcher = new TimelineBatcher();
        new NoteLayer().Render(batcher, Context(snapshot, lod));
        return batcher;
    }

    private static TimelineBatcher RenderBar(bool isEditTarget, bool asPreview)
    {
        var shape = new RenderHandShape { Time = 100, EndTime = 400, TemplateId = 0, IsEditTarget = isEditTarget };
        var snapshot = new FrameSnapshot();
        if (asPreview)
        {
            snapshot.PreviewHandShapes = [shape];
            snapshot.PreviewHandShapeCount = 1;
        }
        else
        {
            snapshot.HandShapes = [shape];
            snapshot.HandShapeCount = 1;
        }

        var batcher = new TimelineBatcher();
        new HandShapeLayer().Render(batcher, Context(snapshot, NoteLevelOfDetail.Full));
        return batcher;
    }

    private static ChartRenderContext Context(FrameSnapshot snapshot, NoteLevelOfDetail lod) =>
        new()
        {
            Width = 900,
            Height = 420,
            CurrentZoom = 1.0,
            ViewStartMs = 0,
            ViewEndMs = 900,
            TopMargin = TopMargin,
            BottomMargin = 20f,
            LaneAreaHeight = LaneAreaHeight,
            LaneHeight = LaneHeight,
            StringCount = 6,
            DrawLabels = false,
            NoteLod = lod,
            Snapshot = snapshot
        };

    private static (byte R, byte G, byte B) Rgb(Color c) => (c.R, c.G, c.B);

    /// <summary>The colour of the LAST solid triangle covering the point.</summary>
    private static Color TopmostAt(TimelineBatcher batcher, float x, float y)
    {
        var buf = batcher.SolidBuffer;
        var found = Colors.Transparent;
        for (var v = 0; v + 2 < batcher.SolidVertexCount; v += 3)
        {
            var i = v * 6;
            if (!InTriangle(x, y, buf[i], buf[i + 1], buf[i + 6], buf[i + 7], buf[i + 12], buf[i + 13])) continue;
            found = Color.FromArgb(ToByte(buf[i + 5]), ToByte(buf[i + 2]), ToByte(buf[i + 3]), ToByte(buf[i + 4]));
        }

        return found;
    }

    private static byte ToByte(float channel) => (byte)Math.Round(channel * 255f);

    private static bool InTriangle(float px, float py, float ax, float ay, float bx, float by, float cx, float cy)
    {
        var d1 = (px - bx) * (ay - by) - (ax - bx) * (py - by);
        var d2 = (px - cx) * (by - cy) - (bx - cx) * (py - cy);
        var d3 = (px - ax) * (cy - ay) - (cx - ax) * (py - ay);
        var hasNeg = d1 < 0 || d2 < 0 || d3 < 0;
        var hasPos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(hasNeg && hasPos);
    }
}
