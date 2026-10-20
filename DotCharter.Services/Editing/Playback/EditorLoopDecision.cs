using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Services.Editing.Playback;

/// <summary>What the transport must do at the end of a loop pass.</summary>
public enum EditorLoopAction
{
    None,

    /// <summary>Come round: seek back to the loop start and keep playing.</summary>
    Wrap,

    /// <summary>The one-shot AUDITION pass is done: seek back to the start and
    /// PAUSE, so pressing the key again replays the same span.</summary>
    StopAtStart
}

public readonly record struct EditorLoopStep(EditorLoopAction Action, double SeekToMs)
{
    public static readonly EditorLoopStep None = new(EditorLoopAction.None, 0);
}

/// <summary>Tick-to-tick state of <see cref="EditorLoopDecision" />.</summary>
public readonly record struct EditorLoopTickState(bool WaitingForSeek)
{
    public static readonly EditorLoopTickState Idle = new(false);
}

/// <summary>
///     When the editor's loop comes round — PURE, so the one thing that runs at
///     250 Hz on the audio timer thread is unit-tested without an engine.
/// </summary>
public static class EditorLoopDecision
{
    /// <summary>
    ///     <paramref name="rawPlayheadMs" /> must be
    ///     <c>IAudioPlaybackService.RawPlayheadMs</c> — the song time the engine
    ///     is currently WRITING INTO the output buffer — NOT the
    ///     latency-compensated <c>PositionChanged</c>/<c>CurrentTimeMs</c> the
    ///     user is hearing. Wrapping on the audible axis means the engine has
    ///     already rendered one output latency of material PAST the loop end
    ///     before the seek lands, and the user hears it: inaudible on a tight
    ///     ASIO buffer, obvious on the ~50 ms Steam-mirror device. On the raw
    ///     axis those samples are never rendered at all, and the output latency
    ///     never enters the arithmetic, so a wrong latency estimate cannot bias
    ///     the loop point either way. Same rule as the metronome/clap scheduler.
    ///     <para>
    ///         <see cref="EditorLoopTickState.WaitingForSeek" /> is the "a wrap is
    ///         already in flight" latch: at 250 Hz a seek takes several ticks to
    ///         show up on the playhead, and without it the loop would fire a
    ///         burst of seeks per pass. It clears the moment the playhead is
    ///         observed back inside the region — the same shape as Live Play's
    ///         <c>_pendingSeekTargetMs</c> guard.
    ///     </para>
    /// </summary>
    public static (EditorLoopStep Step, EditorLoopTickState Next) Advance(
        EditorLoopTickState state,
        LivePlayLoop? region,
        bool enabled,
        bool isAudition,
        double rawPlayheadMs,
        bool isPlaying)
    {
        // No region, switched off, or parked: nothing to wrap, and the latch
        // resets so re-enabling mid-song does not swallow the first pass.
        if (region is not { } loop || !enabled || !isPlaying)
            return (EditorLoopStep.None, EditorLoopTickState.Idle);

        if (!loop.ShouldWrap(rawPlayheadMs))
            return (EditorLoopStep.None, EditorLoopTickState.Idle);

        if (state.WaitingForSeek)
            return (EditorLoopStep.None, state);

        var action = isAudition ? EditorLoopAction.StopAtStart : EditorLoopAction.Wrap;
        return (new EditorLoopStep(action, loop.StartMs), new EditorLoopTickState(true));
    }
}
