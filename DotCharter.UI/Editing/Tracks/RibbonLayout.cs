namespace DotCharter.UI.Editing.Tracks;

/// <summary>
/// Builds the <see cref="RibbonLayout" /> for a track's
/// <see cref="RibbonVisibility" /> toggles (which live in Services with the
/// track context).
/// </summary>
public static class RibbonVisibilityLayout
{
    public static RibbonLayout BuildLayout(this RibbonVisibility visibility) =>
        new(visibility.Lanes(), visibility.Density());

    /// <summary>The user toggles as one <see cref="RibbonLanes" /> set.</summary>
    public static RibbonLanes Lanes(this RibbonVisibility visibility) =>
        (visibility.ShowTones ? RibbonLanes.Tones : RibbonLanes.None)
        | (visibility.ShowSections ? RibbonLanes.Sections : RibbonLanes.None)
        | (visibility.ShowPhrases ? RibbonLanes.Phrases : RibbonLanes.None)
        | (visibility.ShowLyrics ? RibbonLanes.Lyrics : RibbonLanes.None)
        | (visibility.ShowMeasureMetadata ? RibbonLanes.MeasureMetadata : RibbonLanes.None)
        | (visibility.ShowLinks ? RibbonLanes.Links : RibbonLanes.None);

    /// <summary>The row height the user picked (Compact rows).</summary>
    public static RibbonDensity Density(this RibbonVisibility visibility) =>
        visibility.CompactRows ? RibbonDensity.Compact : RibbonDensity.Normal;
}

/// <summary>
///     The lanes of the top ribbon, as one set. A new lane (LINKS) is a new
///     flag here, an entry in <see cref="RibbonLayout" />'s row order and a Y
///     property — never another constructor parameter (replaced the five
///     positional bools).
/// </summary>
[Flags]
public enum RibbonLanes
{
    None = 0,
    Tones = 1 << 0,
    Sections = 1 << 1,
    Phrases = 1 << 2,
    Lyrics = 1 << 3,
    MeasureMetadata = 1 << 4,

    /// <summary>Linked measures: a chip per placement. Not in <see cref="All" />: the row
    ///     exists only while the track has link groups (<c>RibbonLaneRules</c>).</summary>
    Links = 1 << 5,

    /// <summary>The content lanes every chart can have.</summary>
    All = Tones | Sections | Phrases | Lyrics | MeasureMetadata
}

/// <summary>How tall the ribbon's rows are drawn. Compact keeps every
/// lane, only shorter, and hands the difference to the note lanes.</summary>
public enum RibbonDensity
{
    Normal,
    Compact
}

/// <summary>
/// Computed Y-positions and total ribbon height for the top ribbon based
/// on which rows are visible. Replaces the hardcoded ToneY/SectionY/...
/// constants and the binary HIGHWAY_TOP_MARGIN/_NO_LYRICS switch.
/// </summary>
public readonly struct RibbonLayout
{
    /// <summary>
    ///     Vertical height reserved for one ribbon row — the lane PITCH, and
    ///     the number the gutter's captions are spaced by.
    ///     <para>
    ///         <b>24, from the owner's <c>Chart Timeline.reference.png</c></b>
    ///         (four lanes over a 96 px band, captions 24 px apart). It was 30
    ///         with a 20 px bar in it, i.e. 5 px of dead space above and below
    ///         every bar in every lane; measured against the reference the
    ///         whole ribbon spent 132 px on content the design draws in 96. The
    ///         owner's report was that the app's rows "cost more height than
    ///         they use" — this constant and <see cref="BarHeight" /> are that
    ///         report.
    ///     </para>
    /// </summary>
    public const float RowHeight = 24f;

    /// <summary>
    ///     Height of the coloured BAR drawn inside one ribbon row — a section
    ///     chip, a phrase chip, a tone badge, a lyric syllable.
    ///     <para>
    ///         <b>It lives here, beside the pitch it has to fit inside, and not
    ///         in <c>ChartEditorConstants</c>.</b> A bar height
    ///         and a row height are one decision: the reference draws a 19 px
    ///         bar in a 24 px lane, so the bar all but fills its row and the row
    ///         is doing work with every pixel it occupies. Two constants in two
    ///         files is how that ratio drifts back to the 20-in-30 it was.
    ///     </para>
    /// </summary>
    public const float BarHeight = 19f;

    /// <summary>Inset above the ribbon's first row. The reference starts its
    /// first lane immediately under the toolbar; this is a hairline of
    /// breathing room, not a margin.</summary>
    public const float TopPadding = 2f;

    /// <summary>
    ///     Gap between the last visible event row and the measure band.
    ///     <b>Zero on purpose</b> — in the reference the MEASURES lane is just
    ///     the next lane down, and the gutter already draws a hairline seam
    ///     under every caption band, so the separation is drawn rather than
    ///     spent.
    /// </summary>
    public const float RibbonBottomPadding = 0f;

    /// <summary>Height reserved below the ribbon for the per-measure metadata
    /// pill. One lane, near enough: a 17 px pill in a 20 px band.</summary>
    public const float MetadataBandHeight = 20f;

    /// <summary>Structural separator bar between the event ribbon and the
    /// note lanes — the same visual language as the pane splitters, so the
    /// two regions read as distinct areas. Three pixels: two hairline edges
    /// with one row of fill between them, which is as close as this treatment
    /// gets to the reference's single rule.</summary>
    public const float SeparatorHeight = 3f;

    /// <summary>Top Y of the bar drawn inside the row starting at
    /// <paramref name="rowY" />, at NORMAL density. Drawing code asks the layout
    /// it draws with (<see cref="BarTop" />), which also knows compact rows.</summary>
    public static float BarTopIn(float rowY) => rowY + (RowHeight - BarHeight) * 0.5f;

    /// <summary>Bottom Y of that bar: where an event's drop-line starts and
    /// where the active-section underline is tucked.</summary>
    public static float BarBottomIn(float rowY) => BarTopIn(rowY) + BarHeight;

    /// <summary>Top Y of the measure pill inside the metadata band starting at
    /// <paramref name="bandY" />.</summary>
    public static float PillTopIn(float bandY) => bandY + (MetadataBandHeight - PillHeight) * 0.5f;

    /// <summary>Height of the measure-metadata pill. Same rule as
    /// <see cref="BarHeight" />: it fills its band rather than floating in
    /// it (it was 16 in a 22 px band, drawn 1 px ABOVE the band's own top, so
    /// 7 of those 22 px were empty).</summary>
    public const float PillHeight = 17f;

    /// <summary>
    ///     <b>Compact rows</b>: the owner-approved mockup
    ///     (<c>03_links_row_compact_1080</c>) draws every ribbon row at 18 px and
    ///     the measures band at 15. The chip keeps a 15 px bar — the chart font's
    ///     cap height with room above and below, so a section name stays
    ///     readable — and the gutter's 9.5 px captions still fit their band
    ///     (<c>Perf/TimelineLaneMenuUiPerfSmokeTests</c> measures both).
    /// </summary>
    public const float CompactRowHeight = 18f;

    /// <summary>The chip inside a compact row. See <see cref="CompactRowHeight" />.</summary>
    public const float CompactBarHeight = 15f;

    /// <summary>The measures band in compact density.</summary>
    public const float CompactMetadataBandHeight = 15f;

    /// <summary>The measure pill in compact density: it fills its band like the normal one.</summary>
    public const float CompactPillHeight = 13f;

    /// <summary>The event rows, top to bottom — the one place their ORDER lives.
    ///     A new lane is a flag and an entry here.</summary>
    private static readonly RibbonLanes[] EventRowOrder =
        [RibbonLanes.Tones, RibbonLanes.Sections, RibbonLanes.Phrases, RibbonLanes.Lyrics, RibbonLanes.Links];

    public RibbonLayout(RibbonLanes lanes, RibbonDensity density = RibbonDensity.Normal)
    {
        Lanes = lanes;
        Density = density;
        var compact = density == RibbonDensity.Compact;
        RowPitch = compact ? CompactRowHeight : RowHeight;
        Bar = compact ? CompactBarHeight : BarHeight;
        Band = compact ? CompactMetadataBandHeight : MetadataBandHeight;
        Pill = compact ? CompactPillHeight : PillHeight;

        ToneY = RowTopFor(lanes, RowPitch, RibbonLanes.Tones);
        SectionY = RowTopFor(lanes, RowPitch, RibbonLanes.Sections);
        PhraseY = RowTopFor(lanes, RowPitch, RibbonLanes.Phrases);
        LyricY = RowTopFor(lanes, RowPitch, RibbonLanes.Lyrics);
        LinkY = RowTopFor(lanes, RowPitch, RibbonLanes.Links);

        var y = TopPadding;
        foreach (var lane in EventRowOrder)
        {
            if ((lanes & lane) != 0) y += RowPitch;
        }

        var ribbonBottom = y + RibbonBottomPadding;
        MetadataY = ribbonBottom;
        TotalHeight = ribbonBottom + ((lanes & RibbonLanes.MeasureMetadata) != 0 ? Band : 0f) + SeparatorHeight;
    }

    /// <summary>Where <paramref name="lane" />'s row starts: under every VISIBLE row
    ///     above it in <see cref="EventRowOrder" />. A hidden row keeps the Y it
    ///     would have, so a caller never reads an unset zero.</summary>
    private static float RowTopFor(RibbonLanes lanes, float pitch, RibbonLanes lane)
    {
        var y = TopPadding;
        foreach (var row in EventRowOrder)
        {
            if (row == lane) return y;
            if ((lanes & row) != 0) y += pitch;
        }

        return y;
    }

    /// <summary>Every lane this layout gives height to.</summary>
    public RibbonLanes Lanes { get; }

    public RibbonDensity Density { get; }

    public bool ShowTones => (Lanes & RibbonLanes.Tones) != 0;
    public bool ShowSections => (Lanes & RibbonLanes.Sections) != 0;
    public bool ShowPhrases => (Lanes & RibbonLanes.Phrases) != 0;
    public bool ShowLyrics => (Lanes & RibbonLanes.Lyrics) != 0;
    public bool ShowMeasureMetadata => (Lanes & RibbonLanes.MeasureMetadata) != 0;
    public bool ShowLinks => (Lanes & RibbonLanes.Links) != 0;

    /// <summary>This layout's row pitch: <see cref="RowHeight" />, or <see cref="CompactRowHeight" />.</summary>
    public float RowPitch { get; }

    /// <summary>This layout's chip height: <see cref="BarHeight" />, or <see cref="CompactBarHeight" />.</summary>
    public float Bar { get; }

    /// <summary>This layout's measures band: <see cref="MetadataBandHeight" />, or the compact one.</summary>
    public float Band { get; }

    /// <summary>This layout's measure pill: <see cref="PillHeight" />, or the compact one.</summary>
    public float Pill { get; }

    public float ToneY { get; }
    public float SectionY { get; }
    public float PhraseY { get; }
    public float LyricY { get; }
    public float LinkY { get; }
    public float MetadataY { get; }
    public float TotalHeight { get; }

    /// <summary>Top Y of the chip inside the row at <paramref name="rowY" />, in THIS layout's density.</summary>
    public float BarTop(float rowY) => rowY + (RowPitch - Bar) * 0.5f;

    /// <summary>Bottom Y of that chip: where a drop-line starts.</summary>
    public float BarBottom(float rowY) => BarTop(rowY) + Bar;

    /// <summary>Top Y of the measure pill inside the band at <paramref name="bandY" />, in THIS layout's density.</summary>
    public float PillTop(float bandY) => bandY + (Band - Pill) * 0.5f;

    public static readonly RibbonLayout Default = new(RibbonLanes.All);

    /// <summary>
    /// Returns the row whose Y-band contains <paramref name="y"/>, or
    /// <see cref="EventRow.None"/> if the click is below the ribbon
    /// (i.e. inside the lane / metadata area).
    /// </summary>
    public EventRow HitTestRow(double y)
    {
        if (ShowTones && y >= ToneY && y < ToneY + RowPitch) return EventRow.Tone;
        if (ShowSections && y >= SectionY && y < SectionY + RowPitch) return EventRow.Section;
        if (ShowPhrases && y >= PhraseY && y < PhraseY + RowPitch) return EventRow.Phrase;
        if (ShowLyrics && y >= LyricY && y < LyricY + RowPitch) return EventRow.Lyric;
        if (ShowLinks && y >= LinkY && y < LinkY + RowPitch) return EventRow.Link;
        return EventRow.None;
    }
}

public enum EventRow { None, Tone, Section, Phrase, Lyric, Link }

