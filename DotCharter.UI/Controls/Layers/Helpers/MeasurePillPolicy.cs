namespace DotCharter.UI.Controls.Layers.Helpers;

/// <summary>
/// Pure decision logic for the measure-metadata pill (<c>BackgroundLayer</c>):
/// which segments a pill shows. A pill ALWAYS shows its measure number; the BPM
/// and time-signature segments are appended only when they carry new
/// information.
/// <para>
///     The two follow DIFFERENT rules on purpose, and 0.51 is where they parted
///     company. A TEMPO varies bar to bar, so every measure with room on screen
///     prints it (the design's <c>M73 143.88 BPM</c>) and a cramped one falls
///     back to change-plus-context. A METER is usually decided once for a whole
///     chart, so it prints on a change and — at the left edge only — when the
///     passage is not in the meter the chart opened in. Repeating "4/4" on the
///     pill a reader looks at most, on every 4/4 chart there is, was the noise
///     the owner reported.
/// </para>
/// Extracted pure so the show/hide rules and the BPM-change epsilon are
/// unit-tested without a GPU context.
/// </summary>
public static class MeasurePillPolicy
{
    /// <summary>Two BPMs within this many BPM of each other count as
    /// unchanged. Matches the pill's "0.##" formatter: 131.87 vs 132.16 is a
    /// real change and prints; 200.000 vs 200.003 is float noise and does not.</summary>
    public const double BpmChangeEpsilon = 0.005;

    /// <summary>True when <paramref name="a"/> and <paramref name="b"/> are a
    /// genuine tempo change (beyond <see cref="BpmChangeEpsilon"/>).</summary>
    public static bool BpmDiffers(double a, double b) => System.Math.Abs(a - b) > BpmChangeEpsilon;

    /// <summary>
    ///     How much room a measure needs before its pill is allowed to carry the
    ///     BPM segment as well as the number. A full pill measures roughly
    ///     8 + "M129" + 15 + "143.88 BPM" + 8 ≈ 120 px in the batcher's font, and
    ///     the next measure's pill starts at the next measure line — so below
    ///     this the two run into each other and the band becomes unreadable
    ///     text-on-text. Above it, every measure prints its tempo.
    /// </summary>
    public const double MinBpmMeasureWidthPx = 135.0;

    /// <summary>
    ///     Show the BPM segment when it changed from the previous measure, when
    ///     this pill is the first one visible in the viewport, or when the
    ///     measure is simply wide enough on screen to say it.
    ///     <para>
    ///         Through 0.51 this was change-only — a deliberate 0.15-era
    ///         decision that repeating an identical "220 BPM" on every measure
    ///         was noise. The owner's `Chart Editor.dc.html` prints it on every
    ///         measure (`M73 143.88 BPM`), which is the more useful reading:
    ///         the tempo under the cursor is answerable without scrolling back
    ///         to the last change. The width gate is what keeps that from
    ///         becoming the noise the old rule was avoiding — zoomed out far
    ///         enough, the band falls back to change-only.
    ///     </para>
    /// </summary>
    public static bool ShowBpm(bool bpmChangedFromPrevious, bool isFirstVisible, double measureWidthPx)
        => bpmChangedFromPrevious || isFirstVisible || measureWidthPx >= MinBpmMeasureWidthPx;

    /// <summary>
    ///     Show the time-signature segment when it CHANGED — and, on the
    ///     left-edge context pill, when the meter is not the one the chart
    ///     opened in.
    ///     <para>
    ///         Through 0.51 the rule was <c>changed || isFirstVisible</c>,
    ///         i.e. the same rule as <see cref="ShowBpm" />. That is right for
    ///         tempo and wrong for meter: a tempo varies bar to bar and the one
    ///         under the cursor is a real question, while a chart's meter is
    ///         usually decided once and never changes again. So the context
    ///         clause printed "4/4" on the pill the reader looks at most —
    ///         the pinned one at the left edge — on every 4/4 chart in the
    ///         library, forever, saying nothing. The owner's report was
    ///         precisely that: the band prints an extra time signature the
    ///         design's <c>M73 143.88 BPM</c> does not.
    ///     </para>
    ///     <para>
    ///         Dropping the clause outright would have been the cheaper edit
    ///         and it loses something real: scroll into the middle of a chart
    ///         that modulates and nothing on screen says you are in 7/8 until
    ///         the change itself scrolls in. <paramref name="differsFromChartOpening" />
    ///         keeps exactly that case and no other — a chart in the meter it
    ///         started in never spends a pixel repeating it.
    ///     </para>
    /// </summary>
    public static bool ShowTimeSig(bool sigChangedFromPrevious, bool isFirstVisible, bool differsFromChartOpening)
        => sigChangedFromPrevious || (isFirstVisible && differsFromChartOpening);

    /// <summary>A pill sits this far right of the measure line it belongs to.
    /// The sticky pill is pushed out by the first in-flow pill's LEFT EDGE, so
    /// <c>BackgroundLayer</c> adds this to the measure line's x before handing it
    /// to <see cref="StickyLabel.PinnedX" /> — the wrong edge would let the two
    /// overlap by exactly this much.</summary>
    public const double PillLeftOffsetPx = 4.0;
}
