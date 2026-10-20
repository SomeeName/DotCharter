namespace DotCharter.Services.Audio.LivePlay;

/// <summary>
///     Chart time → BAR NUMBER, for the Progress hub's per-section ranges
///     ("BAR 96-112"). Pure, and deliberately the only place that mapping is
///     made: a section's bar range is captured ONCE, when the run is recorded
///     and the chart is still open, because the practice store keeps no beat
///     map and a stored run therefore cannot be re-barred later.
///     <para>
///         A bar is a <see cref="BeatMarker" /> with <see cref="BeatMarker.IsMeasure" />
///         set; bars are 1-BASED, the way a charter counts them. A chart with no
///         measure markers at all yields <c>null</c> everywhere — which is
///         "not recorded", NOT bar 0, and the UI falls back to the time range.
///     </para>
/// </summary>
public static class LiveBarNumbers
{
    /// <summary>
    ///     The 1-based bar containing <paramref name="timeMs" />, or null when
    ///     <paramref name="beats" /> carries no measure markers (an imported
    ///     chart with a flat beat list, or no beat map at all).
    ///     <para>
    ///         A time BEFORE the first measure marker is bar 1: the pickup
    ///         belongs to the bar it leads into, and returning 0 would collide
    ///         with the "not recorded" sentinel.
    ///     </para>
    /// </summary>
    public static int? BarAt(IList<BeatMarker> beats, double timeMs)
    {
        // ONE rule, folded — the same <see cref="BarCount" /> the status rail's
        // incremental scanner and ChartPositionLabel now use, so the practice
        // store, the Issues-tab location cell and the readout under the editor
        // cannot report a chart's bars differently. This half was already the
        // right answer; see BarCount for the pickup grid where the other two
        // were a bar out.
        var count = BarCount.Zero;
        var anyMeasure = false;
        foreach (var beat in beats)
        {
            if (beat.IsMeasure) anyMeasure = true;
            if (beat.Time > timeMs) continue;
            count = count.Advance(beat.IsMeasure);
        }

        // No measure marker anywhere ⇒ this chart cannot be barred. With one
        // that is still ahead of the time, Bar's floor gives the pickup the bar
        // it leads into rather than 0, which is the "not recorded" sentinel.
        return anyMeasure ? count.Bar : null;
    }

    /// <summary>
    ///     The bar range a section spans, inclusive at both ends. The END bar is
    ///     taken one millisecond BEFORE <paramref name="endMs" /> because a
    ///     section's end is the next section's start — reading it directly would
    ///     report every section as one bar too long, and two abutting sections
    ///     as sharing a bar.
    /// </summary>
    public static (int? Start, int? End) RangeOf(
        IList<BeatMarker> beats, double startMs, double endMs)
    {
        var start = BarAt(beats, startMs);
        if (start is not { } from) return (null, null);
        var end = BarAt(beats, Math.Max(startMs, endMs - 1)) ?? from;
        return (from, Math.Max(from, end));
    }
}
