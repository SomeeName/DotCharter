namespace DotCharter.UI.Controls.Layers.Helpers;

/// <summary>
/// Pure decision logic for the measure-metadata pill (<c>BackgroundLayer</c>):
/// which measures get a pill at the current zoom (<see cref="Plan" />) and
/// which segments a pill shows. A pill ALWAYS shows its measure number and the
/// tempo it is in; the time signature only when it carries new information.
/// <para>
///     A TEMPO varies bar to bar, so every pill states the one it is in. A METER
///     is usually decided once for a whole chart, so it prints on a change, on the
///     first pill and on the sticky left-edge pill only — "4/4" on every pill of
///     every 4/4 chart is noise.
/// </para>
/// Extracted pure so the placement rules and the BPM-change epsilon are
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

    /// <summary>A pill sits this far right of the measure line it belongs to.
    /// The sticky pill is pushed out by the first in-flow pill's LEFT EDGE, so
    /// <see cref="Plan" /> places every pill at its measure line's x plus this, and the
    /// sticky pill is pushed by that left edge.</summary>
    public const double PillLeftOffsetPx = 4.0;

    // ── the measure row at any zoom ──────────────────────────────────────

    /// <summary>Clear space kept between two pills, so neighbours never touch.</summary>
    public const double MinPillGapPx = 6.0;

    /// <summary>The coarsest thinning: one pill every this many measures. Far past
    /// any real zoom-out (a 10-minute song is ~300 measures).</summary>
    public const int MaxStride = 1024;

    /// <summary>
    ///     How many measures one pill speaks for: the smallest power of two (1, 2, 4,
    ///     8, 16 …) whose span is wide enough for a pill plus its gap. The row is never
    ///     hidden for zoom: a zoomed-out chart with no measure numbers, tempo or meter
    ///     is unreadable, so the row thins by a stride instead and reads at every zoom.
    /// </summary>
    public static int Stride(double measureWidthPx, double pillWidthPx)
    {
        if (double.IsPositiveInfinity(measureWidthPx)) return 1;
        if (!(measureWidthPx > 0)) return MaxStride;
        var stride = 1;
        while (stride < MaxStride && stride * measureWidthPx < pillWidthPx + MinPillGapPx) stride *= 2;
        return stride;
    }

    /// <summary>
    ///     Whether measure <paramref name="measureNumber" /> carries the stride label.
    ///     Aligned to the measure NUMBER (1, 9, 17, 25 … for a stride of 8), never to
    ///     whatever happens to be first on screen, so the labels do not jump from
    ///     measure to measure while the view scrolls.
    /// </summary>
    public static bool OnStride(int measureNumber, int stride) =>
        stride <= 1 || ((measureNumber - 1) % stride + stride) % stride == 0;

    /// <summary>One measure line in view, as the planner sees it.</summary>
    /// <param name="MeasureNumber">The chart's measure number.</param>
    /// <param name="X">The measure line's screen x.</param>
    /// <param name="BpmChanged">The tempo differs from the previous measure's.</param>
    /// <param name="SigChanged">The meter differs from the previous measure's.</param>
    public readonly record struct MeasureSlot(int MeasureNumber, double X, bool BpmChanged, bool SigChanged);

    /// <summary>One pill to draw. Every pill states its measure number AND the tempo it
    /// is in; <see cref="ShowSig" /> adds the meter. <see cref="Full" /> is the
    /// <c>M17 | 120 BPM | 4/4</c> form, otherwise the compact <c>17 · 120</c>.</summary>
    public readonly record struct PlannedPill(int Slot, double Left, double Width, bool Full, bool ShowSig);

    /// <summary>The width of slot <paramref name="slot" />'s pill in a form.</summary>
    public delegate double PillWidth(int slot, bool full, bool showSig);

    /// <summary>
    ///     Which measures get a pill, in which form, so that no two pills overlap at
    ///     ANY zoom. Pure: <c>BackgroundLayer</c> feeds it the measures in view and the
    ///     batcher's text widths; <c>UI/Rendering/MeasurePillPolicyTests</c> feeds it
    ///     synthetic ones.
    ///     <list type="number">
    ///         <item>The first pill in view, when no sticky pill is pinned at the left
    ///             edge (<paramref name="firstCarriesContext" />), always shows: number,
    ///             tempo and meter.</item>
    ///         <item>Every tempo or meter CHANGE shows, as a pill of its own, even in a
    ///             measure the stride skips — it wins over the stride label it would
    ///             collide with. Two changes closer than one compact pill cannot both
    ///             be drawn without overlapping; the earlier one keeps its place.</item>
    ///         <item>Then one pill per <see cref="Stride" /> measures, stride-aligned,
    ///             wherever it fits.</item>
    ///         <item>A pill becomes the full form when that form still ends a gap
    ///             before the next pill.</item>
    ///     </list>
    ///     <paramref name="placed" /> is cleared and refilled, sorted by
    ///     <see cref="PlannedPill.Left" />; the caller owns it so a frame allocates nothing.
    /// </summary>
    public static void Plan(IReadOnlyList<MeasureSlot> slots, PillWidth width, bool firstCarriesContext,
        List<PlannedPill> placed)
    {
        placed.Clear();
        if (slots.Count == 0) return;

        var span = slots.Count > 1 ? (slots[^1].X - slots[0].X) / (slots.Count - 1) : double.PositiveInfinity;
        var widest = 0.0;
        for (var i = 0; i < slots.Count; i++) widest = Math.Max(widest, width(i, false, false));
        var stride = Stride(span, widest);

        if (firstCarriesContext) TryPlace(placed, slots, width, 0, showSig: true, force: true);

        for (var i = 0; i < slots.Count; i++)
        {
            if (slots[i].BpmChanged || slots[i].SigChanged)
                TryPlace(placed, slots, width, i, showSig: slots[i].SigChanged, force: false);
        }

        for (var i = 0; i < slots.Count; i++)
        {
            if (OnStride(slots[i].MeasureNumber, stride))
                TryPlace(placed, slots, width, i, showSig: slots[i].SigChanged, force: false);
        }

        // Grow each pill to the full form where that still clears the next one.
        for (var k = 0; k < placed.Count; k++)
        {
            var p = placed[k];
            var full = width(p.Slot, true, p.ShowSig);
            var next = k + 1 < placed.Count ? placed[k + 1].Left : double.PositiveInfinity;
            if (p.Left + full + MinPillGapPx <= next) placed[k] = p with { Width = full, Full = true };
        }
    }

    private static void TryPlace(List<PlannedPill> placed, IReadOnlyList<MeasureSlot> slots, PillWidth width,
        int slot, bool showSig, bool force)
    {
        var left = slots[slot].X + PillLeftOffsetPx;
        var w = width(slot, false, showSig);

        // Sorted by Left: only the two neighbours of the insertion point can overlap.
        var lo = 0;
        var hi = placed.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (placed[mid].Left < left) lo = mid + 1;
            else hi = mid;
        }

        var at = lo;
        if (at < placed.Count && placed[at].Slot == slot) return;
        if (at > 0 && placed[at - 1].Slot == slot) return;
        if (!force)
        {
            if (at > 0 && placed[at - 1].Left + placed[at - 1].Width + MinPillGapPx > left) return;
            if (at < placed.Count && left + w + MinPillGapPx > placed[at].Left) return;
        }

        placed.Insert(at, new PlannedPill(slot, left, w, false, showSig));
    }
}
