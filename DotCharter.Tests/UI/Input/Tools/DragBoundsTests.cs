using System.Text.RegularExpressions;
using DotCharter.UI.Input.Tools;

namespace DotCharter.Tests.UI.Input.Tools;

/// <summary>
///     A timeline drag moves its payload RIGIDLY, so the only correct answer when
///     the earliest member would land before the start of the song is to shorten
///     the DELTA for everyone.
///     <para>
///         Two of the four drag paths clamped each item on its own
///         (<c>Max(Zero, original + delta)</c>): the highway drag — notes, chords,
///         hand shapes and anchors — and the section/phrase marker drag. Dragging
///         a run off the front of the song piled every member that went negative
///         onto beat 0 while the rest kept moving, and the preview is what the
///         release COMMITS, so the selection came back with its internal spacing
///         gone. The other two clamped the delta but each carried a private
///         <c>new FractionalPosition(999999)</c> sentinel tested with
///         <c>minTime.BeatId != 999999</c>.
///     </para>
/// </summary>
public sealed class DragBoundsTests
{
    private static FractionalPosition At(int beat) => new(beat);

    [Fact]
    public void ADeltaThatKeepsEverythingOnTheTimeline_IsUnchanged()
    {
        var delta = At(3);
        Assert.Equal(delta, DragBounds.ClampDeltaToStart(delta, At(4)));
    }

    [Fact]
    public void ADeltaThatWouldCarryTheEarliestPastZero_IsShortenedToLandItOnZero()
    {
        // Earliest at beat 2, dragged 5 beats left: the run may only move 2.
        var clamped = DragBounds.ClampDeltaToStart(FractionalPosition.Zero - At(5), At(2));
        Assert.Equal(FractionalPosition.Zero - At(2), clamped);
        Assert.Equal(FractionalPosition.Zero, At(2) + clamped);
    }

    /// <summary>The whole point: every member keeps its offset from the one that
    /// hit the wall. This is what a per-item floor destroyed.</summary>
    [Fact]
    public void TheRunKeepsItsSpacingWhenItHitsTheStart()
    {
        FractionalPosition[] run = [At(2), At(6), At(11)];
        var clamped = DragBounds.ClampDeltaToStart(FractionalPosition.Zero - At(5), DragBounds.Earliest(run));

        var moved = run.Select(p => p + clamped).ToArray();
        Assert.Equal(FractionalPosition.Zero, moved[0]);
        Assert.Equal(At(4), moved[1]);
        Assert.Equal(At(9), moved[2]);

        // …and the per-item floor the tools used to apply does NOT.
        var floored = run.Select(p => FractionalPosition.Max(FractionalPosition.Zero,
            p + (FractionalPosition.Zero - At(5)))).ToArray();
        Assert.Equal(FractionalPosition.Zero, floored[0]);
        Assert.Equal(At(1), floored[1]); // 4 beats of spacing became 1
    }

    [Fact]
    public void AnEmptyPayload_ClampsNothing()
    {
        var delta = FractionalPosition.Zero - At(9);
        Assert.Null(DragBounds.Earliest([], Array.Empty<FractionalPosition>()));
        Assert.Equal(delta, DragBounds.ClampDeltaToStart(delta, null));
    }

    [Fact]
    public void Earliest_TakesTheSmallestAcrossEveryGroup()
    {
        var earliest = DragBounds.Earliest(
            [At(9), At(4)],
            [],
            [At(7), At(2), At(30)]);

        Assert.Equal(At(2), earliest);
    }

    /// <summary>
    ///     A position at beat 999,999 is a legal (if absurd) position, and the
    ///     sentinel these tools used to carry treated it as "no payload" — the
    ///     clamp switched itself off.
    /// </summary>
    [Fact]
    public void ThePayloadIsNotDetectedByASentinelBeat()
    {
        var earliest = DragBounds.Earliest([At(999999)]);
        Assert.Equal(At(999999), earliest);

        var clamped = DragBounds.ClampDeltaToStart(FractionalPosition.Zero - At(1_000_000), earliest);
        Assert.Equal(FractionalPosition.Zero, At(999999) + clamped);
    }

    /// <summary>
    ///     The mechanical half: no drag path may floor an item on its own again.
    ///     All four go through the shared clamp, and the two private sentinels are
    ///     gone.
    /// </summary>
    [Theory]
    [InlineData("DotCharter.UI/Input/Tools/HighwayInteractionTool.cs")]
    [InlineData("DotCharter.UI/Input/Tools/HandShapeInteractionTool.cs")]
    [InlineData("DotCharter.UI/Input/Tools/FretAnchorInteractionTool.cs")]
    [InlineData("DotCharter.UI/Input/Tools/EventInteractionGestures/EventPointDragGesture.cs")]
    public void EveryDragPath_ClampsTheDeltaThroughTheSharedBounds(string relativePath)
    {
        var source = Read(relativePath).Replace("\r\n", "\n");

        Assert.Contains("DragBounds.ClampDeltaToStart", source);
        Assert.DoesNotContain("999999", source);

        // The per-item floor, in any spelling: Max(Zero, <anything> + delta).
        var perItemFloor = Regex.Match(source,
            @"FractionalPosition\.Max\(FractionalPosition\.Zero,[^;]*\+\s*delta");
        Assert.False(perItemFloor.Success,
            $"{relativePath} still floors an individual item at zero: {perItemFloor.Value}");
    }

    private static string Read(string relative) =>
        UiSourceTree.ReadAllText(Path.Combine(TestPaths.RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));
}
