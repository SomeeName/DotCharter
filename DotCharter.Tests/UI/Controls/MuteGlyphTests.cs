using DotCharter.UI.Constants;
using DotCharter.UI.Controls.Layers.Helpers;

namespace DotCharter.Tests.UI.Controls;

/// <summary>
///     A muted note's X on the 2D timeline. It crossed the whole head, through the fret number, and a
///     grey plate behind a dark number patched it back — the lowest-contrast number on screen, with the
///     X's ends poking out of the plate (screenshot review, 2026-09-23). The X is four rim ticks now,
///     framing a clear centre, and the number is white like every other head's.
/// </summary>
public sealed class MuteGlyphTests
{
    private const float S = ChartEditorConstants.RenderMetrics.BaseNoteRadius * ChartEditorConstants.RenderMetrics.MuteIconScale;

    /// <summary>The ticks' outer stroke (4.5 px at most) never reaches a two-digit number's box.</summary>
    [Fact]
    public void TheTicks_StayClearOfTheFretNumber()
    {
        const float strokeHalf = 4.5f / 2f;

        // Distance from the box's nearest corner to the tick's centreline segment, which runs along
        // the diagonal from (from, from) to (to, to); clear when it exceeds the stroke's half width.
        var (from, to) = HighwayNoteHeadRenderer.MuteTickSpan(S);
        var cx = HighwayNoteHeadRenderer.FretNumberHalfWidthPx;
        var cy = HighwayNoteHeadRenderer.FretNumberHalfHeightPx;
        var t = Math.Clamp((cx + cy) / 2f, from, to);
        var distance = MathF.Sqrt((cx - t) * (cx - t) + (cy - t) * (cy - t));
        Assert.True(distance > strokeHalf, $"a tick starting at {from} passes {distance:0.00} px from the fret number");
    }

    [Fact]
    public void TheTicks_ReachPastTheRim_SoTheHeadReadsAsCrossed()
    {
        var (from, to) = HighwayNoteHeadRenderer.MuteTickSpan(S);
        var rim = ChartEditorConstants.RenderMetrics.BaseNoteRadius;
        Assert.True(to * MathF.Sqrt(2f) > rim, $"the ticks end at {to * MathF.Sqrt(2f)} px, inside the {rim} px rim");
        Assert.True(to - from >= 3f, "the ticks are too short to read as an X");
    }

    [Fact]
    public void AMutedHead_DrawsItsNumberWhite_WithNoPlate()
    {
        var source = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Controls", "Layers", "Helpers",
            "HighwayNoteHeadRenderer.cs")));
        Assert.Contains("MuteTickSpan(s)", source);
        Assert.DoesNotContain("batcher.AddRect(x - 6f, y - 6f, 12f, 12f", source);
    }
}
