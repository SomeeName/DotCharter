using System.Globalization;

namespace DotCharter.UI.ViewModels.Editor;

/// <summary>
///     The one line printed under the timeline's <c>SONG MAP</c> eyebrow —
///     <c>4:12 · 84 BARS</c> in the owner's `Chart Editor.dc.html`.
///     <para>
///         Pure so it is tested without a shell: the whole point of the caption
///         is that it answers "how long is this song and how many bars is that"
///         at a glance, and the two states worth getting right (no audio yet, no
///         beat map yet) are exactly the ones a running app is least likely to
///         be sitting in when someone looks.
///     </para>
/// </summary>
public static class SongMapCaption
{
    /// <summary>
    ///     <paramref name="barsText" /> is the already-pluralized count
    ///     ("1 BAR" / "84 BARS"), because choosing the form is the localization
    ///     service's job and this type is deliberately free of it.
    ///     A song with no audio length prints only its bars; a chart with no
    ///     beat map prints only its duration; with neither, nothing at all —
    ///     never a "0:00 · 0 BARS", which reads as a measurement rather than as
    ///     the absence of one.
    /// </summary>
    /// <remarks>The separator goes between the two halves. The timeline's caption cell passes a
    ///     line break: at its 118 px the one-line form ("3:03 · 156 bars", "3:03 · 156 Takte") never
    ///     fit and printed "3:03 · 156 …".</remarks>
    public static string Describe(double totalMs, int barCount, string barsText, string separator = " · ")
    {
        var duration = Duration(totalMs);
        var bars = barCount > 0 ? barsText : null;

        if (duration == null) return bars ?? string.Empty;
        return bars == null ? duration : duration + separator + bars;
    }

    /// <summary>
    ///     <c>m:ss</c>, growing to <c>h:mm:ss</c> only when it has to — the
    ///     design prints <c>4:12</c>, not <c>04:12</c>, because this is a
    ///     duration and not a timestamp. Null when there is no audio yet.
    /// </summary>
    public static string? Duration(double totalMs)
    {
        if (double.IsNaN(totalMs) || totalMs <= 0) return null;

        var t = TimeSpan.FromMilliseconds(totalMs);
        return t.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture,
                $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture,
                $"{(int)t.TotalMinutes}:{t.Seconds:00}");
    }
}
