using System.Globalization;
using DotCharter.Core.Domain.Events;

namespace DotCharter.Core.Utilities;

/// <summary>
///     Chart time → the readout a charter reads a POSITION from: <c>M19.3</c>
///     (bar 19, beat 3) and <c>02:13.16</c>, joined as
///     <c>M19.3 · 02:13.16</c>.
///     <para>
///         It is the display counterpart to <see cref="TimelineMath" />, the same
///         way <see cref="TuningNameResolver" /> is to
///         <see cref="TuningUtilities" />: pure, allocation-light, and free of
///         any UI type so the mapping can be pinned in the default gate.
///     </para>
///     <para>
///         <b>The bar/beat counting is <see cref="BarCount" />'s, and so is
///         everyone else's.</b> <c>StatusBarViewModel.RefreshPlaybackStatus</c>
///         prints <c>M{bar}.{beat}</c> under the editor at 60 Hz, the timeline
///         draws a measure pill, and the practice store records a bar range —
///         three readings of one beat map, which is three chances to put
///         different bar numbers on screen for one instant of the song. The
///         status bar cannot simply CALL this one (it is an incremental scanner
///         carrying cached indices across ticks), so what is shared is a foldable
///         COUNT rather than a function: see <see cref="BarCount" /> for the rule
///         and for the pickup grid where the two answers would differ by one.
///     </para>
///     <para>
///         One deliberate divergence: a chart whose beat map carries NO measure
///         marker at all gets no <c>M…</c> token here, only the time. The status
///         bar keeps counting (and prints "M1.57") because a live readout must
///         always say something; a stored issue location that claims bar 1 for
///         the whole song is just a wrong bar number. Same call
///         <c>LiveBarNumbers.BarAt</c> makes for the practice store.
///     </para>
/// </summary>
public static class ChartPositionLabel
{
    /// <summary>Separator between the bar token and the time token — the mono
    /// middle dot the rest of this design layer uses between two facts.</summary>
    public const string Separator = " · ";

    /// <summary>
    ///     The full location readout for <paramref name="timeMs" />, or an EMPTY
    ///     string when the issue carries no time at all. Never invents a
    ///     position: with no beat map, or none carrying a measure marker, it
    ///     falls back to the time alone.
    /// </summary>
    public static string Format(IReadOnlyList<BeatMarker>? beats, double? timeMs)
    {
        if (timeMs is not { } ms) return string.Empty;
        var bar = Measure(beats, ms);
        var time = Time(ms);
        return bar is null ? time : bar + Separator + time;
    }

    /// <summary>
    ///     <c>M{bar}.{beat}</c> for <paramref name="timeMs" />, or <c>null</c>
    ///     when the beat map is empty or carries no measure marker.
    /// </summary>
    public static string? Measure(IReadOnlyList<BeatMarker>? beats, double timeMs)
    {
        if (beats is null || beats.Count == 0) return null;

        var hasMeasure = false;
        for (var i = 0; i < beats.Count; i++)
        {
            if (beats[i].IsMeasure)
            {
                hasMeasure = true;
                break;
            }
        }

        if (!hasMeasure) return null;

        // The last beat at or before the time. Nothing at or before it (a
        // pre-roll position) reads as the first beat, which is what the status
        // bar's own binary search leaves behind.
        int lo = 0, hi = beats.Count - 1, idx = 0;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            if (beats[mid].Time <= timeMs)
            {
                idx = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        var count = BarCount.Zero.AdvanceThrough(beats, 0, idx);
        return string.Create(CultureInfo.InvariantCulture, $"M{count.Bar}.{count.BeatInBar}");
    }

    /// <summary>
    ///     <c>mm:ss.ff</c>, the format every other time readout in the editor
    ///     prints (the status bar, the timeline ruler). Negative times clamp to
    ///     zero rather than printing a negative clock.
    /// </summary>
    public static string Time(double timeMs)
    {
        var clamped = timeMs > 0 ? timeMs : 0;
        // Quantize to 10 ms first so the hundredths never round UP into a second
        // the playhead has not reached (TimeSpan formatting truncates, but the
        // double it is handed can sit a fraction of a millisecond short).
        var centis = (long)(clamped / 10.0);
        return TimeSpan.FromMilliseconds(centis * 10.0)
            .ToString(@"mm\:ss\.ff", CultureInfo.InvariantCulture);
    }
}
