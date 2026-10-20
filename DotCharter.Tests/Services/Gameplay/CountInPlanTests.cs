using DotCharter.Services.Gameplay;

namespace DotCharter.Tests.Services.Gameplay;

/// <summary>
/// Pure schedule for the pre-gameplay metronome count-in (0.14.4 C): normalises
/// the off/4/8 setting, spaces clicks at the start tempo scaled by practice
/// speed, and accents measure downbeats. Injected-time / no-audio, mirroring the
/// pure CountBeatsCrossed style so the driver just awaits the plan.
/// </summary>
public class CountInPlanTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(3, 0)]
    [InlineData(4, 4)]
    [InlineData(7, 4)]
    [InlineData(8, 8)]
    [InlineData(16, 8)]
    public void Sanitize_NormalisesToOffFourOrEight(int input, int expected)
        => Assert.Equal(expected, CountInPlan.Sanitize(input));

    [Fact]
    public void MsPerClick_AtFullSpeed_IsOneBeatOfTheTempo()
    {
        // 120 bpm → 500 ms/beat at 1.0×.
        Assert.Equal(500.0, CountInPlan.MsPerClick(120, 1.0), 3);
        // 60 bpm → 1000 ms/beat.
        Assert.Equal(1000.0, CountInPlan.MsPerClick(60, 1.0), 3);
    }

    [Fact]
    public void MsPerClick_ScalesWithPracticeSpeed() =>
        // A 60% practice run leads in at 60% tempo: beats are longer.
        Assert.Equal(500.0 / 0.6, CountInPlan.MsPerClick(120, 0.6), 3);

    [Theory]
    [InlineData(120, 0)]      // absurd tempo → 120 fallback
    [InlineData(0, 1.0)]      // zero tempo → 120 fallback
    [InlineData(-40, 1.0)]    // negative tempo → 120 fallback
    public void MsPerClick_DefendsAgainstNonsenseInputs(double bpm, double speed)
    {
        var ms = CountInPlan.MsPerClick(bpm, speed);
        Assert.True(ms is > 0 and < 10_000, $"unexpected ms {ms}");
    }

    [Fact]
    public void IsAccent_MarksMeasureDownbeats()
    {
        // 4/4: beats 0 and 4 are downbeats; 1..3, 5..7 are not.
        Assert.True(CountInPlan.IsAccent(0, 4));
        Assert.False(CountInPlan.IsAccent(1, 4));
        Assert.False(CountInPlan.IsAccent(3, 4));
        Assert.True(CountInPlan.IsAccent(4, 4));
        Assert.False(CountInPlan.IsAccent(5, 4));
    }

    [Fact]
    public void Build_Off_ReturnsEmpty()
        => Assert.Empty(CountInPlan.Build(0, 4, 120, 1.0));

    [Fact]
    public void Build_FourBeats_FirstClickHasNoDelay_RestSpacedOneBeat()
    {
        var plan = CountInPlan.Build(4, 4, 120, 1.0);

        Assert.Equal(4, plan.Length);
        Assert.Equal(0.0, plan[0].DelayBeforeMs, 3); // first plays immediately
        for (var i = 1; i < plan.Length; i++)
            Assert.Equal(500.0, plan[i].DelayBeforeMs, 3);
    }

    [Fact]
    public void Build_EightBeats_AccentsFirstBeatOfEachMeasure()
    {
        var plan = CountInPlan.Build(8, 4, 120, 1.0);

        Assert.Equal(8, plan.Length);
        Assert.True(plan[0].Accent);   // bar 1 downbeat
        Assert.True(plan[4].Accent);   // bar 2 downbeat
        for (var i = 0; i < plan.Length; i++)
        {
            if (i is not (0 or 4))
                Assert.False(plan[i].Accent, $"click {i} should not be accented");
        }
    }

    [Fact]
    public void Build_NormalisesOddBeatCount_AndScalesSpacingBySpeed()
    {
        // 5 → sanitised to 4; 0.5× speed doubles the spacing.
        var plan = CountInPlan.Build(5, 4, 120, 0.5);
        Assert.Equal(4, plan.Length);
        Assert.Equal(1000.0, plan[1].DelayBeforeMs, 3);
    }

    // ---- ResolveGridTempo (the "~21 s session-to-transport gap", v0.24) -----
    //
    // Real chart: Ice Nine Kills – Funeral Derangements (CDLC). The SNG's beat 0
    // is parked at t=0 as a one-beat "measure" spanning the intro silence, so its
    // per-beat Bpm — derived from the 5240 ms gap to beat 1 — is 11.45. The old
    // grid[0].Bpm pick spaced the 4-beat count-in at 5240 ms/click: 20.96 s of
    // dead air between session start and transport start. The real grid follows
    // at 157.9 bpm. (Library scan 2026-07-17: 46/1824 CDLCs over 5 s, worst
    // Animals as Leaders – Monomyth at 65.2 s from beat0 Bpm 3.68.)

    private static List<BeatMarker> FuneralDerangementsGrid() =>
    [
        new BeatMarker { Time = 0, IsMeasure = true, Bpm = 11.450, BeatsInMeasure = 1 },
        new BeatMarker { Time = 5240, IsMeasure = true, Bpm = 157.895, BeatsInMeasure = 4 },
        new BeatMarker { Time = 5620, Bpm = 158.311, BeatsInMeasure = 4 },
        new BeatMarker { Time = 5999, Bpm = 157.895, BeatsInMeasure = 4 },
        new BeatMarker { Time = 6379, Bpm = 157.895, BeatsInMeasure = 4 }
    ];

    [Fact]
    public void ResolveGridTempo_SkipsTheLeadingSilenceBeatOfACdlcImport()
    {
        var (bpm, beatsPerMeasure) = CountInPlan.ResolveGridTempo(
            FuneralDerangementsGrid(), startMs: 0, fallbackBpm: 120);

        Assert.Equal(157.895, bpm, 3);
        Assert.Equal(4, beatsPerMeasure);

        // The whole point: the 4-beat count-in (3 inter-click gaps + the final
        // lead-in gap) collapses from ~21 s of silence to under 2 s.
        var total = 4 * CountInPlan.MsPerClick(bpm, 1.0);
        Assert.InRange(total, 1000, 2000);
    }

    [Fact]
    public void ResolveGridTempo_UsesTheFirstBeatWhenItIsSane()
    {
        var grid = new List<BeatMarker>
        {
            new() { Time = 0, IsMeasure = true, Bpm = 120, BeatsInMeasure = 4 },
            new() { Time = 500, Bpm = 120, BeatsInMeasure = 4 }
        };

        var (bpm, beatsPerMeasure) = CountInPlan.ResolveGridTempo(grid, 0, 90);
        Assert.Equal(120, bpm, 3);
        Assert.Equal(4, beatsPerMeasure);
    }

    [Fact]
    public void ResolveGridTempo_ReadsTheTempoAtTheRunStart_NotTheChartStart()
    {
        // Tempo change mid-song: a run starting inside the second region must
        // count in at that region's tempo.
        var grid = new List<BeatMarker>
        {
            new() { Time = 0, IsMeasure = true, Bpm = 120, BeatsInMeasure = 4 },
            new() { Time = 500, Bpm = 120, BeatsInMeasure = 4 },
            new() { Time = 1000, IsMeasure = true, Bpm = 60, BeatsInMeasure = 3 },
            new() { Time = 2000, Bpm = 60, BeatsInMeasure = 3 }
        };

        var (bpm, beatsPerMeasure) = CountInPlan.ResolveGridTempo(grid, 1500, 90);
        Assert.Equal(60, bpm, 3);
        Assert.Equal(3, beatsPerMeasure);
    }

    [Fact]
    public void ResolveGridTempo_RunStartAheadOfTheGrid_UsesTheFirstBeat()
    {
        // Authored chart: grid starts at 10 s, run starts 3 s earlier.
        var grid = new List<BeatMarker>
        {
            new() { Time = 10_000, IsMeasure = true, Bpm = 96, BeatsInMeasure = 4 },
            new() { Time = 10_625, Bpm = 96, BeatsInMeasure = 4 }
        };

        var (bpm, _) = CountInPlan.ResolveGridTempo(grid, 7000, 90);
        Assert.Equal(96, bpm, 3);
    }

    [Fact]
    public void ResolveGridTempo_FallsBackWhenTheGridOffersNothingSane()
    {
        Assert.Equal((90.0, 4), CountInPlan.ResolveGridTempo(null, 0, 90));
        Assert.Equal((90.0, 4), CountInPlan.ResolveGridTempo([], 0, 90));

        // A grid of nothing but silence-encoding tempos (degenerate one-beat chart).
        var insane = new List<BeatMarker>
        {
            new() { Time = 0, IsMeasure = true, Bpm = 3.68, BeatsInMeasure = 1 }
        };
        Assert.Equal((90.0, 4), CountInPlan.ResolveGridTempo(insane, 0, 90));
    }
}
