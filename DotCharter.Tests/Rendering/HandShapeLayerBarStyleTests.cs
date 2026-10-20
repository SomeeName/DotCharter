using Avalonia.Media;
using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;
using DotCharter.Rendering.Text;
using DotCharter.Rendering.Timeline2D;
using DotCharter.UI.Constants;
using DotCharter.UI.Controls.Layers;
using DotCharter.UI.Controls.Layers.Helpers;
using DotCharter.UI.Controls.Themes;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     The 2D hand-shape bar says what it is (#480): the template's name inside it,
///     an arpeggio's own colour and bracket posts, and a warning outline, flag and
///     wash on a bar the inspector has a finding against. The layer only READS the
///     snapshot (<see cref="RenderHandShape.Label" />,
///     <see cref="RenderHandShape.IsArpeggio" />, <see cref="RenderHandShape.HasFinding" />),
///     so these set it directly;
///     <see cref="HandShapeBarRenderDataTests" /> pins where those come from.
///     <para>
///         No GPU: <see cref="TimelineBatcher" /> is a plain CPU vertex builder.
///         Bars, posts, outlines and the flag are SOLID geometry (the topmost
///         triangle over a point is what shows there); glyphs are GENERAL quads,
///         so "a name was drawn" is "general vertices were emitted". The atlas is
///         a real <see cref="ChartFontAtlas" /> with the upload stubbed out, so
///         <see cref="TimelineBatcher.MeasureTextWidth" /> measures real glyphs.
///     </para>
/// </summary>
public sealed class HandShapeLayerBarStyleTests
{
    private const float TopMargin = 20f;
    private const float LaneAreaHeight = 360f;
    private const int PlainId = 0;
    private const int ArpeggioId = 1;
    private const int NoTemplate = -1;

    private static float BarY => TopMargin + LaneAreaHeight + ChartEditorConstants.RenderMetrics.HandShapeBarYOffset;

    private static float BarH => ChartEditorConstants.RenderMetrics.HandShapeActiveBarHeight;

    [Fact]
    public void APlainBar_IsTheBarColour_AndAnArpeggioBar_IsTheArpeggioColour()
    {
        var plain = Render(Shape(PlainId, 100, 400));
        var arpeggio = Render(Shape(ArpeggioId, 100, 400));

        Assert.Equal(Rgb(ChartTheme.HandShapeBarColor), Rgb(TopmostAt(plain, 250, BarY + BarH / 2)));
        Assert.Equal(Rgb(ChartTheme.HandShapeArpeggioColor), Rgb(TopmostAt(arpeggio, 250, BarY + BarH / 2)));
        Assert.NotEqual(Rgb(ChartTheme.HandShapeBarColor), Rgb(ChartTheme.HandShapeArpeggioColor));
    }

    [Fact]
    public void AnArpeggioBar_CarriesBracketPosts_APlainBarDoesNot()
    {
        // A shape cue as well as a colour: the posts rise above the bar's ends.
        var postY = BarY - HandShapeLayer.BracketPostHeight / 2f;

        var arpeggio = TopmostAt(Render(Shape(ArpeggioId, 100, 400)), 101, postY);
        var plain = TopmostAt(Render(Shape(PlainId, 100, 400)), 101, postY);

        Assert.Equal(255, arpeggio.A);
        Assert.Equal(Rgb(ChartTheme.HandShapeArpeggioColor), Rgb(arpeggio));
        Assert.True(plain.A < 255, "a plain bar has only the faint lane wash above it");
    }

    [Fact]
    public void AWideBar_DrawsItsName()
    {
        var batcher = Render(Shape(PlainId, 100, 400));

        Assert.True(batcher.GeneralVertexCount > 0, "the template name is drawn inside a bar it fits in");
        var (minX, maxX) = GlyphXRange(batcher);
        Assert.True(minX >= 100 && maxX <= 400, $"the name stays inside the bar: glyphs span {minX}..{maxX}");
    }

    [Fact]
    public void ATooNarrowBar_EmitsNoText()
    {
        // 20 px cannot hold "Cmaj7"; a cut-off name would read as another chord.
        var batcher = Render(Shape(PlainId, 100, 120));

        Assert.Equal(0, batcher.GeneralVertexCount);
        Assert.True(batcher.SolidVertexCount > 0, "the bar itself is still drawn");
    }

    [Fact]
    public void AtMarksLod_OrWithLabelsOff_NoNameIsDrawn()
    {
        Assert.Equal(0, Render(Shape(PlainId, 100, 400), lod: NoteLevelOfDetail.Marks).GeneralVertexCount);
        Assert.Equal(0, Render(Shape(PlainId, 100, 400), drawLabels: false).GeneralVertexCount);
    }

    [Fact]
    public void ABarThatBeganOffScreen_KeepsItsNameOnThePartShowing()
    {
        var batcher = Render(Shape(PlainId, -200, 300));

        Assert.True(batcher.GeneralVertexCount > 0);
        var (minX, _) = GlyphXRange(batcher);
        Assert.True(minX >= 0, $"the name starts on screen, not at the bar's off-screen start ({minX})");
    }

    [Fact]
    public void ABarWithAFinding_GetsTheWarningOutlineFlagAndWash()
    {
        var warned = Render(Shape(PlainId, 100, 400, hasFinding: true));
        var clean = Render(Shape(PlainId, 100, 400));
        var warning = Rgb(ChartTheme.HandShapeWarningColor);

        Assert.Equal(warning, Rgb(TopmostAt(warned, 100.5f, BarY + BarH / 2)));
        Assert.Equal(warning, Rgb(TopmostAt(warned, 101.5f, BarY + 1.5f)));
        Assert.Equal(warning, Rgb(TopmostAt(warned, 250, 200)));

        Assert.NotEqual(warning, Rgb(TopmostAt(clean, 100.5f, BarY + BarH / 2)));
        Assert.NotEqual(warning, Rgb(TopmostAt(clean, 250, 200)));
    }

    [Fact]
    public void ASelectedBarWithAFinding_KeepsItsFlag()
    {
        var batcher = Render(Shape(PlainId, 100, 400, hasFinding: true, selected: true));

        // The outline goes white for the selection; the flag is what still says "warning".
        Assert.Equal(Rgb(ChartTheme.WhiteSmokeColor), Rgb(TopmostAt(batcher, 100.5f, BarY + BarH - 1)));
        Assert.Equal(Rgb(ChartTheme.HandShapeWarningColor), Rgb(TopmostAt(batcher, 101.5f, BarY + 1.5f)));
    }

    [Fact]
    public void AGriplessBarWithAFinding_IsWarned_AndHasNoName()
    {
        var batcher = Render(Shape(NoTemplate, 100, 400, hasFinding: true));

        Assert.Equal(0, batcher.GeneralVertexCount);
        Assert.Equal(Rgb(ChartTheme.HandShapeWarningColor), Rgb(TopmostAt(batcher, 101.5f, BarY + 1.5f)));
    }

    [Fact]
    public void ABarBeingDrawn_PrintsItsGrip_EvenAtMarksLod_ThroughTheSameFitRule()
    {
        // #481's preview label and #480's names are ONE text path.
        var wide = Render(Shape(PlainId, 100, 400, label: "Am x02210"), lod: NoteLevelOfDetail.Marks,
            asPreview: true);
        var narrow = Render(Shape(PlainId, 100, 120, label: "Am x02210"), asPreview: true);

        Assert.True(wide.GeneralVertexCount > 0, "the grip of a bar being drawn shows at every density");
        Assert.Equal(0, narrow.GeneralVertexCount);
    }

    [Fact]
    public void ASuggestedBar_IsDashed_WhereACommittedBarIsSolid()
    {
        // #491: a proposal must never read as a shape the chart has. Its top edge alternates
        // dash and gap; a committed bar's outline is unbroken there.
        var suggested = Render(Shape(PlainId, 100, 400) with { IsSuggested = true }, asSuggested: true);
        var committed = Render(Shape(PlainId, 100, 400));
        var edgeY = BarY + ChartEditorConstants.RenderMetrics.HandShapeNormalThickness / 2f;
        var inDash = 100 + HandShapeLayer.SuggestedDash + HandShapeLayer.SuggestedGap + 2f;
        var inGap = 100 + HandShapeLayer.SuggestedDash + HandShapeLayer.SuggestedGap / 2f;

        Assert.Equal(HandShapeLayer.SuggestedDashAlpha, TopmostAt(suggested, inDash, edgeY).A);
        Assert.Equal(HandShapeLayer.SuggestedFillAlpha, TopmostAt(suggested, inGap, edgeY).A);
        Assert.Equal(255, TopmostAt(committed, inGap, edgeY).A);
    }

    [Fact]
    public void ASuggestedBar_WashesNoLane_AndPrintsItsGrip()
    {
        var suggested = Render(Shape(PlainId, 100, 400) with { IsSuggested = true }, asSuggested: true);

        Assert.Equal(0, TopmostAt(suggested, 250, 200).A);
        Assert.True(suggested.GeneralVertexCount > 0, "the grip's name shows on a bar it fits");
        Assert.Equal(0, Render(Shape(PlainId, 100, 120) with { IsSuggested = true }, asSuggested: true).GeneralVertexCount);
    }

    [Fact]
    public void ASuggestedBarFarOffTheLeftEdge_DrawsOnlyTheDashesOnScreen()
    {
        // A bar that began a minute ago must not loop over thousands of invisible dashes.
        var batcher = Render(Shape(PlainId, -60_000, 400) with { IsSuggested = true }, asSuggested: true,
            drawLabels: false);

        Assert.True(batcher.SolidVertexCount < 6 * 4 * (2 * 410 / (int)(HandShapeLayer.SuggestedDash + HandShapeLayer.SuggestedGap) + 8),
            $"{batcher.SolidVertexCount} solid vertices for a bar 400 px on screen");
    }

    [Fact]
    public void APreviewBar_NeverWearsTheWarning()
    {
        var batcher = Render(Shape(PlainId, 100, 400, hasFinding: true), asPreview: true);

        Assert.NotEqual(Rgb(ChartTheme.HandShapeWarningColor), Rgb(TopmostAt(batcher, 250, 200)));
    }

    // ── fixture ──────────────────────────────────────────────────────────────

    private static RenderHandShape Shape(int templateId, double startMs, double endMs, bool hasFinding = false,
        bool selected = false, string? label = null) =>
        new()
        {
            Time = startMs, EndTime = endMs, TemplateId = templateId, HasFinding = hasFinding,
            IsSelected = selected,
            // What the snapshot resolves: the template's name and IsArpeggio.
            Label = label ?? templateId switch { PlainId => "Cmaj7", ArpeggioId => "Am", _ => null },
            IsArpeggio = templateId == ArpeggioId
        };

    private static TimelineBatcher Render(RenderHandShape shape, NoteLevelOfDetail lod = NoteLevelOfDetail.Full,
        bool drawLabels = true, bool asPreview = false, bool asSuggested = false)
    {
        var snapshot = new FrameSnapshot();
        if (asSuggested)
        {
            snapshot.SuggestedHandShapes = [shape];
            snapshot.SuggestedHandShapeCount = 1;
        }
        else if (asPreview)
        {
            snapshot.PreviewHandShapes = [shape];
            snapshot.PreviewHandShapeCount = 1;
        }
        else
        {
            snapshot.HandShapes = [shape];
            snapshot.HandShapeCount = 1;
        }

        var batcher = new TimelineBatcher { FontAtlas = Atlas.Value };
        new HandShapeLayer().Render(batcher, new ChartRenderContext
        {
            Width = 900,
            Height = 420,
            CurrentZoom = 1.0,
            ViewStartMs = 0,
            ViewEndMs = 900,
            TopMargin = TopMargin,
            BottomMargin = 20f,
            LaneAreaHeight = LaneAreaHeight,
            LaneHeight = 60f,
            StringCount = 6,
            DrawLabels = drawLabels,
            NoteLod = lod,
            Snapshot = snapshot
        });
        batcher.FinalizeFrame();
        return batcher;
    }

    private static readonly Lazy<ProbeAtlas> Atlas = new(() =>
    {
        var atlas = new ProbeAtlas();
        atlas.SetChartFont("Default", 14f, isBold: false);
        atlas.SetRenderScale(1f);
        atlas.RebuildIfNeeded();
        return atlas;
    });

    private static (byte R, byte G, byte B) Rgb(Color c) => (c.R, c.G, c.B);

    /// <summary>The colour of the LAST solid triangle covering the point — what
    /// the frame shows there. Transparent black when nothing covers it.</summary>
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

    private static (float Min, float Max) GlyphXRange(TimelineBatcher batcher)
    {
        var buf = batcher.GeneralBuffer;
        float min = float.MaxValue, max = float.MinValue;
        for (var v = 0; v < batcher.GeneralVertexCount; v++)
        {
            var px = buf[v * 9];
            min = Math.Min(min, px);
            max = Math.Max(max, px);
        }

        return (min, max);
    }

    private static bool InTriangle(float px, float py, float ax, float ay, float bx, float by, float cx, float cy)
    {
        var d1 = (px - bx) * (ay - by) - (ax - bx) * (py - by);
        var d2 = (px - cx) * (by - cy) - (bx - cx) * (py - cy);
        var d3 = (px - ax) * (cy - ay) - (cx - ax) * (py - ay);
        var hasNeg = d1 < 0 || d2 < 0 || d3 < 0;
        var hasPos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(hasNeg && hasPos);
    }

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
