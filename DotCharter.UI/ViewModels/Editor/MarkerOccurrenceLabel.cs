using System.Globalization;
using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.UI.ViewModels.Editor;

/// <summary>
///     The one label for a place a phrase, section or tone is used: "#2 · bar 17 · 0:42.0". The
///     bar is <see cref="LiveBarNumbers.BarAt" />, the rule the status rail and the Issues tab
///     count bars by, so the three never disagree; a chart without measure markers has no bars
///     and the label keeps the time alone.
/// </summary>
internal static class MarkerOccurrenceLabel
{
    public static string Format(ILocalizationService localization, int ordinal, IList<BeatMarker> beats, double timeMs)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, timeMs)).ToString(@"m\:ss\.f", CultureInfo.InvariantCulture);
        return LiveBarNumbers.BarAt(beats, timeMs) is { } bar
            ? localization.Format("Inspector_Markers_OccurrenceFmt", ordinal, bar, time)
            : localization.Format("Inspector_Markers_OccurrenceNoBarFmt", ordinal, time);
    }
}
