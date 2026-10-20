using System.Globalization;

namespace DotCharter.Services.Ai.Match;

/// <summary>
///     A time inside a song as the tone-matching text reads it: m:ss, never
///     h:mm:ss, because every section is inside one song and "0:10" is how the
///     UI's own span pickers read. A negative time reads as 0:00.
/// </summary>
internal static class SectionClock
{
    public static string Format(TimeSpan at)
    {
        var total = at < TimeSpan.Zero ? TimeSpan.Zero : at;
        return string.Create(CultureInfo.InvariantCulture, $"{(int)total.TotalMinutes}:{total.Seconds:00}");
    }
}
