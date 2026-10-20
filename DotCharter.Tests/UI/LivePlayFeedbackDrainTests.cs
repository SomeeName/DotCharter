using System.Collections.Concurrent;
using DotCharter.Rendering.Data;
using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Gameplay.Spectator;
using DotCharter.UI.Controls;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI;

/// <summary>
///     <see cref="LivePlayFeedbackDrain" /> is the Live Play half of
///     <c>Preview3DRenderDriver</c>'s snapshot staging (#656). Its
///     <see cref="LivePlayFeedbackEvent.IsGrade" /> guard had no test while it lived
///     in the driver (PR #540: "needs the Vulkan render loop"): a late timing GRADE is
///     a hit already counted and marked when it was credited, so it may move the
///     EARLY/LATE and tier cues but must never reach the crowd meter a second time
///     or put a second marker on the highway.
/// </summary>
public sealed class LivePlayFeedbackDrainTests
{
    private const double StartMs = 1_000;
    private const int Streak = 12;

    private readonly LivePlayFeedbackDrain _drain = new();
    private readonly ConcurrentQueue<LivePlayFeedbackEvent> _queue = new();
    private readonly FrameSnapshot _snapshot = new();

    [Fact]
    public void AGradeEvent_NeverReachesTheCrowdMeterOrTheMarkers_ButMovesTheStickyCues()
    {
        StartSession();
        _queue.Enqueue(Event(1_100, LivePlayVerdict.Hit, LiveTimingDirection.Late, LiveTimingTier.Perfect,
            isGrade: true));

        Frame(1_200);

        Assert.Equal(0, _snapshot.LivePlayFeedbackCount);
        Assert.Equal(ReferenceEnergy(1_200), _snapshot.CrowdEnergy);
        Assert.Equal((byte)LiveTimingDirection.Late, _snapshot.LastTimingDirection);
        Assert.Equal(1_100, _snapshot.LastTimingAtMs);
        Assert.Equal((byte)LiveTimingTier.Perfect, _snapshot.LastTimingTier);
        Assert.Equal(1_100, _snapshot.LastTimingTierAtMs);
    }

    [Fact]
    public void AJudgment_FeedsTheCrowdMeterWithTheStreak_AndAddsOneMarker()
    {
        StartSession();
        _queue.Enqueue(Event(1_100, LivePlayVerdict.Hit, LiveTimingDirection.Early, LiveTimingTier.Great,
            stringIndex: 3, fret: 7, noteTimeMs: 1_090, isChord: true));

        Frame(1_200);

        Assert.Equal(ReferenceEnergy(1_200, (LivePlayVerdict.Hit, 1_100)), _snapshot.CrowdEnergy);
        Assert.True(_snapshot.CrowdEnergy > CrowdBackgroundPalette.NeutralEnergy, "a hit must lift the crowd");

        Assert.Equal(1, _snapshot.LivePlayFeedbackCount);
        var marker = _snapshot.LivePlayFeedback[0];
        Assert.Equal(1, marker.Seq);
        Assert.Equal(1_090, marker.NoteTimeMs);
        Assert.Equal(1_100, marker.JudgedAtMs);
        Assert.Equal(3, marker.StringIndex);
        Assert.Equal(7, marker.Fret);
        Assert.True(marker.IsChord);
        Assert.Equal((byte)LivePlayVerdict.Hit, marker.Verdict);
        Assert.Equal((byte)LiveTimingDirection.Early, marker.Direction);
    }

    /// <summary>The CQT path's real shape: the credit (a judgment with no honest attack
    /// time) and, later, its grade. One verdict for the crowd, one marker.</summary>
    [Fact]
    public void ACreditAndItsGrade_CountOnceForTheCrowd_AndMarkOnce()
    {
        StartSession();
        _queue.Enqueue(Event(1_100, LivePlayVerdict.Hit, LiveTimingDirection.None, LiveTimingTier.None));
        _queue.Enqueue(Event(1_080, LivePlayVerdict.Hit, LiveTimingDirection.OnTime, LiveTimingTier.Perfect,
            isGrade: true));

        Frame(1_200);

        Assert.Equal(1, _snapshot.LivePlayFeedbackCount);
        Assert.Equal(ReferenceEnergy(1_200, (LivePlayVerdict.Hit, 1_100)), _snapshot.CrowdEnergy);
        Assert.Equal((byte)LiveTimingTier.Perfect, _snapshot.LastTimingTier);
    }

    /// <summary>#730: a technique downgrade after the credit takes the hit's extra over a half
    /// hit back off the crowd (at the streak it was credited at, not the frame's) and draws a
    /// wrong-technique marker at the ruling — so the crowd reads as if the hit had been a
    /// wrong-technique hit all along.</summary>
    [Fact]
    public void ADowngrade_TakesTheHitsExtraBackOffTheCrowd_AndMarksTheWrongTechnique()
    {
        StartSession();
        _queue.Enqueue(Event(1_100, LivePlayVerdict.Hit, LiveTimingDirection.None, LiveTimingTier.None,
            stringIndex: 2, fret: 9));
        _queue.Enqueue(new LivePlayFeedbackEvent(1_100, 1_100, 2, 9, false, LivePlayVerdict.HitWrongTechnique,
            IsTechniqueDowngrade: true, StreakAtCredit: Streak));

        Frame(1_200);

        Assert.Equal(ReferenceEnergy(1_200, (LivePlayVerdict.HitWrongTechnique, 1_100)), _snapshot.CrowdEnergy, 5);
        Assert.Equal(2, _snapshot.LivePlayFeedbackCount);
        var marker = _snapshot.LivePlayFeedback[1];
        Assert.Equal((byte)LivePlayVerdict.HitWrongTechnique, marker.Verdict);
        Assert.Equal(2, marker.StringIndex);
        Assert.Equal(9, marker.Fret);
        Assert.Equal(1_100, marker.JudgedAtMs);
    }

    /// <summary>The sticky tier follows <see cref="TimingFlashMath.NextSticky" /> event by
    /// event, and the direction keeps the same rule: None (a miss, a CQT pull-verified
    /// hit) never clears a live cue.</summary>
    [Fact]
    public void TheStickyCues_FollowNextSticky_AndANoneNeverClearsThem()
    {
        StartSession();
        (LiveTimingDirection Dir, LiveTimingTier Tier, double At, bool Grade)[] stream =
        [
            (LiveTimingDirection.Early, LiveTimingTier.Good, 1_100, false),
            (LiveTimingDirection.None, LiveTimingTier.None, 1_150, false),   // a miss
            (LiveTimingDirection.Late, LiveTimingTier.Great, 1_200, true),   // a grade still moves the cues
            (LiveTimingDirection.None, LiveTimingTier.None, 1_250, false),
            (LiveTimingDirection.OnTime, LiveTimingTier.Perfect, 1_300, false),
        ];

        byte tier = 0, direction = 0;
        double tierAt = double.NegativeInfinity, directionAt = double.NegativeInfinity;
        foreach (var (dir, t, at, grade) in stream)
        {
            var verdict = dir == LiveTimingDirection.None ? LivePlayVerdict.Miss : LivePlayVerdict.Hit;
            _queue.Enqueue(Event(at, verdict, dir, t, isGrade: grade));
            Frame(at + 10);

            (tier, tierAt) = TimingFlashMath.NextSticky(tier, tierAt, (byte)t, at);
            if (dir != LiveTimingDirection.None) (direction, directionAt) = ((byte)dir, at);

            Assert.Equal(tier, _snapshot.LastTimingTier);
            Assert.Equal(tierAt, _snapshot.LastTimingTierAtMs);
            Assert.Equal(direction, _snapshot.LastTimingDirection);
            Assert.Equal(directionAt, _snapshot.LastTimingAtMs);
        }

        Assert.Equal((byte)LiveTimingTier.Perfect, _snapshot.LastTimingTier);
        Assert.Equal((byte)LiveTimingDirection.OnTime, _snapshot.LastTimingDirection);
    }

    /// <summary>The cues are drain-owned and re-stamped EVERY frame (the snapshots
    /// ping-pong), so a frame with nothing new still carries them.</summary>
    [Fact]
    public void AFrameWithNoEvents_StillCarriesTheStickyCues()
    {
        StartSession();
        _queue.Enqueue(Event(1_100, LivePlayVerdict.Hit, LiveTimingDirection.Late, LiveTimingTier.Good));
        Frame(1_110);

        var other = new FrameSnapshot();
        _drain.Drain(_queue, livePlayActive: true, Streak, 1_120, other);

        Assert.Equal(0, other.LivePlayFeedbackCount);
        Assert.Equal((byte)LiveTimingDirection.Late, other.LastTimingDirection);
        Assert.Equal((byte)LiveTimingTier.Good, other.LastTimingTier);
        Assert.Equal(1_100, other.LastTimingTierAtMs);
    }

    [Fact]
    public void StartingLivePlay_ResetsTheCrowdAndTheCues_ButNotTheSequence()
    {
        StartSession();
        _queue.Enqueue(Event(1_100, LivePlayVerdict.Miss, LiveTimingDirection.None, LiveTimingTier.None));
        _queue.Enqueue(Event(1_150, LivePlayVerdict.Miss, LiveTimingDirection.None, LiveTimingTier.None));
        _queue.Enqueue(Event(1_200, LivePlayVerdict.Hit, LiveTimingDirection.Late, LiveTimingTier.Perfect));
        Frame(1_250);
        Assert.Equal(2, _snapshot.LivePlayFeedback[1].Seq);
        Assert.NotEqual(ReferenceEnergy(1_250), _snapshot.CrowdEnergy);

        // Stopped: the snapshot shows a neutral crowd and no cue.
        _drain.Drain(_queue, livePlayActive: false, Streak, 1_300, _snapshot);
        Assert.Equal(CrowdBackgroundPalette.NeutralEnergy, _snapshot.CrowdEnergy);
        Assert.Equal(0, _snapshot.LastTimingDirection);
        Assert.Equal(double.NegativeInfinity, _snapshot.LastTimingAtMs);
        Assert.Equal(0, _snapshot.LastTimingTier);
        Assert.Equal(double.NegativeInfinity, _snapshot.LastTimingTierAtMs);

        // Started again at 5 000: a fresh room and no leftover cue from the last run.
        _drain.Drain(_queue, livePlayActive: true, Streak, 5_000, _snapshot);
        Assert.Equal(ReferenceEnergy(5_000, startMs: 5_000), _snapshot.CrowdEnergy);
        Assert.Equal(0, _snapshot.LastTimingDirection);
        Assert.Equal(double.NegativeInfinity, _snapshot.LastTimingAtMs);
        Assert.Equal(0, _snapshot.LastTimingTier);
        Assert.Equal(double.NegativeInfinity, _snapshot.LastTimingTierAtMs);

        // The sequence is monotonic for the life of the drain: the render component
        // dedups on it, so a restart must never hand out an id it has seen.
        _queue.Enqueue(Event(5_100, LivePlayVerdict.Hit, LiveTimingDirection.Early, LiveTimingTier.Good));
        Frame(5_110);
        Assert.Equal(4, _snapshot.LivePlayFeedback[0].Seq);
    }

    /// <summary>Staying active is not a start: the cues and the crowd carry on.</summary>
    [Fact]
    public void StayingActive_DoesNotReset()
    {
        StartSession();
        _queue.Enqueue(Event(1_100, LivePlayVerdict.Hit, LiveTimingDirection.Late, LiveTimingTier.Great));
        Frame(1_110);
        Frame(1_120);

        Assert.Equal((byte)LiveTimingTier.Great, _snapshot.LastTimingTier);
        Assert.Equal(ReferenceEnergy(1_120, (LivePlayVerdict.Hit, 1_100)), _snapshot.CrowdEnergy);
    }

    [Fact]
    public void Markers_ComeOutInQueueOrder_WithConsecutiveIds_AcrossFrames()
    {
        StartSession();
        for (var i = 0; i < 5; i++)
        {
            _queue.Enqueue(Event(1_100 + i, LivePlayVerdict.Hit, LiveTimingDirection.OnTime, LiveTimingTier.Good,
                stringIndex: i, fret: 10 + i, isGrade: i == 2));
        }

        Frame(1_200);

        Assert.Equal(4, _snapshot.LivePlayFeedbackCount);
        int[] expectedStrings = [0, 1, 3, 4];
        for (var k = 0; k < expectedStrings.Length; k++)
        {
            var marker = _snapshot.LivePlayFeedback[k];
            Assert.Equal(k + 1, marker.Seq);
            Assert.Equal(expectedStrings[k], marker.StringIndex);
            Assert.Equal(10 + expectedStrings[k], marker.Fret);
        }

        _queue.Enqueue(Event(1_300, LivePlayVerdict.Miss, LiveTimingDirection.None, LiveTimingTier.None));
        Frame(1_310);
        Assert.Equal(1, _snapshot.LivePlayFeedbackCount);    // each frame carries only its own events
        Assert.Equal(5, _snapshot.LivePlayFeedback[0].Seq);
    }

    /// <summary>Inactive frames still drain, so a stopped session's events never leak
    /// into the next one — and they feed no crowd.</summary>
    [Fact]
    public void AnInactiveFrame_DrainsTheQueue_AndFeedsNoCrowd()
    {
        _queue.Enqueue(Event(1_100, LivePlayVerdict.Hit, LiveTimingDirection.Late, LiveTimingTier.Great));
        _drain.Drain(_queue, livePlayActive: false, Streak, 1_200, _snapshot);

        Assert.True(_queue.IsEmpty);
        Assert.Equal(CrowdBackgroundPalette.NeutralEnergy, _snapshot.CrowdEnergy);
        Assert.Equal(0, _snapshot.LastTimingTier);

        StartSession();
        Assert.Equal(0, _snapshot.LivePlayFeedbackCount);
        Assert.Equal(ReferenceEnergy(StartMs), _snapshot.CrowdEnergy);
    }

    [Fact]
    public void NoQueue_ClearsLastFramesMarkers()
    {
        StartSession();
        _queue.Enqueue(Event(1_100, LivePlayVerdict.Hit, LiveTimingDirection.Late, LiveTimingTier.Great));
        Frame(1_110);
        Assert.Equal(1, _snapshot.LivePlayFeedbackCount);

        _drain.Drain(null, livePlayActive: true, Streak, 1_120, _snapshot);

        Assert.Equal(0, _snapshot.LivePlayFeedbackCount);
        Assert.Equal((byte)LiveTimingTier.Great, _snapshot.LastTimingTier);
    }

    /// <summary>Runs on the render thread at the fps cap: a steady-state frame with
    /// events allocates nothing (the crowd meter is created once, on the first start;
    /// the snapshot's marker array has grown during the warm-up).</summary>
    [Fact]
    public void ASteadyStateFrame_IsAllocationFree()
    {
        StartSession();
        var now = 2_000.0;
        AllocationProbe.AssertAllocationFree("LivePlayFeedbackDrain.Drain", () =>
        {
            for (var frame = 0; frame < 16; frame++)
            {
                now += 16;
                _queue.Enqueue(Event(now - 4, LivePlayVerdict.Hit, LiveTimingDirection.Early, LiveTimingTier.Great));
                _queue.Enqueue(Event(now - 2, LivePlayVerdict.Hit, LiveTimingDirection.Late, LiveTimingTier.Perfect,
                    isGrade: true));
                _queue.Enqueue(Event(now - 1, LivePlayVerdict.Miss, LiveTimingDirection.None, LiveTimingTier.None));
                _drain.Drain(_queue, livePlayActive: true, Streak, now, _snapshot);
            }
        });

        Assert.Equal(2, _snapshot.LivePlayFeedbackCount);
    }

    private void StartSession() => _drain.Drain(_queue, livePlayActive: true, Streak, StartMs, _snapshot);

    private void Frame(double nowMs) => _drain.Drain(_queue, livePlayActive: true, Streak, nowMs, _snapshot);

    /// <summary>What the crowd reads at <paramref name="nowMs" /> after a start at
    /// <paramref name="startMs" /> and exactly these verdicts, at <see cref="Streak" />.</summary>
    private static float ReferenceEnergy(
        double nowMs, (LivePlayVerdict Verdict, double AtMs)? verdict = null, double startMs = StartMs)
    {
        var meter = new CrowdMeter();
        meter.Reset(startMs);
        if (verdict is { } v) meter.OnVerdict(v.Verdict, Streak, v.AtMs);
        return (float)meter.Value(nowMs);
    }

    private static LivePlayFeedbackEvent Event(
        double judgedAtMs, LivePlayVerdict verdict, LiveTimingDirection direction, LiveTimingTier tier,
        int stringIndex = 0, int fret = 5, double? noteTimeMs = null, bool isChord = false, bool isGrade = false) =>
        new(noteTimeMs ?? judgedAtMs, judgedAtMs, stringIndex, fret, isChord, verdict, direction, tier, isGrade);
}
