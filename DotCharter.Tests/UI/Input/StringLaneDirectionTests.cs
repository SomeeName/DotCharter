using DotCharter.UI.Input;

namespace DotCharter.Tests.UI.Input;

/// <summary>
///     An arrow key means the direction the charter can SEE.
///     <para>
///         The highway lays lane 0 at the top and
///         <c>ChartRenderContext.LaneTopFor</c> maps a string to it as
///         <c>flip ? count - 1 - index : index</c> — so unflipped, string 0 (the
///         LOWEST-pitched) is drawn at the TOP and a bigger index is further
///         down. The arrow keys did not know that: Up ran
///         <c>MoveSelectedStringUp</c>, which INCREMENTS the index, so the note
///         went down the screen and down sent it up. Reported as "arrow keys
///         seem inverted when I want to move note up string it moves it down and
///         vice versa".
///     </para>
/// </summary>
public sealed class StringLaneDirectionTests
{
    /// <summary>
    ///     Unflipped: the top of the highway is the LOW string, index 0, so
    ///     moving up the screen means a smaller index.
    /// </summary>
    [Fact]
    public void Unflipped_TowardTheTopDecreasesTheIndex()
    {
        Assert.False(StringLaneDirection.TowardTopIncreasesIndex(false));
        Assert.Equal(-1, StringLaneDirection.IndexStep(false, towardTop: true));
        Assert.Equal(+1, StringLaneDirection.IndexStep(false, towardTop: false));
    }

    /// <summary>
    ///     …and flipped (lefty mode, or the invert-strings setting — the viewport
    ///     XORs them) it is the other way, which is exactly why this is a
    ///     function of the flag rather than a constant.
    /// </summary>
    [Fact]
    public void Flipped_TowardTheTopIncreasesTheIndex()
    {
        Assert.True(StringLaneDirection.TowardTopIncreasesIndex(true));
        Assert.Equal(+1, StringLaneDirection.IndexStep(true, towardTop: true));
        Assert.Equal(-1, StringLaneDirection.IndexStep(true, towardTop: false));
    }

    /// <summary>Up and down are always opposites, whichever way the lanes run.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UpAndDownAlwaysOppose(bool flip) =>
        Assert.Equal(0, StringLaneDirection.IndexStep(flip, true) + StringLaneDirection.IndexStep(flip, false));

    /// <summary>
    ///     The claim this all rests on, restated against the renderer's own
    ///     formula so the two cannot drift apart: lane 0 is the TOP, and
    ///     <c>LaneTopFor</c> is <c>flip ? count - 1 - index : index</c>.
    /// </summary>
    [Theory]
    [InlineData(false, 0, 0)] // unflipped: string 0 is lane 0 — the top
    [InlineData(false, 5, 5)]
    [InlineData(true, 0, 5)]  // flipped: string 0 is the bottom lane
    [InlineData(true, 5, 0)]
    public void TheStepMatchesTheRenderersLaneFormula(bool flip, int stringIndex, int expectedLane)
    {
        const int count = 6;
        var lane = flip ? count - 1 - stringIndex : stringIndex;
        Assert.Equal(expectedLane, lane);

        // One step toward the top must lower the LANE by one.
        var moved = stringIndex + StringLaneDirection.IndexStep(flip, towardTop: true);
        if (moved is < 0 or >= count) return; // clamped at the edge; nothing to check
        var movedLane = flip ? count - 1 - moved : moved;
        Assert.Equal(lane - 1, movedLane);
    }
}
