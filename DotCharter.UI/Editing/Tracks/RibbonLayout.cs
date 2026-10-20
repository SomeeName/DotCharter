namespace DotCharter.UI.Editing.Tracks;

/// <summary>
/// Builds the <see cref="RibbonLayout" /> for a track's
/// <see cref="RibbonVisibility" /> toggles (which live in Services with the
/// track context).
/// </summary>
public static class RibbonVisibilityLayout
{
    public static RibbonLayout BuildLayout(this RibbonVisibility visibility) =>
        new(visibility.ShowTones, visibility.ShowSections, visibility.ShowPhrases, visibility.ShowLyrics,
            visibility.ShowMeasureMetadata);
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
    /// <paramref name="rowY" /> — the ONE place a bar is centred in its
    /// lane.</summary>
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

    public RibbonLayout(bool showTones, bool showSections, bool showPhrases, bool showLyrics, bool showMeasureMetadata)
    {
        ShowTones = showTones;
        ShowSections = showSections;
        ShowPhrases = showPhrases;
        ShowLyrics = showLyrics;
        ShowMeasureMetadata = showMeasureMetadata;

        var y = TopPadding;
        ToneY = y; if (showTones) y += RowHeight;
        SectionY = y; if (showSections) y += RowHeight;
        PhraseY = y; if (showPhrases) y += RowHeight;
        LyricY = y; if (showLyrics) y += RowHeight;

        var ribbonBottom = y + RibbonBottomPadding;
        MetadataY = ribbonBottom;
        TotalHeight = ribbonBottom + (showMeasureMetadata ? MetadataBandHeight : 0f) + SeparatorHeight;
    }

    public bool ShowTones { get; }
    public bool ShowSections { get; }
    public bool ShowPhrases { get; }
    public bool ShowLyrics { get; }
    public bool ShowMeasureMetadata { get; }

    public float ToneY { get; }
    public float SectionY { get; }
    public float PhraseY { get; }
    public float LyricY { get; }
    public float MetadataY { get; }
    public float TotalHeight { get; }

    public static readonly RibbonLayout Default = new(true, true, true, true, true);

    /// <summary>
    /// Returns the row whose Y-band contains <paramref name="y"/>, or
    /// <see cref="EventRow.None"/> if the click is below the ribbon
    /// (i.e. inside the lane / metadata area).
    /// </summary>
    public EventRow HitTestRow(double y)
    {
        if (ShowTones && y >= ToneY && y < ToneY + RowHeight) return EventRow.Tone;
        if (ShowSections && y >= SectionY && y < SectionY + RowHeight) return EventRow.Section;
        if (ShowPhrases && y >= PhraseY && y < PhraseY + RowHeight) return EventRow.Phrase;
        if (ShowLyrics && y >= LyricY && y < LyricY + RowHeight) return EventRow.Lyric;
        return EventRow.None;
    }
}

public enum EventRow { None, Tone, Section, Phrase, Lyric }

