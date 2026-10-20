using DotCharter.UI.Constants;

namespace DotCharter.UI.Controls.Layers.Helpers;

/// <summary>How one lyric syllable's pill is drawn: its width, where its text starts, and whether
/// the text is drawn at all.</summary>
public readonly record struct LyricPill(float Width, float TextX, bool ShowText);

/// <summary>
///     Lays out one syllable pill in the 2D timeline's lyrics lane. Pure, so it is tested without a
///     GPU batcher.
///     <para>
///         A pill is as long as its syllable, at least <see cref="LyricPillMetrics.MinPillWidthPx" />,
///         and wide enough for its text — but that alone would not stop it at the NEXT syllable.
///         Short syllables sung quickly would overlap, and the next pill, drawn later, would cover the text of
///         the one before ("shi ma u" would read "s ma u"). So a pill ends
///         <see cref="GapPx" /> before the next syllable starts; a narrow one tightens its padding and
///         centres its text; one too narrow even for that leaves its text out rather than printing it
///         under its neighbour — zooming in brings it back.
///     </para>
/// </summary>
public static class LyricPillLayout
{
    /// <summary>Clear space between one pill and the next syllable's.</summary>
    public const float GapPx = 2f;

    /// <summary>Text inset in a pill with room to spare.</summary>
    public const float NormalPadPx = 9f;

    /// <summary>The tightest inset a pill's text is drawn with.</summary>
    public const float TightPadPx = 2f;

    /// <summary>Narrowest pill drawn at all, however crowded — it still marks where a syllable is.</summary>
    public const float MinDrawnWidthPx = 4f;

    /// <param name="startX">Where the syllable starts on screen.</param>
    /// <param name="endX">Where it ends.</param>
    /// <param name="nextStartX">Where the next syllable starts (<see cref="float.PositiveInfinity" /> for the last).</param>
    /// <param name="textWidth">The syllable's text width.</param>
    public static LyricPill Layout(float startX, float endX, float nextStartX, float textWidth)
    {
        var natural = Math.Max(Math.Max(endX - startX, LyricPillMetrics.MinPillWidthPx), textWidth + NormalPadPx * 2f);
        var room = nextStartX - GapPx - startX;
        var width = Math.Max(MinDrawnWidthPx, Math.Min(natural, room));

        if (textWidth + NormalPadPx * 2f <= width)
            return new LyricPill(width, StickyLabel.TextX(startX, width, textWidth, NormalPadPx), true);

        if (textWidth + TightPadPx * 2f <= width)
        {
            var pad = (width - textWidth) / 2f;
            return new LyricPill(width, StickyLabel.TextX(startX, width, textWidth, pad), true);
        }

        return new LyricPill(width, startX, false);
    }

    /// <summary>How far a vocal phrase's region reaches outside its first and last syllables.</summary>
    public const float RegionPadPx = 4f;

    /// <summary>
    ///     The limit a phrase's LAST pill is laid out against when another phrase follows: not that
    ///     phrase's first syllable but its region's edge, less this region's own pad and a gap — so a
    ///     back-to-back pair keeps each last pill inside its own region.
    /// </summary>
    public static float LastPillLimit(float nextPhraseFirstX) => nextPhraseFirstX - RegionPadPx * 2f - GapPx;

    /// <summary>
    ///     The screen span of a vocal phrase's region — the rounded band drawn behind its syllables
    ///     (not "[" / "]" bracket ticks, whose closing tick sits at the phrase's end
    ///     TIME and so lands inside or short of a last pill widened to fit its text). It runs from
    ///     <see cref="RegionPadPx" /> before the first syllable to <see cref="RegionPadPx" /> past
    ///     whichever ends later, the phrase or its last pill, and stops <see cref="GapPx" /> short of
    ///     the next phrase's region.
    ///     <para>
    ///         That stop WINS over the region's minimum width (2 × <see cref="RegionPadPx" />): two
    ///         phrases closer than that on screen get narrow regions, down to an empty one
    ///         (<c>End == Start</c>, which the layer skips), rather than one band running into the
    ///         next — merged bands read as one phrase, which is the one thing a region must not say.
    ///         If the minimum won, neighbouring bands would meet or cross at low zoom.
    ///     </para>
    /// </summary>
    public static (float Start, float End) PhraseRegion(float firstSyllableX, float phraseEndX, float lastPillRight,
        float nextPhraseFirstX)
    {
        var start = firstSyllableX - RegionPadPx;
        var cap = nextPhraseFirstX - RegionPadPx - GapPx;
        var end = Math.Min(Math.Max(phraseEndX, lastPillRight) + RegionPadPx, cap);
        end = Math.Max(end, Math.Min(start + RegionPadPx * 2f, cap));
        return (start, Math.Max(end, start));
    }
}
