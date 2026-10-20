namespace DotCharter.UI.Controls.Layers.Helpers;

/// <summary>
///     The 2D timeline's sticky labels — the section, phrase and tone chips, the measure pill and
///     the FHP fret chip that stay pinned at the left edge while the thing they name scrolls off it.
///     Pure, so the rules are testable without a GPU batcher. Two of them:
///     <list type="bullet">
///         <item><b>Push-off.</b> A pinned label is shoved left by the next label in its row, the way
///         a sticky header is, and leaves when that label reaches the edge. Without it the
///         event chips would stay put and the next chip would slide UNDER them, and the measure pill simply
///         vanish the moment the next pill gets close — at a normal zoom, where a measure is
///         narrower than a full pill, it would be gone more often than it is there.</item>
///         <item><b>Text follows the visible part.</b> A chip that started off-screen left keeps its
///         text on the part still on screen (the lyric row drew a blank grey block there).</item>
///     </list>
/// </summary>
public static class StickyLabel
{
    /// <summary>Clear space a pushed label keeps before the label pushing it.</summary>
    public const float PushGapPx = 4f;

    /// <summary>
    ///     Where a label of <paramref name="width" />, pinned at <paramref name="fixedX" />, sits
    ///     while the next label of its row starts at <paramref name="nextLabelX" />
    ///     (<see cref="float.PositiveInfinity" /> when there is none).
    /// </summary>
    public static float PinnedX(float fixedX, float width, float nextLabelX) =>
        Math.Min(fixedX, nextLabelX - PushGapPx - width);

    /// <summary>Whether any of a pushed label is still on screen.</summary>
    public static bool IsVisible(float x, float width) => x + width > 0f;

    /// <summary>
    ///     Where a chip's text starts: <paramref name="pad" /> inside the chip, or — for a chip that
    ///     began off-screen left — <paramref name="pad" /> inside the visible part, but never so far
    ///     right that the text runs past the chip's end.
    /// </summary>
    public static float TextX(float chipX, float chipWidth, float textWidth, float pad)
    {
        var inside = chipX + pad;
        var visible = Math.Max(inside, pad);
        var lastFit = chipX + chipWidth - pad - textWidth;
        return Math.Max(inside, Math.Min(visible, lastFit));
    }
}
