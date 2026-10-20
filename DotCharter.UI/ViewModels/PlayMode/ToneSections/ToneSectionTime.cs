using System.Globalization;

namespace DotCharter.UI.ViewModels.PlayMode.ToneSections;

/// <summary>
///     The section list's clock: <c>m:ss</c> text in and out.
///
///     <para>
///         <b>Text, not a numeric spinner.</b> The owner describes a section as
///         "0:10 to 0:30" and that is what the boxes take. A spinner over
///         milliseconds would be exact and unusable, and a slider cannot be typed
///         into at all.
///     </para>
///     <para>
///         PURE and internal so it is a unit test rather than a layout one:
///         every parsing rule below shipped as a bug in some other app —
///         <c>90</c> meaning a minute and a half, <c>0:75</c> being accepted as
///         75 seconds, a decimal comma in a German locale, and a negative time.
///         Parsing is INVARIANT-culture on the fraction deliberately: the text is
///         a timecode, not a formatted number, and a user who types
///         <c>0:10.5</c> means half a second in every language.
///     </para>
/// </summary>
internal static class ToneSectionTime
{
    /// <summary>"1:07", or "1:07.5" when the fraction is not a whole second.
    /// Minutes are never zero-padded and seconds always are, which is what makes
    /// a column of them readable.</summary>
    public static string Format(TimeSpan value)
    {
        if (value < TimeSpan.Zero) value = TimeSpan.Zero;
        var minutes = (int)value.TotalMinutes;
        var seconds = value.Seconds;
        var tenths = (int)Math.Round(value.Milliseconds / 100.0);

        // 59.96 s rounds to 60.0 — carry it rather than printing "0:59.10".
        if (tenths >= 10)
        {
            tenths = 0;
            seconds++;
            if (seconds >= 60)
            {
                seconds = 0;
                minutes++;
            }
        }

        return tenths == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{minutes}:{seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes}:{seconds:00}.{tenths}");
    }

    /// <summary>
    ///     <c>m:ss[.f]</c>, or a bare seconds count. Returns false for anything
    ///     else rather than guessing — a section whose time was silently
    ///     misparsed matches the wrong stretch of song and says nothing about it.
    /// </summary>
    /// <param name="text">What the user typed.</param>
    /// <param name="value">The parsed time; <see cref="TimeSpan.Zero" /> on
    /// failure.</param>
    public static bool TryParse(string? text, out TimeSpan value)
    {
        value = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var trimmed = text.Trim();
        var colon = trimmed.IndexOf(':');

        if (colon < 0)
        {
            // A bare number is SECONDS. "90" is a minute and a half in a stopwatch
            // and ninety seconds here; both agree, and the colon form is how the
            // hint asks for it.
            if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var bare) ||
                double.IsNaN(bare) || bare < 0)
            {
                return false;
            }

            value = TimeSpan.FromSeconds(bare);
            return true;
        }

        var minutesText = trimmed[..colon];
        var secondsText = trimmed[(colon + 1)..];

        if (!int.TryParse(minutesText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) ||
            minutes < 0)
        {
            return false;
        }

        if (!double.TryParse(secondsText, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ||
            double.IsNaN(seconds) || seconds < 0)
        {
            return false;
        }

        // "0:75" is a typo, not 75 seconds: accepting it would make 1:15 and 0:75
        // two spellings of one time and the round trip would rewrite what was
        // typed.
        if (seconds >= 60) return false;

        value = TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
        return true;
    }
}
