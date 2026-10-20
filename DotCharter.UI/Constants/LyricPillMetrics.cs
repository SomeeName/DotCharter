namespace DotCharter.UI.Constants;

/// <summary>
///     THE screen extent of one lyric syllable pill in the 2D timeline's lyrics
///     lane — shared by the layer that DRAWS it (<c>EventLayer.DrawLyrics</c>)
///     and by the tester that CLICKS it
///     (<c>EventInteractionHitTester.HitTestLyric</c>).
///     <para>
///         They disagreed. The layer widens a pill to a floor of
///         <see cref="MinPillWidthPx" /> so a short syllable is still a target;
///         the hit test read the syllable's DURATION alone plus an 8 px slack,
///         so at the editing zooms a 60 ms "the" drew an 18 px chip whose right
///         half was not clickable, and zoomed out far enough every syllable in
///         the song was a sliver of hit area under a full-width pill.
///     </para>
///     <para>
///         The floor is the part that can be shared: the layer ALSO widens a
///         pill to fit its text, which needs a font atlas the pure tester has
///         not got. Text wider than the duration therefore still overhangs —
///         but only ever to the right of a pill whose first
///         <see cref="MinPillWidthPx" /> px now hit.
///     </para>
/// </summary>
public static class LyricPillMetrics
{
    /// <summary>Narrowest pill the layer will draw, in screen px.</summary>
    public const float MinPillWidthPx = 18f;

    /// <summary>Duration assumed for a syllable whose end is missing or
    /// degenerate — the same fallback on both sides.</summary>
    public const double DefaultDurationMs = 200.0;
}
