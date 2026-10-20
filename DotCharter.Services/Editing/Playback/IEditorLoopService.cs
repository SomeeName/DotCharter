using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Services.Editing.Playback;

/// <summary>
///     The editor's A/B loop region and per-selection audition (v0.35, editing
///     rework stage 3). Charting a hard passage means hearing it repeatedly;
///     the editor had no loop at all — <c>LoopStart</c>/<c>LoopEnd</c> existed
///     only inside <c>LivePlayViewModel</c>, behind a live-play session.
///     <para>
///         It REUSES that plumbing rather than duplicating it: the region is a
///         <see cref="LivePlayLoop" /> and it is normalized and wrap-tested by
///         that type's own <c>TryCreate</c>/<c>ShouldWrap</c>, which
///         <c>LivePlaySessionService</c> now also goes through. What is NOT
///         shared is the session brain — count-in, passes, the speed ladder and
///         dynamic difficulty are gameplay, and the editor loop is a plain
///         transport loop.
///     </para>
///     <para>
///         Region state lives on <c>ProjectState</c> so the toolbar can bind it
///         and the 2D timeline can DRAW it. This service is its only writer.
///     </para>
/// </summary>
public interface IEditorLoopService
{
    /// <summary>The marked region, null until both points are punched in.</summary>
    LivePlayLoop? Region { get; }

    /// <summary>True while the region is actually looping the transport.</summary>
    bool IsEnabled { get; }

    /// <summary>Punch-in: arms the loop start at <paramref name="timeMs" />.
    /// Re-arming replaces any previous pending start.</summary>
    void SetStart(double timeMs);

    /// <summary>
    ///     Punch-out: closes the region at <paramref name="timeMs" /> and turns
    ///     the loop ON. False when there is no armed start or the region is
    ///     degenerate (<see cref="LivePlayLoop.MinLengthMs" />) — the caller
    ///     surfaces that rather than silently doing nothing.
    /// </summary>
    bool SetEnd(double timeMs);

    /// <summary>Flips looping on/off, KEEPING the punched region. False when
    /// there is no region to toggle.</summary>
    bool Toggle();

    /// <summary>Drops the region, the pending start and the enabled flag.</summary>
    void Clear();

    /// <summary>
    ///     Plays <paramref name="startMs" />..<paramref name="endMs" /> ONCE —
    ///     the per-selection audition. It is the loop with one pass: the same
    ///     region, the same wrap test, ending in a seek back to the start plus a
    ///     pause. False when the span is degenerate.
    /// </summary>
    bool BeginAudition(double startMs, double endMs);

    /// <summary>
    ///     Auditions whatever the SHARED target ladder resolves — selection →
    ///     hand shape → pen, the same sounds every keyboard verb acts on, so
    ///     "hear this" needs no separate notion of what "this" is.
    ///     <para>
    ///         Returns the transport position the caller must seek to before
    ///         playing (the same contract as <c>LivePlaySessionService.SetLoop</c>),
    ///         or null when nothing resolved or the span is unusable. The
    ///         service deliberately does NOT drive the transport itself.
    ///     </para>
    /// </summary>
    double? BeginAuditionOfTarget();

    /// <summary>Clears the one-shot audition after its pass completed. Called
    /// from the UI thread by the coordinator, because it writes bound state.</summary>
    void EndAudition();

    /// <summary>
    ///     One transport tick. <paramref name="rawPlayheadMs" /> MUST be
    ///     <c>IAudioPlaybackService.RawPlayheadMs</c>, never the
    ///     latency-compensated playhead — see <see cref="EditorLoopDecision" />.
    ///     Returns what the transport must do; the caller applies it, because
    ///     this runs on the audio timer thread.
    /// </summary>
    EditorLoopStep Tick(double rawPlayheadMs, bool isPlaying);
}
