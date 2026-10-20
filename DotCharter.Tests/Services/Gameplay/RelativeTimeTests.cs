using DotCharter.Services.Gameplay.Stats;

namespace DotCharter.Tests.Services.Gameplay;

/// <summary>
///     <see cref="RelativeTime" /> bucketing against an injected now — the "4
///     days ago" behind the song library's last-played line and the profile
///     switcher. It outlived the session-history read-model it was written
///     for (removed 2026-09-19).
/// </summary>
public sealed class RelativeTimeTests
{
    private static readonly DateTime Now = new(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(0, RelativeTimeUnit.JustNow, 0)]
    [InlineData(30.0 / 86400, RelativeTimeUnit.JustNow, 0)] // 30 s
    [InlineData(90.0 / 86400, RelativeTimeUnit.Minutes, 1)] // 90 s
    [InlineData(2.0 / 24, RelativeTimeUnit.Hours, 2)] // 2 h
    [InlineData(3, RelativeTimeUnit.Days, 3)]
    [InlineData(10, RelativeTimeUnit.Weeks, 1)]
    [InlineData(60, RelativeTimeUnit.Months, 2)]
    [InlineData(400, RelativeTimeUnit.Years, 1)]
    public void RelativeTime_BucketsByAge(double ageDays, RelativeTimeUnit unit, int value)
    {
        var rt = RelativeTime.Between(Now.AddDays(-ageDays), Now);
        Assert.Equal(unit, rt.Unit);
        Assert.Equal(value, rt.Value);
    }

    [Fact]
    public void RelativeTime_FutureTimestamp_ClampsToJustNow()
    {
        var rt = RelativeTime.Between(Now.AddDays(3), Now); // clock skew
        Assert.Equal(RelativeTimeUnit.JustNow, rt.Unit);
    }
}
