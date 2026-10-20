using DotCharter.Rendering.Batching;
using DotCharter.Rendering.Components;
using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;
using DotCharter.Rendering.Scene;
using DotCharter.Core.Graphics;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     There is exactly ONE look-ahead notion on the 3D highway,
///     <see cref="RenderContext.HorizonMs" />. `HighwayRenderComponent` culled hand
///     shapes against a hardcoded <c>+ 10000</c> while
///     <see cref="HighwaySpeedPolicy.MaxHorizonMs" /> is 12 000, so at max draw
///     distance with a slow scroll speed a bracket between 10 s and 12 s of
///     look-ahead was dropped while the board was still showing that stretch —
///     the bracket popped in mid-board instead of flying in from the horizon.
///     <para>
///         No GPU: <see cref="QuadBatcher" /> only stages floats until it is
///         flushed, and these fixtures stay far below its capacity.
///     </para>
/// </summary>
public sealed class HighwayHorizonCullTests
{
    [Theory]
    [InlineData(11_000, true)]  // inside the 12 000 ms horizon; the old literal culled it
    [InlineData(13_000, false)] // genuinely past the horizon
    public void A_hand_shape_is_drawn_exactly_while_it_is_inside_the_horizon(double timeMs, bool expectDrawn)
    {
        var withShape = StagedFloats(HandShape(timeMs));
        var withoutShape = StagedFloats(null);

        if (expectDrawn)
        {
            Assert.True(withShape > withoutShape,
                $"a hand shape at +{timeMs} ms is inside the {HighwaySpeedPolicy.MaxHorizonMs} ms horizon " +
                "and must be drawn");
        }
        else
        {
            Assert.Equal(withoutShape, withShape);
        }
    }

    [Fact]
    public void The_hand_shape_window_is_the_shared_horizon_not_a_literal()
    {
        // Straddle the old literal: 10 001 ms is past `+ 10000` but well inside
        // the real horizon, so this is the exact case the literal broke.
        var withShape = StagedFloats(HandShape(10_001));
        var withoutShape = StagedFloats(null);

        Assert.True(HighwaySpeedPolicy.MaxHorizonMs > 10_000f,
            "the horizon must still exceed the removed literal, or this test proves nothing");
        Assert.True(withShape > withoutShape);
    }

    // ---- fixtures -----------------------------------------------------------

    private static RenderHandShape HandShape(double timeMs) =>
        new() { Time = timeMs, EndTime = timeMs + 500, TemplateId = -1 };

    /// <summary>Renders one highway frame at the widest possible horizon and
    /// returns what the pass staged. Only the hand shape differs between runs,
    /// so the delta IS the hand-shape geometry.</summary>
    private static int StagedFloats(RenderHandShape? handShape)
    {
        var snapshot = new FrameSnapshot { StringCount = 6 };
        if (handShape is { } hs)
        {
            snapshot.HandShapes = [hs];
            snapshot.HandShapeCount = 1;
        }

        // Max scroll speed + max draw distance == MaxHorizonMs (12 000 ms).
        const float scrollSpeed = HighwaySpeedPolicy.MaxScrollSpeed;
        const float drawDistance = HighwaySpeedPolicy.MaxDrawDistanceZ;

        var batcher = new QuadBatcher(null!); // never flushed; nothing reaches the device
        var context = new RenderContext
        {
            Snapshot = snapshot,
            TimeMs = 0,
            BoardWidth = 10f,
            QuadBatcher = batcher,
            TimeToZScale = HighwaySpeedPolicy.TimeToZScale(scrollSpeed),
            FadeEndZ = drawDistance,
            FadeStartZ = HighwaySpeedPolicy.FadeStartZ(drawDistance),
            HorizonMs = HighwaySpeedPolicy.HorizonMs(scrollSpeed, drawDistance)
        };

        new HighwayRenderComponent().Render(context);
        return batcher.StagedFloatCount;
    }
}
