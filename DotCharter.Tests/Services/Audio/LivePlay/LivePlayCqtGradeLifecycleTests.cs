using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Tests.Services.Audio.LivePlay;

/// <summary>
///     #329, the session's half: in CQT mode a hit is credited when the CQT confirms it and its
///     timing grade follows when the analyzer reports the attack onset, ~200 ms after the attack.
///     Whatever ends the run inside that gap — a stop, a loop pass wrapping — the hit is already
///     credited and must stay so; only the pending GRADE resolves (untimed). Holding the credit
///     for the onset lost exactly these hits.
/// </summary>
public sealed class LivePlayCqtGradeLifecycleTests
{
    private const double Bpm = 120;
    private const double MsPerBeat = 60000.0 / Bpm;

    private static List<BeatMarker> Beats() =>
        [.. Enumerable.Range(0, 32).Select(i => new BeatMarker { Time = i * MsPerBeat, Bpm = Bpm })];

    /// <summary>One note at 1000 ms (E2 + 3 on the low E string), over a CQT that confirms everything.</summary>
    private static LivePlaySessionService CqtSession(int countInBeats = 4)
    {
        var arr = new Arrangement { ArrangementType = ArrangementType.Lead };
        arr.Levels.Add(new ChartLevel());
        arr.Levels[0].Notes.Add(new GuitarNote { Position = new FractionalPosition(2), Fret = 3, StringIndex = 0 });

        Func<ExpectedSound, ChartVerifyOutcome> confirm = _ => new ChartVerifyOutcome(true, 0.9f);
        var session = new LivePlaySessionService();
        session.Start(arr, arr.Levels[0], Beats(), new LivePlaySessionOptions
        {
            ChartVerify = confirm,
            ChartVerifyEarly = confirm,
            CountInBeats = countInBeats
        });
        session.SyncTo(0);
        return session;
    }

    private static void TickTo(LivePlaySessionService session, double fromMs, double toMs)
    {
        for (var t = fromMs; t <= toMs; t += 33) session.HandleTick(t, isPlaying: true);
    }

    [Fact]
    public void AStopInsideTheOnsetsReportingDelay_KeepsTheHit_GradedUntimed()
    {
        var session = CqtSession();
        TickTo(session, 0, 1000);
        var credit = Assert.Single(session.HandleTick(1100, isPlaying: true).Events);
        Assert.Equal(LivePlayVerdict.Hit, credit.Verdict);
        Assert.Equal(50, session.Stats!.Score);                          // the floor, the grade still pending

        // The run stops before the onset is reported.
        var result = session.Stop(transportSpeed: 1.0);

        Assert.Equal(1, result.Hits);
        Assert.Equal(0, result.Misses);
        Assert.Equal(100, result.Score);                                  // the untimed full base, never less
        Assert.Equal(0, result.PerfectCount + result.GreatCount + result.GoodCount);
    }

    [Fact]
    public void AnOnsetAfterTheCredit_GradesThroughTheSession_AndTalliesNothingTwice()
    {
        var session = CqtSession();
        TickTo(session, 0, 1000);
        Assert.Single(session.HandleTick(1100, isPlaying: true).Events);
        var sectionHits = session.Sections.Sum(s => s.Hits);
        var also = new List<LivePlayEvent>();
        var graded = new List<LivePlayGrade>();

        var judged = session.Offer([new LivePitch(43, 0f, 1f)], [], 1020, LiveTechniqueFlags.None, 0f, also, graded);

        Assert.Null(judged);
        Assert.Empty(also);
        var grade = Assert.Single(graded);
        Assert.Equal(LiveTimingTier.Perfect, grade.Tier);
        Assert.Equal(LiveTimingDirection.Late, grade.Direction);
        Assert.Equal(sectionHits, session.Sections.Sum(s => s.Hits));     // the hit was tallied at its credit
        Assert.Equal(1, session.Stats!.Hits);
        Assert.Equal(1, session.Stats.PerfectCount);
        Assert.Equal(100, session.Stats.Score);

        var result = session.Stop(transportSpeed: 1.0);
        Assert.Equal(1, result.PerfectCount);
        Assert.Equal(100, result.Score);
    }

    /// <summary>A loop pass that wraps before the onset is reported counts the hit in the
    /// pass — before the fix it was still held, so the pass saw nothing judged.</summary>
    [Fact]
    public void ALoopWrapInsideTheOnsetsReportingDelay_CountsTheHitInThePass()
    {
        var session = CqtSession(countInBeats: 0);
        var seek = session.SetLoop(800, 1150);
        Assert.Equal(800, seek);

        session.HandleTick(800, isPlaying: true);                         // the pass's own seek lands
        TickTo(session, 833, 1000);
        Assert.Single(session.HandleTick(1100, isPlaying: true).Events);  // credited at confirmation
        var wrap = session.HandleTick(1160, isPlaying: true);              // wraps 60 ms later

        Assert.True(wrap.PassCompleted);
        Assert.Equal(1, wrap.PassJudged);
        Assert.Equal(1.0, wrap.PassAccuracy, 6);
    }
}
