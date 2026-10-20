namespace DotCharter.UI.Controls.Layers.Helpers;

/// <summary>
///     Whether a slide's destination-fret box is drawn on the 2D timeline. The box sits where the
///     slide ends; when the next note on that string starts right there on the same fret, its head
///     already says the fret, and the box — drawn after the heads — only covered it. In a fast run
///     (double-stops sliding 10/8 → 11/9 → 10/8 on the B and high E strings) the
///     boxes would stack over the heads and no fret would be readable. A slide whose next note is a DIFFERENT
///     fret keeps its box: then the box says something the head does not. The next head comes from
///     <see cref="NextHeadScan" />.
/// </summary>
public static class SlideLabelPolicy
{
    /// <summary>How close past the slide's end the next head has to start to sit under the box
    /// (the box and a head are each about this wide).</summary>
    public const float ClearancePx = 24f;

    /// <summary>True when the next head on the string repeats the slide's target where its box would go.</summary>
    public static bool NextHeadRepeatsTarget(float slideEndX, float nextHeadX, byte slideTo, byte nextFret) =>
        nextFret == slideTo && nextHeadX >= slideEndX - 1f && nextHeadX - slideEndX <= ClearancePx;
}
