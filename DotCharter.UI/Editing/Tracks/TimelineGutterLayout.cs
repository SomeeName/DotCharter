namespace DotCharter.UI.Editing.Tracks;

/// <summary>
///     THE vertical layout of one 2D timeline: where the ribbon rows sit, where
///     the string lanes start, how tall each lane is, and which lane a string
///     index draws in.
///     <para>
///         It exists because that arithmetic is now needed in TWO places — the
///         renderer that draws the chart and the <c>TimelineGutterControl</c>
///         that labels its lanes down the left edge — and those two are side by
///         side on screen. A caption one pixel off its lane is not a cosmetic
///         defect: it names the wrong string. So the formula lives here once and
///         both callers use it, rather than each carrying a copy that agrees
///         today.
///     </para>
///     <para>
///         A readonly struct with no allocation: <c>ChartRenderer</c> builds one
///         per frame on the UI thread.
///     </para>
/// </summary>
public readonly struct TimelineGutterLayout
{
    private TimelineGutterLayout(
        float topMargin, float bottomMargin, float laneAreaHeight, float laneHeight,
        int stringCount, bool flipStrings, RibbonLayout ribbon)
    {
        TopMargin = topMargin;
        BottomMargin = bottomMargin;
        LaneAreaHeight = laneAreaHeight;
        LaneHeight = laneHeight;
        StringCount = stringCount;
        FlipStrings = flipStrings;
        Ribbon = ribbon;
    }

    /// <summary>Height reserved above the lanes — the whole ribbon.</summary>
    public float TopMargin { get; }

    /// <summary>Height reserved below the lanes for the time ruler.</summary>
    public float BottomMargin { get; }

    public float LaneAreaHeight { get; }
    public float LaneHeight { get; }
    public int StringCount { get; }

    /// <summary>
    ///     The COMBINED vertical string flip: lefty XOR inverted-strings. Both
    ///     flags flip the lane order, so together they cancel — the renderer's
    ///     <c>ChartRenderContext.LaneTopFor</c> and every hit-tester take the
    ///     same combined value, and so must the gutter or its labels run
    ///     upside down for a left-handed player.
    /// </summary>
    public bool FlipStrings { get; }

    public RibbonLayout Ribbon { get; }

    /// <summary>
    ///     The layout for a timeline <paramref name="height" /> px tall.
    ///     <paramref name="drawLabels" /> false is the stacked/preview case,
    ///     where the chart fills the whole control and there is no ribbon and no
    ///     ruler.
    /// </summary>
    public static TimelineGutterLayout For(
        double height, RibbonLayout ribbon, int stringCount, bool flipStrings,
        double bottomMargin, bool drawLabels = true)
    {
        var top = drawLabels ? ribbon.TotalHeight : 0f;
        var bottom = drawLabels ? (float)bottomMargin : 0f;
        var laneArea = (float)height - top - bottom;
        var count = Math.Max(1, stringCount);
        return new TimelineGutterLayout(top, bottom, laneArea, laneArea / count, count, flipStrings, ribbon);
    }

    /// <summary>
    ///     Top Y of the lane <paramref name="stringIndex" /> draws in. Must stay
    ///     byte-identical to <c>ChartRenderContext.LaneTopFor</c> — pinned by
    ///     <c>UI/TimelineGutterLayoutTests</c>.
    /// </summary>
    public float LaneTopFor(int stringIndex)
        => TopMargin + (FlipStrings ? StringCount - 1 - stringIndex : stringIndex) * LaneHeight;

    /// <summary>Vertical centre of that lane — where a gutter caption sits.</summary>
    public float LaneCentreFor(int stringIndex) => LaneTopFor(stringIndex) + LaneHeight * 0.5f;

    /// <summary>
    ///     Everything that can move a caption. The gutter repaints only when
    ///     this changes, which is what keeps it off the per-frame path: the
    ///     renderer rebuilds a layout every frame and almost every one of them
    ///     is identical to the last.
    /// </summary>
    public bool SameShapeAs(TimelineGutterLayout other)
        => TopMargin.Equals(other.TopMargin)
           && BottomMargin.Equals(other.BottomMargin)
           && LaneHeight.Equals(other.LaneHeight)
           && StringCount == other.StringCount
           && FlipStrings == other.FlipStrings
           && Ribbon.Lanes == other.Ribbon.Lanes
           && Ribbon.Density == other.Ribbon.Density;
}
