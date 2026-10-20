using DotCharter.Services.Gameplay;

namespace DotCharter.Tests.Services.Gameplay;

/// <summary>
///     The runtime walk over a <see cref="FirstNoteCountInPlan" />: a click is
///     handed out once it is within the lookahead of the RAW playhead, exactly
///     once, in order; a stale one is skipped; a seek re-arms what is still
///     ahead. Driven the way the Live Play worker drives it — rearm on a seek,
///     then take until nothing more is due.
/// </summary>
public sealed class FirstNoteCountInScheduleTests
{
    private static readonly FirstNoteCountInPlan.Click[] FourBeats =
    [
        new(10_000, true),
        new(10_500, false),
        new(11_000, false),
        new(11_500, false),
    ];

    [Fact]
    public void Nothing_is_due_until_it_is_armed()
    {
        var schedule = new FirstNoteCountInSchedule();

        Assert.False(schedule.IsArmed);
        Assert.Empty(Tick(schedule, rawMs: 10_000));
    }

    [Fact]
    public void A_click_is_handed_out_once_it_is_within_the_lookahead()
    {
        var schedule = Armed();

        Assert.Empty(Tick(schedule, rawMs: 10_000 - FirstNoteCountInSchedule.LookaheadMs - 1));
        Assert.Equal([10_000.0], Tick(schedule, rawMs: 10_000 - FirstNoteCountInSchedule.LookaheadMs));
    }

    [Fact]
    public void Every_click_is_handed_out_exactly_once_and_in_order()
    {
        var schedule = Armed();
        var handed = new List<double>();

        for (var rawMs = 9_000.0; rawMs <= 12_000; rawMs += 33)
        {
            handed.AddRange(Tick(schedule, rawMs));
        }

        Assert.Equal([10_000.0, 10_500.0, 11_000.0, 11_500.0], handed);
    }

    [Fact]
    public void Accent_rides_along_with_its_click()
    {
        var schedule = Armed();

        Assert.True(schedule.TryTakeDue(10_000, out var first));
        Assert.True(first.Accent);
        Assert.True(schedule.TryTakeDue(10_000, out var second));
        Assert.False(second.Accent);
    }

    [Fact]
    public void A_click_far_behind_the_playhead_is_skipped_not_fired_stale()
    {
        var schedule = Armed();

        var handed = Tick(schedule, rawMs: 10_000 + FirstNoteCountInSchedule.StaleAfterMs + 1);

        Assert.Equal([10_500.0], handed);
    }

    [Fact]
    public void A_seek_rearms_the_clicks_still_ahead_and_only_those()
    {
        var schedule = Armed();
        Assert.Equal([10_000.0, 10_500.0], Tick(schedule, rawMs: 10_200));

        var afterRewind = Tick(schedule, rawMs: 10_100, sawSeek: true);

        Assert.Equal([10_500.0], afterRewind);
    }

    [Fact]
    public void Disarm_and_an_empty_arm_both_leave_nothing_due()
    {
        var schedule = Armed();
        schedule.Disarm();
        Assert.Empty(Tick(schedule, rawMs: 10_000));

        schedule.Arm([]);
        Assert.False(schedule.IsArmed);
        schedule.Arm(null);
        Assert.False(schedule.IsArmed);
    }

    private static FirstNoteCountInSchedule Armed()
    {
        var schedule = new FirstNoteCountInSchedule();
        schedule.Arm(FourBeats);
        return schedule;
    }

    /// <summary>One worker tick: rearm on a seek, then take everything due.</summary>
    private static List<double> Tick(FirstNoteCountInSchedule schedule, double rawMs, bool sawSeek = false)
    {
        if (sawSeek)
        {
            schedule.RearmAfterSeek(rawMs);
        }

        var handed = new List<double>();
        while (schedule.TryTakeDue(rawMs, out var click))
        {
            handed.Add(click.TimeMs);
        }

        return handed;
    }
}
