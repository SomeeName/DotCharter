using DotCharter.Core.Domain.Events;

namespace DotCharter.Core.Utilities;

/// <summary>
///     How this app numbers BARS and the beats inside them — the one rule, folded
///     one beat at a time so an incremental scanner and a from-scratch walk cannot
///     disagree.
/// </summary>
/// <remarks>
///     <para>
///         There were three counters and two answers. The TIMELINE numbers a bar
///         by its ordinal among the <see cref="BeatMarker.IsMeasure" /> beats
///         (<c>FrameSnapshot</c>'s <c>measureCounter</c>, drawn as the measure
///         pill), and so does the practice store
///         (<c>LiveBarNumbers.BarAt</c>). The STATUS RAIL and
///         <see cref="ChartPositionLabel" /> instead started at bar 1 and
///         incremented on every measure marker <i>after the first beat</i> — which
///         is the same number only when beat 0 is itself a measure marker.
///     </para>
///     <para>
///         On a PICKUP grid it is not. A chart whose beat map opens with an
///         anacrusis has its first measure marker at beat 1 or later, and there
///         the rail printed <c>M2.1</c> at the very instant the timeline drew the
///         pill <c>1</c> — two readouts of the same chart, one bar apart, with the
///         Issues-tab location cell taking the rail's side and a recorded run's
///         "BAR 96-112" taking the timeline's.
///     </para>
///     <para>
///         The timeline's answer wins, because it is the one a charter reads off
///         the grid and the one already written into every stored run: <b>the bar
///         number is the count of measure markers at or before the time</b>, and a
///         position before the FIRST marker is bar 1 — the pickup belongs to the
///         bar it leads into, and 0 would collide with "this chart cannot be
///         barred". Charts whose first beat IS a measure marker — very nearly all
///         of them — are unaffected in either direction.
///     </para>
/// </remarks>
public readonly record struct BarCount(int Measures, int BeatsSinceMeasure)
{
    /// <summary>Nothing counted yet — before the first beat of the map.</summary>
    public static readonly BarCount Zero = new(0, 0);

    /// <summary>Folds one more beat in, in time order.</summary>
    public BarCount Advance(bool isMeasure) =>
        isMeasure ? new BarCount(Measures + 1, 0) : new BarCount(Measures, BeatsSinceMeasure + 1);

    /// <summary>The 1-based bar. Floored at 1 so a pre-first-marker pickup reads
    /// as the bar it leads into rather than as bar 0.</summary>
    public int Bar => Measures < 1 ? 1 : Measures;

    /// <summary>The 1-based beat inside <see cref="Bar" />.</summary>
    public int BeatInBar => BeatsSinceMeasure + 1;

    /// <summary>Folds <paramref name="beats" /> from <paramref name="fromIndex" />
    /// through <paramref name="throughIndex" /> (both inclusive) into this
    /// count. Out-of-range indexes are clamped; an empty span returns this.</summary>
    public BarCount AdvanceThrough(IReadOnlyList<BeatMarker> beats, int fromIndex, int throughIndex)
    {
        ArgumentNullException.ThrowIfNull(beats);
        if (fromIndex < 0) fromIndex = 0;
        if (throughIndex >= beats.Count) throughIndex = beats.Count - 1;

        var count = this;
        for (var i = fromIndex; i <= throughIndex; i++) count = count.Advance(beats[i].IsMeasure);
        return count;
    }
}
