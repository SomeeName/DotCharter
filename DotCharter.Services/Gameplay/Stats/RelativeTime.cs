namespace DotCharter.Services.Gameplay.Stats;

/// <summary>Coarse "how long ago" bucket for a stored timestamp (the song
/// library's "last played", the profile switcher's "last used").
/// The caller computes it from an INJECTED now (never
/// <see cref="DateTime.UtcNow"/> directly) so the mapping is deterministic
/// and unit-testable; the UI localizes the bucket into text.</summary>
public enum RelativeTimeUnit
{
    JustNow,
    Minutes,
    Hours,
    Days,
    Weeks,
    Months,
    Years
}

/// <summary>A time distance expressed as a unit + count (e.g. 3 Days).
/// <see cref="RelativeTimeUnit.JustNow"/> carries <see cref="Value"/> 0.</summary>
public readonly record struct RelativeTime(RelativeTimeUnit Unit, int Value)
{
    /// <summary>Buckets <paramref name="pastUtc"/> against <paramref name="nowUtc"/>.
    /// A future timestamp (clock skew) clamps to <see cref="RelativeTimeUnit.JustNow"/>
    /// — a stored run is never "in the future".</summary>
    public static RelativeTime Between(DateTime pastUtc, DateTime nowUtc)
    {
        var seconds = (nowUtc - pastUtc).TotalSeconds;
        if (seconds < 60) return new RelativeTime(RelativeTimeUnit.JustNow, 0);

        var minutes = seconds / 60.0;
        if (minutes < 60) return new RelativeTime(RelativeTimeUnit.Minutes, (int)minutes);

        var hours = minutes / 60.0;
        if (hours < 24) return new RelativeTime(RelativeTimeUnit.Hours, (int)hours);

        var days = hours / 24.0;
        if (days < 7) return new RelativeTime(RelativeTimeUnit.Days, (int)days);
        if (days < 30) return new RelativeTime(RelativeTimeUnit.Weeks, Math.Max(1, (int)(days / 7)));
        if (days < 365) return new RelativeTime(RelativeTimeUnit.Months, Math.Max(1, (int)(days / 30)));
        return new RelativeTime(RelativeTimeUnit.Years, Math.Max(1, (int)(days / 365)));
    }
}
