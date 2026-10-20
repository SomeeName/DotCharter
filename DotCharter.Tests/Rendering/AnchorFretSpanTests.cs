using DotCharter.Rendering.Batching;
using DotCharter.Rendering.Components;
using DotCharter.Rendering.Components.NoteRendering;
using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;
using DotCharter.Rendering.Scene;
using DotCharter.Core.Graphics;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     Every part of the highway that draws "where the hand is" must agree, and
///     the rule is CLAMP THE FRET FIRST, then derive the width from the clamped
///     value.
///     <para>
///         Six call sites re-derived that by hand. Three clamped only the lower
///         bound (<c>Math.Max(1, fret)</c> paired with
///         <c>Math.Min(24, fret + width - 1)</c>), so a fret-0 anchor made the
///         FHP lane fill, the anchor sweep and the fret-number rail ONE FRET
///         NARROWER than the beat lines, the chord box, the open-note bar and
///         the camera. Two clamped nothing and drew the hand-shape box and the
///         arpeggio bracket from <c>GetLinearFretPosition(-1)</c> — a whole
///         fret to the LEFT of the nut. A fret-0 anchor is malformed but three
///         importers admit one (see <see cref="HighwayLaneOpacity" />, extracted
///         after a fret-0 anchor threw the whole frame away), and the renderer
///         must never be what enforces chart validity.
///     </para>
/// </summary>
public sealed class AnchorFretSpanTests
{
    [Theory]
    [InlineData(0, 4, 1, 4)]   // the malformed one: clamps to first position, KEEPS its four frets
    [InlineData(1, 4, 1, 4)]   // …and is therefore indistinguishable from a fret-1 anchor
    [InlineData(5, 4, 5, 8)]
    [InlineData(5, 0, 5, 5)]   // zero width is one fret, not a negative range
    [InlineData(23, 4, 23, 24)] // clipped at the top of the neck
    [InlineData(30, 4, 24, 24)] // past the neck entirely — still a valid single fret
    public void The_range_clamps_the_fret_before_deriving_the_width(
        int fret, int width, int expectedMin, int expectedMax) => Assert.Equal((expectedMin, expectedMax), AnchorFretSpan.FretRange(fret, width));

    [Fact]
    public void A_fret_zero_anchor_is_the_same_span_as_a_fret_one_anchor()
    {
        Assert.Equal(AnchorFretSpan.WorldSpan(1, 4), AnchorFretSpan.WorldSpan(0, 4));
        Assert.Equal((0f, 4f), AnchorFretSpan.WorldSpan(0, 4));
    }

    [Fact]
    public void The_span_never_starts_before_the_nut()
    {
        for (var fret = 0; fret <= 30; fret++)
        {
            for (var width = 0; width <= 8; width++)
            {
                var (x0, x1) = AnchorFretSpan.WorldSpan(fret, width);
                Assert.True(x0 >= 0f, $"anchor {fret}/{width} started at {x0}, left of the nut");
                Assert.True(x1 > x0, $"anchor {fret}/{width} produced an empty span");
            }
        }
    }

    // ---- the call sites now agree ------------------------------------------

    [Fact]
    public void The_fhp_lane_fill_covers_the_whole_anchor_on_a_fret_zero_anchor()
    {
        // The lane fill is the only pass drawing at this y, so filtering the
        // staged vertices by it isolates exactly that geometry.
        var withFretZero = FhpLaneFillXRange(anchorFret: 0);
        var withFretOne = FhpLaneFillXRange(anchorFret: 1);

        Assert.Equal(withFretOne, withFretZero);
        Assert.Equal((0f, 4f), withFretZero); // four frets, nut to fret 4 — not three
    }

    [Fact]
    public void The_arpeggio_bracket_starts_at_the_nut_not_a_fret_before_it()
    {
        var (min, max) = ArpeggioBracketXRange(anchorFret: 0);

        Assert.True(min >= 0f, $"the bracket's nut-side post was drawn at x={min}, left of the nut");
        Assert.Equal(ArpeggioBracketXRange(anchorFret: 1), (min, max));
    }

    [Fact]
    public void The_arpeggio_bracket_spans_the_same_frets_as_the_chord_box()
    {
        // NoteRenderMath.GetAnchorSpan is what the chord box, the open-note bar
        // and the beat lines are drawn from; the bracket must not disagree.
        var context = Context(new QuadBatcher(null!), anchorFret: 0, arpeggio: true);
        var (boxX0, boxX1) = NoteRenderMath.GetAnchorSpan(context, HandShapeTimeMs);

        var (min, max) = ArpeggioBracketXRange(anchorFret: 0);

        // The bracket posts are `thickness` wide and sit INSIDE the span edges,
        // so the outer silhouette is the span itself.
        Assert.Equal(boxX0, min, 3);
        Assert.Equal(boxX1, max, 3);
    }

    // ---- fixtures -----------------------------------------------------------

    private const double HandShapeTimeMs = 1_000;

    /// <summary>y of `HighwayRenderComponent.RenderFhpSpans`' quads — unique to
    /// that pass (the fret lines sit at 0, the beat lines at +0.0001).</summary>
    private const float FhpLaneY = FretboardConstants.ChartboardYPosition - 0.001f;

    private static (float min, float max) FhpLaneFillXRange(int anchorFret)
    {
        var batcher = new QuadBatcher(null!);
        new HighwayRenderComponent().Render(Context(batcher, anchorFret, arpeggio: false));
        return XRangeAtY(batcher, FhpLaneY);
    }

    private static (float min, float max) ArpeggioBracketXRange(int anchorFret)
    {
        var batcher = new QuadBatcher(null!);
        HandShapeRenderer.Render(Context(batcher, anchorFret, arpeggio: true));
        return XRangeAtY(batcher, null);
    }

    private static (float min, float max) XRangeAtY(QuadBatcher batcher, float? y)
    {
        var data = batcher.StagedVertices;
        var min = float.MaxValue;
        var max = float.MinValue;
        for (var i = 0; i < data.Length; i += QuadBatcher.FloatsPerVertex)
        {
            if (y.HasValue && Math.Abs(data[i + 1] - y.Value) > 1e-6f) continue;
            if (data[i] < min) min = data[i];
            if (data[i] > max) max = data[i];
        }

        Assert.True(max > min, "the pass staged no geometry — the fixture is not exercising it");
        return (min, max);
    }

    private static RenderContext Context(QuadBatcher batcher, int anchorFret, bool arpeggio)
    {
        var template = new RenderChordTemplate { Id = 0, Name = "Am", Arpeggio = arpeggio };
        var snapshot = new FrameSnapshot
        {
            StringCount = 6,
            Anchors = [new RenderAnchor { Time = 0, EndTime = 100_000, Fret = (byte)anchorFret, Width = 4 }],
            AnchorCount = 1,
            HandShapes = [new RenderHandShape { Time = HandShapeTimeMs, EndTime = 2_000, TemplateId = 0 }],
            HandShapeCount = 1,
            Templates = [template],
            TemplateCount = 1
        };

        return new RenderContext
        {
            Snapshot = snapshot,
            TimeMs = 0,
            BoardWidth = 10f,
            QuadBatcher = batcher,
            TimeToZScale = HighwaySpeedPolicy.TimeToZScale(HighwaySpeedPolicy.MaxScrollSpeed),
            FadeEndZ = HighwaySpeedPolicy.MaxDrawDistanceZ,
            FadeStartZ = HighwaySpeedPolicy.FadeStartZ(HighwaySpeedPolicy.MaxDrawDistanceZ),
            HorizonMs = HighwaySpeedPolicy.HorizonMs(
                HighwaySpeedPolicy.MaxScrollSpeed, HighwaySpeedPolicy.MaxDrawDistanceZ)
        };
    }
}
