using System.Reflection;
using DotCharter.UI.Input;

namespace DotCharter.Tests.UI.Input;

public class MinimapDragControllerTests
{
    [Fact]
    public void ComputeTargetMs_ClampsBelowZero()
    {
        var ms = InvokeCompute(-50, 1000, 200000);
        Assert.Equal(0, ms);
    }

    [Fact]
    public void ComputeTargetMs_ClampsAboveOne()
    {
        var ms = InvokeCompute(2000, 1000, 200000);
        Assert.Equal(200000, ms);
    }

    [Fact]
    public void ComputeTargetMs_LinearInterpolatesMid()
    {
        var ms = InvokeCompute(500, 1000, 200000);
        Assert.Equal(100000, ms);
    }

    [Fact]
    public void ComputeTargetMs_ReturnsNegativeForZeroWidth()
    {
        var ms = InvokeCompute(500, 0, 200000);
        Assert.True(ms < 0);
    }

    [Fact]
    public void ComputeTargetMs_ReturnsNegativeForZeroLength()
    {
        var ms = InvokeCompute(500, 1000, 0);
        Assert.True(ms < 0);
    }

    /// <summary>
    ///     The 60 Hz throttle drops moves, and the drag has to END where the user
    ///     let go rather than at the last move that happened to land on a slot
    ///     boundary.
    ///     <para>
    ///         Release used to never re-seek, on the reasoning that "the last move
    ///         was already at the drop position". Moves arrive at the mouse poll
    ///         rate — 1000 Hz on a gaming mouse — so the move just before the button
    ///         comes up is almost always inside the window and is discarded. The
    ///         transport then finished up to 16 ms of pointer travel behind the
    ///         pointer, which on a five-minute strip is seconds of chart and reads
    ///         as the minimap snapping back.
    ///     </para>
    /// </summary>
    [Fact]
    public void A_move_dropped_by_the_throttle_is_flushed_on_release()
    {
        var c = new MinimapDragController(() => 0);

        c.BeginDrag(1000);
        Assert.False(c.TryTakeMoveSlot(1005)); // inside the 16 ms window: dropped
        Assert.True(c.TakePendingSeek());
    }

    /// <summary>…and a release that dropped nothing issues nothing. A third
    /// identical seek at the same position once lined up with an ASIO callback and
    /// caused a 45 ms dropout, which is why this is conditional.</summary>
    [Fact]
    public void A_release_after_a_move_that_did_seek_does_not_seek_again()
    {
        var c = new MinimapDragController(() => 0);

        c.BeginDrag(1000);
        Assert.True(c.TryTakeMoveSlot(1020)); // past the window: seeks
        Assert.False(c.TakePendingSeek());
    }

    /// <summary>A press seeks, so a drag that is released without moving at all has
    /// nothing pending either.</summary>
    [Fact]
    public void A_press_and_release_with_no_move_seeks_once()
    {
        var c = new MinimapDragController(() => 0);

        c.BeginDrag(1000);
        Assert.False(c.TakePendingSeek());
    }

    /// <summary>The flush is CONSUMING — a release can only ever fire one, and the
    /// flag may not survive into the next drag.</summary>
    [Fact]
    public void The_pending_flush_is_consumed_and_does_not_leak_into_the_next_drag()
    {
        var c = new MinimapDragController(() => 0);

        c.BeginDrag(1000);
        Assert.False(c.TryTakeMoveSlot(1005));
        Assert.True(c.TakePendingSeek());
        Assert.False(c.TakePendingSeek());

        c.BeginDrag(2000);
        Assert.False(c.TakePendingSeek());
    }

    /// <summary>A run of dropped moves is still ONE flush, at the last position —
    /// the throttle coalesces, it does not queue.</summary>
    [Fact]
    public void A_run_of_dropped_moves_flushes_once()
    {
        var c = new MinimapDragController(() => 0);

        c.BeginDrag(1000);
        for (var t = 1001; t < 1016; t++) Assert.False(c.TryTakeMoveSlot(t));

        Assert.True(c.TakePendingSeek());
        Assert.False(c.TakePendingSeek());
    }

    private static double InvokeCompute(double pointerX, double width, double totalMs)
    {
        var method = typeof(MinimapDragController).GetMethod("ComputeTargetMs",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return (double)method.Invoke(null, [pointerX, width, totalMs])!;
    }
}
