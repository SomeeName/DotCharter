using DotCharter.Tests.Services.Audio.LivePlay;

namespace DotCharter.Tests.UI.ViewModels.PlayMode;

/// <summary>Main judges captured state; only the later completion action may be cancelled.</summary>
public sealed class CapturedTickReviewTests
{
    [Fact]
    public async Task MainWrapsOnTheFollowingTick_WhenSeekArrivesDuringCapture()
    {
        using var run = new CapturedTickTestRun(loop: true);
        run.Tick(730);
        await run.Interleave(1000, "snapshot", "seek", check: false);
        run.Tick(2500, check: false);
        Assert.Equal(1, run.Reference.PassCount); // main's following tick wraps once
        Assert.Equal(0, run.H.Audio.CurrentTimeMs);
        run.Check();
        run.Tick(0);
        Assert.Null(PlayModeReviewHarness.Get<double?>(run.Session, "_pendingSeekTargetMs"));
    }

    [Fact]
    public async Task MainSavesOneMiss_WhenPauseArrivesAfterThePlayingSnapshot()
    {
        using var run = new CapturedTickTestRun(loop: false);
        run.Tick(730);
        await run.PauseDuringCapture(780);
        run.End();
        Assert.Equal((0, 1), run.Reference.Saved); // main expires the 500ms note at captured 780ms
        var saved = Assert.Single(Assert.Single(run.H.Store.GetAll().Values).Sessions);
        Assert.Equal(1, saved.Misses);
        Assert.False(run.H.Audio.IsPlaying);
    }
}

/// <summary>
///     Two gated interleavings per fixed seed, with captured-snapshot executable-main judging. Rule the
///     model adds to main: a loop note whose late window is still open at the wrap is one miss of the
///     pass that ends (#629).
/// </summary>
public sealed class CapturedTickInterleavingReviewTests
{
    public static TheoryData<int> Seeds => ScoringSeeds();

    private static TheoryData<int> ScoringSeeds()
    {
        var seeds = new TheoryData<int>();
        for (var i = 0; i < 200; i++) seeds.Add(0x5eed + i);
        return seeds;
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task CapturedJudgmentsAndCompletionMatchMainAcrossUserInterleavings(int seed)
    {
        await Exercise(seed, "snapshot", (seed & 1) == 0);
        await Exercise(seed, "action", (seed & 1) != 0);
        var (chart, _) = ScoringClockReviewTests.Chart(minimal: false, seed);
        var route = ResumeCompletionReviewTests.Routes[(seed - 0x5eed) % ResumeCompletionReviewTests.Routes.Length];
        ResumeCompletionReviewTests.VerifyResume(route, loop: false, chart);
        ResumeCompletionReviewTests.VerifyResume(route, loop: true, chart);
    }

    private static async Task Exercise(int seed, string phase, bool loop)
    {
        var random = new Random(seed * 17 + (phase == "action" ? 3 : 0));
        var (chart, attacks) = ScoringClockReviewTests.Chart(minimal: false, seed);
        using var run = new CapturedTickTestRun(loop, noInput: true, chart);
        if (random.Next(2) == 0) run.Hit(700);
        var delivered = new HashSet<int> { 700 };
        void OfferUntil(int horizon)
        {
            foreach (var attack in attacks)
            {
                if (attack.At + 190 <= horizon && delivered.Add(attack.At) && random.Next(3) != 0)
                    run.Offer(attack.At, attack.Pitches);
            }
        }
        run.Tick(730);
        var action = new[] { "pause", "resume", "seek", "stop", "run-end" }[random.Next(5)];
        if (phase == "snapshot")
        {
            await run.Interleave(1000, phase, action);
        }
        else
        {
            OfferUntil(loop ? 1900 : 3700);
            run.Tick(loop ? 1900 : 3700);
            await run.Interleave(loop ? 2000 : 4000, phase, action);
        }
        if (run.H.Live.IsActive)
        {
            run.Resume();
            run.Tick(run.H.Audio.CurrentTimeMs);
            if (loop)
            {
                if (run.H.Audio.CurrentTimeMs == 0) run.Tick(0);
                OfferUntil(1900);
                run.Tick(1900);
                OfferUntil(2000);
                run.Tick(2000);
                run.Tick(0);
                Assert.Null(PlayModeReviewHarness.Get<double?>(run.Session, "_pendingSeekTargetMs"));
            }
            else
            {
                OfferUntil(4000);
                run.Tick(4000);
            }
        }
        run.End();
        run.Check();
        Assert.NotNull(run.Reference.CompletedAt);
    }
}
