using System.Globalization;

namespace DotCharter.Core.Utilities;

/// <summary>Invariant elapsed song time, with total hours and zero to three fractional digits.</summary>
public static class ElapsedTimeLabel
{
    /// <summary>
    ///     Formats m:ss below an hour and h:mm:ss thereafter. Negative times clamp to zero;
    ///     fractions truncate. <paramref name="padMinutes" /> selects mm:ss below an hour.
    /// </summary>
    public static string Format(double timeMs, int fractionalDigits = 0, bool padMinutes = false)
    {
        if (fractionalDigits is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(fractionalDigits));
        var milliseconds = (long)(timeMs > 0 ? timeMs : 0);
        var totalSeconds = milliseconds / 1000;
        var hours = totalSeconds / 3600;
        var minutes = totalSeconds / 60 % 60;
        var seconds = totalSeconds % 60;
        var clock = hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:00}:{seconds:00}")
            : padMinutes
                ? string.Create(CultureInfo.InvariantCulture, $"{minutes:00}:{seconds:00}")
                : string.Create(CultureInfo.InvariantCulture, $"{minutes}:{seconds:00}");
        return fractionalDigits switch
        {
            1 => string.Create(CultureInfo.InvariantCulture, $"{clock}.{milliseconds % 1000 / 100}"),
            2 => string.Create(CultureInfo.InvariantCulture, $"{clock}.{milliseconds % 1000 / 10:00}"),
            3 => string.Create(CultureInfo.InvariantCulture, $"{clock}.{milliseconds % 1000:000}"),
            _ => clock
        };
    }
}
