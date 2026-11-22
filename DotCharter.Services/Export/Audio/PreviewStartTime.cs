using System.Globalization;

namespace DotCharter.Services.Export.Audio;

/// <summary>
///     The preview start the charter types as <c>m:ss.fff</c> (blank = automatic) and the numbers it travels as:
///     milliseconds in the project and the build options, seconds in a DLC Builder <c>.rs2dlc</c>
///     (<c>AudioPreviewStartTime</c>).
/// </summary>
public static class PreviewStartTime
{
    /// <summary>"m:ss.fff" for a start, "" for automatic.</summary>
    public static string Format(double? startMs)
    {
        if (startMs is not { } ms || ms < 0 || !double.IsFinite(ms)) return "";
        var total = (long)Math.Round(ms);
        return string.Create(CultureInfo.InvariantCulture, $"{total / 60_000}:{total / 1000 % 60:00}.{total % 1000:000}");
    }

    /// <summary>
    ///     Reads "m:ss", "m:ss.fff" or plain seconds ("90.5"). Blank is valid and means automatic
    ///     (<paramref name="startMs" /> null); anything else unreadable returns false.
    /// </summary>
    public static bool TryParse(string? text, out double? startMs)
    {
        startMs = null;
        if (string.IsNullOrWhiteSpace(text)) return true;

        var parts = text.Trim().Split(':');
        if (parts.Length > 2) return false;
        var minutes = 0L;
        if (parts.Length == 2 && !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out minutes)) return false;
        var secondsText = parts[^1];
        if (secondsText.Length == 0 || secondsText[0] is '-' or '+') return false;
        var readable = double.TryParse(secondsText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds);
        if (!readable || !double.IsFinite(seconds) || (parts.Length == 2 && seconds >= 60)) return false;

        startMs = Math.Round((minutes * 60 + seconds) * 1000, 0);
        return true;
    }

    /// <summary>A DLC Builder <c>AudioPreviewStartTime</c> (seconds) as the project's milliseconds; null when absent or negative.</summary>
    public static double? FromRs2DlcSeconds(double? seconds) =>
        seconds is { } s && s >= 0 && double.IsFinite(s) ? Math.Round(s * 1000, 0) : null;

    /// <summary>The project's milliseconds as a DLC Builder <c>AudioPreviewStartTime</c> (seconds); null for automatic.</summary>
    public static double? ToRs2DlcSeconds(double? startMs) =>
        startMs is { } ms && ms >= 0 && double.IsFinite(ms) ? ms / 1000.0 : null;
}
