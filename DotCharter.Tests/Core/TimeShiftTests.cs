namespace DotCharter.Tests.Core;

public sealed class TimeShiftTests
{
    private static List<BeatMarker> Beats120() =>
        Enumerable.Range(0, 16).Select(i => new BeatMarker { Time = i * 500, Bpm = 120 }).ToList();

    [Fact]
    public void A_grid_step_moves_by_an_exact_fraction_and_keeps_an_off_grid_offset()
    {
        var offGrid = new FractionalPosition(2, new Fraction(1, 3));

        var later = TimeShift.ByGridSteps(offGrid, 1, 4);
        var earlier = TimeShift.ByGridSteps(offGrid, -1, 4);

        Assert.Equal(new FractionalPosition(2, new Fraction(7, 12)), later);
        Assert.Equal(new FractionalPosition(2, new Fraction(1, 12)), earlier);
    }

    [Fact]
    public void A_grid_step_carries_across_a_beat_boundary()
    {
        var result = TimeShift.ByGridSteps(new FractionalPosition(2, new Fraction(3, 4)), 1, 4);

        Assert.Equal(new FractionalPosition(3), result);
    }

    [Theory]
    [InlineData(1, 501)]
    [InlineData(-1, 499)]
    [InlineData(10, 510)]
    [InlineData(-10, 490)]
    public void A_millisecond_shift_lands_within_a_tick_of_the_target(double delta, double expectedMs)
    {
        var beats = Beats120();
        var start = new FractionalPosition(1);

        var moved = TimeShift.ByMilliseconds(start, delta, beats);

        Assert.Equal(expectedMs, moved.GetPositionMs(beats), 0.05);
    }

    [Fact]
    public void A_shift_that_stays_after_the_first_beat_is_planned_whole()
    {
        var starts = new[] { new FractionalPosition(2), new FractionalPosition(3) };

        var plan = TimeShift.Plan(starts, p => TimeShift.ByGridSteps(p, -1, 4));

        Assert.Equal(TimeShiftOutcome.Moved, plan.Outcome);
        Assert.Equal(new FractionalPosition(1, new Fraction(3, 4)), plan.Map(starts[0]));
    }

    [Fact]
    public void A_shift_across_the_first_beat_is_clamped_and_the_group_moves_rigidly()
    {
        var first = new FractionalPosition(0, new Fraction(1, 8));
        var second = new FractionalPosition(1);

        var plan = TimeShift.Plan([first, second], p => TimeShift.ByGridSteps(p, -1, 4));

        Assert.Equal(TimeShiftOutcome.Clamped, plan.Outcome);
        Assert.Equal(FractionalPosition.Zero, plan.Map(first));
        Assert.Equal(new FractionalPosition(0, new Fraction(7, 8)), plan.Map(second));
    }

    [Fact]
    public void An_earlier_shift_with_the_earliest_item_on_the_first_beat_is_refused()
    {
        var plan = TimeShift.Plan([FractionalPosition.Zero, new FractionalPosition(2)],
            p => TimeShift.ByGridSteps(p, -1, 4));

        Assert.Equal(TimeShiftOutcome.AtStart, plan.Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_preroll_group_can_move_forward_while_still_before_the_grid(bool milliseconds)
    {
        var first = new FractionalPosition(-1);
        var second = new FractionalPosition(2);
        var beats = Beats120();
        var plan = TimeShift.Plan([second, first], p => milliseconds
            ? TimeShift.ByMilliseconds(p, 31.25, beats)
            : TimeShift.ByGridSteps(p, 1, 16));

        Assert.Equal(TimeShiftOutcome.Moved, plan.Outcome);
        Assert.Equal(new FractionalPosition(-1, new Fraction(1, 16)), plan.Map(first));
        Assert.Equal(new FractionalPosition(2, new Fraction(1, 16)), plan.Map(second));
    }

    [Fact]
    public void A_preroll_group_cannot_move_further_back()
    {
        var first = new FractionalPosition(-1);
        var plan = TimeShift.Plan([first], p => TimeShift.ByGridSteps(p, -1, 16));

        Assert.Equal(TimeShiftOutcome.AtStart, plan.Outcome);
        Assert.Equal(first, plan.Map(first));
    }

    [Fact]
    public void An_empty_group_has_nothing_to_move() =>
        Assert.Equal(TimeShiftOutcome.Empty, TimeShift.Plan([], p => p).Outcome);
}
