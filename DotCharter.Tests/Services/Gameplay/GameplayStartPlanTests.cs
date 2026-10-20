using DotCharter.Services.Gameplay;

namespace DotCharter.Tests.Services.Gameplay;

/// <summary>
///     Where a gameplay run starts. The bug this exists for: Play Mode hard-seeked to 0,
///     but an authored chart's beat grid starts at LeadingSilenceSeconds (10 s), so the
///     highway — which draws nothing but chart content — sat frozen for ~8 s while the
///     transport ran, and the first beat crossed the strike line at exactly 10 s.
/// </summary>
public sealed class GameplayStartPlanTests
{
    [Fact]
    public void A_chart_with_ten_seconds_of_leading_silence_starts_just_before_the_first_beat()
    {
        // The reported case: beat grid begins at 10 s.
        var start = GameplayStartPlan.ResolveStartMs(10_000);

        Assert.Equal(7_000, start, 3);   // 3 s of lead-in, not 10 s of nothing
    }

    [Fact]
    public void A_psarc_whose_grid_starts_near_zero_still_starts_at_zero()
    {
        // Rocksmith imports already begin at ~0. They must not be seeked to a negative
        // position, and their behaviour must not change at all.
        Assert.Equal(0, GameplayStartPlan.ResolveStartMs(0));
        Assert.Equal(0, GameplayStartPlan.ResolveStartMs(500));
        Assert.Equal(0, GameplayStartPlan.ResolveStartMs(2_999));
    }

    [Fact]
    public void The_lead_in_is_never_skipped_past_the_start_of_the_song() => Assert.Equal(0, GameplayStartPlan.ResolveStartMs(1_000, leadInMs: 5_000));

    [Fact]
    public void An_empty_chart_starts_at_zero() => Assert.Equal(0, GameplayStartPlan.ResolveStartMs(null));

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Nonsense_content_times_degrade_to_zero_rather_than_seeking_somewhere_absurd(double bad) => Assert.Equal(0, GameplayStartPlan.ResolveStartMs(bad));

    [Fact]
    public void A_nonsense_lead_in_does_not_move_the_start_past_the_content()
    {
        // A zero/negative/NaN lead-in must land ON the content, never after it.
        Assert.Equal(10_000, GameplayStartPlan.ResolveStartMs(10_000, leadInMs: 0), 3);
        Assert.Equal(10_000, GameplayStartPlan.ResolveStartMs(10_000, leadInMs: -5), 3);
        Assert.Equal(10_000, GameplayStartPlan.ResolveStartMs(10_000, leadInMs: double.NaN), 3);
    }

    [Fact]
    public void The_lead_in_comfortably_exceeds_the_default_visibility_horizon()
    {
        // At the DEFAULT highway settings (scroll speed 1.3, draw distance 35) the board
        // shows 2275 ms. If the lead-in were shorter than that, the first note would already
        // be partway down the highway when the run began instead of flying in from the far
        // edge. The horizon used to be a fixed 1750 ms; it is a user setting now, so this
        // constant is only the floor — see LeadInForHorizon.
        Assert.True(GameplayStartPlan.DefaultLeadInMs > 2_275,
            "the default lead-in must exceed the default visibility horizon");
    }

    /// <summary>
    ///     The lead-in must track the horizon, because the horizon is now a user setting
    ///     (scroll speed × draw distance) that reaches ~12 s. This is the regression that
    ///     would otherwise be invisible: the run still "works", the first note just materialises
    ///     mid-board instead of approaching.
    /// </summary>
    [Theory]
    [InlineData(1_750, 3_000)]   // the old horizon — floor still wins
    [InlineData(2_275, 3_000)]   // the new default — floor still wins
    [InlineData(3_000, 3_500)]   // horizon overtakes the floor
    [InlineData(12_000, 12_500)] // the slowest, longest board
    public void The_lead_in_tracks_the_visible_horizon(double horizonMs, double expected)
        => Assert.Equal(expected, GameplayStartPlan.LeadInForHorizon(horizonMs), 3);

    [Fact]
    public void The_lead_in_always_covers_the_horizon_so_the_first_note_flies_in()
    {
        foreach (var horizon in new double[] { 500, 1_750, 2_275, 5_250, 8_000, 12_000 })
        {
            Assert.True(GameplayStartPlan.LeadInForHorizon(horizon) >= horizon,
                $"a {horizon} ms horizon needs at least that much lead-in, or the first note starts on-board");
        }
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_nonsense_horizon_falls_back_to_the_default_lead_in(double horizonMs)
        => Assert.Equal(GameplayStartPlan.DefaultLeadInMs, GameplayStartPlan.LeadInForHorizon(horizonMs), 3);

    [Fact]
    public void EarliestOf_picks_the_first_real_time_and_ignores_nonsense()
    {
        Assert.Equal(1_200, GameplayStartPlan.EarliestOf(5_000, 1_200, 3_000));
        Assert.Equal(1_200, GameplayStartPlan.EarliestOf(null, 1_200, double.NaN, double.PositiveInfinity));
        Assert.Null(GameplayStartPlan.EarliestOf(null, double.NaN));
    }
}
