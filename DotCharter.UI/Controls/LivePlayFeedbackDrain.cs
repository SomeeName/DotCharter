using System.Collections.Concurrent;
using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;
using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Gameplay.Spectator;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.UI.Controls;

/// <summary>
///     The Live Play half of <see cref="Preview3DRenderDriver" />'s snapshot staging:
/// drains <see cref="LivePlayViewModel.FeedbackEvents" /> into the staging
///     <see cref="FrameSnapshot" /> once per frame, and owns the state that has to
///     outlive the frame the events arrived in — the crowd meter, the sticky
///     EARLY/LATE direction and PERFECT/GREAT/GOOD tier, and the feedback sequence.
///     <para>
///         Render thread only, like the driver fields it replaced. Pure — no Vulkan,
///         no dispatcher, no clock of its own (the caller hands in the frame's time) —
///         so the <see cref="LivePlayFeedbackEvent.IsGrade" /> guard is testable without
///         standing up the render loop. Allocation-free per frame: the one
///         <see cref="CrowdMeter" /> is created on the first Live Play start and reused.
///     </para>
/// </summary>
internal sealed class LivePlayFeedbackDrain
{
    // Monotonic id stamped onto drained Live Play feedback events so the
    // render component can ignore events it already ingested when the same
    // snapshot is rendered twice (staging update failure keeps the old one).
    private long _sequence;

    // Crowd-driven background (0.14.2 C): the pure CrowdMeter is fed from
    // the SAME drained verdict stream the render feedback uses, so the
    // background energy and the on-highway feedback always agree. Reset on
    // every session start.
    private CrowdMeter? _crowdMeter;
    private bool _lastLivePlayActive;

    /// <summary>Newest judged hit that carried an early/late direction, and when.
    /// Held here on purpose: the staging/render snapshots ping-pong, so the HUD's
    /// decaying label cannot live in the snapshot itself — it is re-stamped into
    /// it every frame.</summary>
    private byte _lastTimingDirection;
    private double _lastTimingAtMs = double.NegativeInfinity;

    /// <summary>Newest judged hit that carried a timing TIER (Perfect/Great/Good),
    /// and when — the source of the PERFECT/GREAT/GOOD flash. Held here for the
    /// same ping-pong reason as the direction pair above: the flash must outlive
    /// the frames after its event was drained, so it cannot live in the ping-ponged
    /// snapshot — it is re-stamped there every frame.</summary>
    private byte _lastTimingTier;
    private double _lastTimingTierAtMs = double.NegativeInfinity;

    /// <summary>
    ///     One frame's drain. Writes <see cref="FrameSnapshot.LivePlayFeedback" />,
    ///     <see cref="FrameSnapshot.CrowdEnergy" /> and the four
    ///     <c>LastTiming*</c> fields of <paramref name="snapshot" />; leaves every
    ///     other field alone.
    /// </summary>
    /// <param name="events">The session's feedback queue, or null without a Live
    /// Play view model. Drained even while inactive, so stale events from a stopped
    /// session never leak into the next one.</param>
    /// <param name="livePlayActive">Whether a Live Play session is running this
    /// frame; its false-to-true edge resets the crowd meter and the sticky cues.</param>
    /// <param name="streak">The player's streak, fed to the crowd meter with each
    /// verdict.</param>
    /// <param name="nowMs">The frame's song time.</param>
    /// <param name="snapshot">The staging snapshot.</param>
    public void Drain(
        ConcurrentQueue<LivePlayFeedbackEvent>? events, bool livePlayActive, int streak, double nowMs,
        FrameSnapshot snapshot)
    {
        if (livePlayActive && !_lastLivePlayActive)
        {
            (_crowdMeter ??= new CrowdMeter()).Reset(nowMs);
            _lastTimingDirection = 0;
            _lastTimingAtMs = double.NegativeInfinity;
            _lastTimingTier = 0;
            _lastTimingTierAtMs = double.NegativeInfinity;
        }

        _lastLivePlayActive = livePlayActive;
        snapshot.ClearLivePlayFeedback();
        if (events != null)
        {
            while (events.TryDequeue(out var fb))
            {
                // A late timing GRADE (CQT mode) is a hit already counted and
                // marked when it was credited: it feeds only the cues below.
                // A technique DOWNGRADE is a hit already counted at full: the crowd
                // gives back its extra over a half hit, at the streak it was counted at.
                if (livePlayActive && fb.IsTechniqueDowngrade)
                    _crowdMeter?.OnTechniqueDowngrade(fb.StreakAtCredit, fb.JudgedAtMs);
                else if (livePlayActive && !fb.IsGrade)
                    _crowdMeter?.OnVerdict(fb.Verdict, streak, fb.JudgedAtMs);

                // Remember the newest hit that actually HAS a direction, so the
                // HUD flash survives the frames after the event was drained.
                // Direction is None for misses and CQT pull-verified hits (no
                // honest attack time) — those must not clear a live label either,
                // or a miss right after a rushed hit would blank the feedback.
                if (fb.Direction != LiveTimingDirection.None)
                {
                    _lastTimingDirection = (byte)fb.Direction;
                    _lastTimingAtMs = fb.JudgedAtMs;
                }

                // The tier flash follows the identical rule: keep the newest hit
                // that carried an honest tier so PERFECT/GREAT/GOOD survives the
                // frames after the event is drained. Tier None (miss, CQT
                // pull-verified) leaves the last flash alone rather than clearing
                // it — see TimingFlashMath.NextSticky.
                (_lastTimingTier, _lastTimingTierAtMs) = TimingFlashMath.NextSticky(
                    _lastTimingTier, _lastTimingTierAtMs, (byte)fb.Tier, fb.JudgedAtMs);

                if (fb.IsGrade) continue;
                snapshot.AddLivePlayFeedback(new RenderLivePlayFeedback
                {
                    Seq = ++_sequence,
                    NoteTimeMs = fb.NoteTimeMs,
                    JudgedAtMs = fb.JudgedAtMs,
                    StringIndex = (short)fb.StringIndex,
                    Fret = (short)fb.Fret,
                    IsChord = fb.IsChord,
                    Verdict = (byte)fb.Verdict,
                    Direction = (byte)fb.Direction
                });
            }
        }

        snapshot.CrowdEnergy = livePlayActive && _crowdMeter != null
            ? (float)_crowdMeter.Value(nowMs)
            : CrowdBackgroundPalette.NeutralEnergy;

        // Written EVERY frame from drain-owned state — the two snapshots
        // ping-pong, so anything left unwritten is stale on the frame it is next
        // read (the label would strobe at half rate).
        snapshot.LastTimingDirection = livePlayActive ? _lastTimingDirection : (byte)0;
        snapshot.LastTimingAtMs = livePlayActive ? _lastTimingAtMs : double.NegativeInfinity;
        snapshot.LastTimingTier = livePlayActive ? _lastTimingTier : (byte)0;
        snapshot.LastTimingTierAtMs = livePlayActive ? _lastTimingTierAtMs : double.NegativeInfinity;
    }
}
