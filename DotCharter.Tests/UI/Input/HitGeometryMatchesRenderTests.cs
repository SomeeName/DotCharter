using DotCharter.UI.Constants;

namespace DotCharter.Tests.UI.Input;

/// <summary>
///     P2-6: the hit-test rects must cover what the renderer DRAWS. The old
///     literals were smaller than the drawn note — a 20x16 box under a ~27 px
///     gem, an 8 px strip under a 15 px sustain tube — so the outer ring of
///     every gem read as empty highway (arming the marquee, clearing the
///     selection) and the 6 px near-miss backstop was quietly doing the hit
///     test's job. The hit constants are DERIVED from the render metrics now;
///     these tests pin the derivation so a metric change cannot re-open a gap.
/// </summary>
public sealed class HitGeometryMatchesRenderTests
{
    [Fact]
    public void TheNoteHeadHitBox_CoversTheDrawnGem()
    {
        // The gem's screen footprint is the outer stroke circle.
        var drawnDiameter = 2.0 * (ChartEditorConstants.RenderMetrics.BaseNoteRadius +
                                   ChartEditorConstants.RenderMetrics.NoteStrokeThickness / 2.0);

        Assert.Equal(ChartEditorConstants.NoteHeadWidth, drawnDiameter);
        Assert.Equal(ChartEditorConstants.NoteHeadHeight, drawnDiameter);
        // Centred on the gem centre — an off-centre box hits harder on one side.
        Assert.Equal(-ChartEditorConstants.NoteHeadWidth / 2.0, ChartEditorConstants.NoteHeadOffsetX);
        Assert.Equal(-ChartEditorConstants.NoteHeadHeight / 2.0, ChartEditorConstants.NoteHeadOffsetY);
    }

    [Fact]
    public void TheChordHeadHitBox_MatchesTheNoteGem()
    {
        Assert.Equal(ChartEditorConstants.NoteHeadWidth, ChartEditorConstants.ChordHeadWidth);
        Assert.Equal(ChartEditorConstants.NoteHeadHeight, ChartEditorConstants.ChordHeadHeight);
        Assert.Equal(-ChartEditorConstants.ChordHeadWidth / 2.0, ChartEditorConstants.ChordHeadOffsetX);
    }

    [Fact]
    public void TheSustainHitStrip_CoversTheDrawnTube()
    {
        // HighwaySustainRenderer draws y-SustainYOffset .. y+SustainYOffset.
        Assert.Equal(ChartEditorConstants.RenderMetrics.SustainDrawHeight, ChartEditorConstants.SustainHeight);
        Assert.Equal(-(double)ChartEditorConstants.RenderMetrics.SustainYOffset, ChartEditorConstants.SustainOffsetY);
        Assert.Equal(2.0 * ChartEditorConstants.RenderMetrics.SustainYOffset,
            ChartEditorConstants.RenderMetrics.SustainDrawHeight);
    }

    /// <summary>The near-miss tolerance stays a BACKSTOP: small relative to
    /// the head itself. If it ever rivals the head radius, hits are being
    /// decided by the backstop again.</summary>
    [Fact]
    public void TheNearMissTolerance_StaysABackstop()
    {
        Assert.True(ChartEditorConstants.NearMissHitTolerance <
                    ChartEditorConstants.RenderMetrics.BaseNoteRadius,
            "NearMissHitTolerance crept up to the gem radius — the backstop is doing the hit test's job again.");
    }
}
