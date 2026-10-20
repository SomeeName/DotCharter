namespace DotCharter.UI.Controls;

/// <summary>
///     The chord box's layout arithmetic, lifted out of the control so it is a
///     pure function of (bounds, string count, fret window) and can be tested
///     without an Avalonia visual tree.
///     <para>
///         Three horizontal zones, left to right: the <b>mask pip</b> column
///         (<see cref="PipZoneWidth" />) where a click toggles the string-edit
///         mask, the <b>open-string</b> column up to <see cref="FretboardStartX" />
///         (the nut / capo, where a click means fret 0 — or the capo fret when one
///         is fitted), and then the fretted cells.
///     </para>
///     <para>
///         The pip column exists because the redesign folds the per-string
///         selector onto this one control: without a dedicated zone, "toggle the
///         mask" and "play the open string" would be the same click, which is
///         exactly the look-alike-gesture problem the redesign removes.
///     </para>
/// </summary>
public readonly record struct ChordBoxGeometry(
    double Width,
    double Height,
    int StringCount,
    int MinFret,
    int MaxFret,
    bool FlipStrings = false)
{
    /// <summary>Width of the mask-pip column at the very left.</summary>
    public const double PipZoneWidth = 14.0;

    /// <summary>X of the nut. Everything left of it is rail (pip + open string).</summary>
    public const double FretboardStartX = 40.0;

    public const double RightPadding = 10.0;
    public const double TopPadding = 22.0;
    public const double BottomPadding = 22.0;

    /// <summary>Where an open-string / capo dot is drawn on the rail.</summary>
    public const double OpenDotX = 27.0;

    public double FretboardWidth => Math.Max(0, Width - FretboardStartX - RightPadding);

    public int FretCount => Math.Max(1, MaxFret - MinFret + 1);

    public double FretSpacing => FretboardWidth / FretCount;

    public double StringSpacing =>
        Math.Max(0, Height - TopPadding - BottomPadding) / Math.Max(1, StringCount - 1);

    /// <summary>
    ///     Y of string <paramref name="stringIndex" />, laid out the way the
    ///     HIGHWAY lays it out.
    ///     <para>
    ///         This drew string 0 at the BOTTOM and its comment claimed that
    ///         matched the highway. It never did:
    ///         <c>ChartRenderContext.LaneTopFor</c> is
    ///         <c>flip ? count - 1 - index : index</c> with lane 0 at the TOP, so
    ///         unflipped the highway puts string 0 — the LOWEST — at the top and
    ///         this control put it at the bottom. The two read as mirror images
    ///         of each other, which is what was reported: "instead of red (low E)
    ///         being the top the purple (high E) is on the top".
    ///     </para>
    ///     <para>
    ///         <see cref="FlipStrings" /> is the viewport's own
    ///         <c>LeftyFlip ^ InvertStrings</c>, so the toggle inverts this
    ///         control with the highway rather than leaving the two disagreeing
    ///         in the other direction.
    ///     </para>
    /// </summary>
    public double StringY(int stringIndex) =>
        TopPadding + LaneOf(stringIndex) * StringSpacing;

    /// <summary>The lane a string occupies, 0 at the TOP — the highway's rule.</summary>
    public int LaneOf(int stringIndex) =>
        FlipStrings ? StringCount - 1 - stringIndex : stringIndex;

    /// <summary>Centre X of a fretted cell. Only meaningful for a fret inside
    /// [<see cref="MinFret" />, <see cref="MaxFret" />].</summary>
    public double FretCenterX(int fret) =>
        FretboardStartX + (fret - MinFret) * FretSpacing + FretSpacing / 2;

    /// <summary>True while the pointer is over the mask-pip column.</summary>
    public static bool IsOverMaskPip(double x) => x is >= 0 and < PipZoneWidth;

    /// <summary>The string under <paramref name="y" />, or <c>-1</c>.</summary>
    public int HitString(double y)
    {
        var spacing = StringSpacing;
        if (spacing <= 0)
            return StringCount > 0 && y >= 0 && y <= Height ? 0 : -1;

        if (y < TopPadding - spacing / 2 || y > Height - BottomPadding + spacing / 2)
            return -1;

        for (var i = 0; i < StringCount; i++)
        {
            if (Math.Abs(y - StringY(i)) < spacing / 2)
                return i;
        }

        return -1;
    }

    /// <summary>
    ///     Full hit test. Returns false when the point is outside every string
    ///     lane. <paramref name="capo" /> decides what the open-string zone
    ///     means: with a capo fitted, "open" IS the capo fret.
    /// </summary>
    public bool TryHit(double x, double y, int capo, out int stringIndex, out int fret)
    {
        fret = -1;
        stringIndex = HitString(y);
        if (stringIndex < 0) return false;

        if (x < FretboardStartX)
        {
            fret = capo > 0 ? capo : 0;
            return true;
        }

        var spacing = FretSpacing;
        if (spacing <= 0) return false;

        var index = (int)((x - FretboardStartX) / spacing);
        if (index < 0 || index >= FretCount) return false;

        fret = MinFret + index;
        return true;
    }

    /// <summary>
    ///     The visible fret window for a shape. Always at least five frets wide,
    ///     never starts below the capo, and ignores notes at or under the capo
    ///     (those are open strings and live on the rail).
    /// </summary>
    public static (int MinFret, int MaxFret) DisplayFrets(IEnumerable<int>? frets, int capo)
    {
        var floor = capo > 0 ? capo + 1 : 1;
        var min = int.MaxValue;
        var max = int.MinValue;

        if (frets != null)
        {
            foreach (var f in frets)
            {
                if (f <= capo) continue;
                if (f < min) min = f;
                if (f > max) max = f;
            }
        }

        if (min == int.MaxValue) return (floor, floor + 4);

        var lo = Math.Max(floor, min - 1);
        var hi = Math.Max(lo + 4, max + 1);
        return (lo, hi);
    }
}
