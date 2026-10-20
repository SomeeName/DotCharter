using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Editing.Playback;

namespace DotCharter.Tests.Services.Editing.Playback;

/// <summary>
///     When the editor's loop comes round. Pure, so the one thing that runs at
///     250 Hz on the audio timer thread is tested without an engine.
/// </summary>
public sealed class EditorLoopDecisionTests
{
    private static readonly LivePlayLoop Region = new(1000, 3000);

    [Fact]
    public void InsideTheRegionNothingHappens()
    {
        var (step, next) = Advance(EditorLoopTickState.Idle, rawPlayheadMs: 2000);

        Assert.Equal(EditorLoopAction.None, step.Action);
        Assert.False(next.WaitingForSeek);
    }

    [Fact]
    public void ReachingTheEndWrapsToTheStart()
    {
        var (step, next) = Advance(EditorLoopTickState.Idle, rawPlayheadMs: 3000);

        Assert.Equal(EditorLoopAction.Wrap, step.Action);
        Assert.Equal(1000, step.SeekToMs);
        Assert.True(next.WaitingForSeek);
    }

    [Fact]
    public void AWrapAlreadyInFlightDoesNotFireASecondSeek()
    {
        // The position timer runs at up to 250 Hz and a seek takes several ticks
        // to show up on the playhead; without the latch one pass would issue a
        // burst of seeks. Same guard as Live Play's _pendingSeekTargetMs.
        var (_, afterFirst) = Advance(EditorLoopTickState.Idle, rawPlayheadMs: 3000);
        var (step, next) = Advance(afterFirst, rawPlayheadMs: 3010);

        Assert.Equal(EditorLoopAction.None, step.Action);
        Assert.True(next.WaitingForSeek);
    }

    [Fact]
    public void TheLatchClearsOnceTheSeekHasLanded()
    {
        var (_, afterFirst) = Advance(EditorLoopTickState.Idle, rawPlayheadMs: 3000);
        var (_, afterLanding) = Advance(afterFirst, rawPlayheadMs: 1005);

        Assert.False(afterLanding.WaitingForSeek);

        var (step, _) = Advance(afterLanding, rawPlayheadMs: 3000);
        Assert.Equal(EditorLoopAction.Wrap, step.Action);
    }

    [Fact]
    public void APausedTransportNeverWraps()
    {
        var (step, _) = Advance(EditorLoopTickState.Idle, rawPlayheadMs: 5000, isPlaying: false);

        Assert.Equal(EditorLoopAction.None, step.Action);
    }

    [Fact]
    public void ADisabledLoopNeverWrapsAndClearsTheLatch()
    {
        var (step, next) = Advance(new EditorLoopTickState(true), rawPlayheadMs: 5000, enabled: false);

        Assert.Equal(EditorLoopAction.None, step.Action);
        Assert.False(next.WaitingForSeek);
    }

    [Fact]
    public void WithNoRegionNothingHappens()
    {
        var (step, _) = EditorLoopDecision.Advance(
            EditorLoopTickState.Idle, null, true, false, 5000, true);

        Assert.Equal(EditorLoopAction.None, step.Action);
    }

    [Fact]
    public void AnAuditionStopsAtTheStartInsteadOfWrapping()
    {
        // One pass: seek back to the start AND pause, so pressing the key again
        // replays the same span without a manual seek.
        var (step, _) = Advance(EditorLoopTickState.Idle, rawPlayheadMs: 3000, isAudition: true);

        Assert.Equal(EditorLoopAction.StopAtStart, step.Action);
        Assert.Equal(1000, step.SeekToMs);
    }

    [Fact]
    public void PlaybackStartingBeforeTheRegionStillRunsIntoIt()
    {
        // Standard loop behaviour: the transport is free to enter from outside
        // and comes round at the end like any other pass.
        var (step, _) = Advance(EditorLoopTickState.Idle, rawPlayheadMs: 200);

        Assert.Equal(EditorLoopAction.None, step.Action);
    }

    private static (EditorLoopStep Step, EditorLoopTickState Next) Advance(
        EditorLoopTickState state, double rawPlayheadMs,
        bool enabled = true, bool isAudition = false, bool isPlaying = true) => EditorLoopDecision.Advance(state, Region, enabled, isAudition, rawPlayheadMs, isPlaying);
}
