namespace DotCharter.UI.Controls.Layers.Helpers;

/// <summary>
///     Where a bend's step label ("↗Full", "↗½") goes on the 2D timeline. Pure, so the two
///     rules are testable without a GPU batcher:
///     <list type="bullet">
///         <item>A label never covers its own note head. The first bend point usually sits AT the
///         head, and labels draw after the heads, so a label placed at its point hid the fret
///         number ("14 ↗Full" would read as "1 ↗Full").</item>
///         <item>Two labels of one note never overlap. When they would, the bigger step stays: the
///         peak is what a charter reads the bend by, and the smaller step is still drawn as the curve.</item>
///     </list>
/// </summary>
public static class BendLabelPlacement
{
    /// <summary>
    ///     The bend curve's ink: the string's colour lifted most of the way to white. Pure white was
    ///     the brightest mark in the lane after the fret numbers, and under a slight bend ("↗0.3") the
    ///     curve lies almost flat along the tail, so it would read as a selection underline.
    ///     The tint keeps it light enough to read on the tail and ties it to its string.
    /// </summary>
    public static Avalonia.Media.Color CurveColor(Avalonia.Media.Color stringColour, byte alpha) =>
        Avalonia.Media.Color.FromArgb(alpha, Lift(stringColour.R), Lift(stringColour.G), Lift(stringColour.B));

    private static byte Lift(byte channel) => (byte)(channel + (255 - channel) * 0.6f);

    /// <summary>Clear space between the head's rim and the first label.</summary>
    public const float HeadGap = 2f;

    /// <summary>Clear space two labels of one note keep between them.</summary>
    public const float LabelGap = 2f;

    /// <summary>
    ///     The label's left edge: at its bend point, never left of the head's right rim, and pulled
    ///     back inside a sustained note when it would run past the note's end
    ///     — unless pulling back would cover the head,
    ///     which loses the fret number and is the worse of the two.
    /// </summary>
    public static float LabelX(float bendX, float headRightX, float sustainEndX, bool hasSustain, float boxWidth)
    {
        var floor = headRightX + HeadGap;
        var labelX = Math.Max(bendX, floor);
        if (hasSustain && labelX + boxWidth > sustainEndX) labelX = Math.Max(floor, sustainEndX - boxWidth);
        return labelX;
    }

    /// <summary>
    ///     Whether a label fits before the next head on its string (from <see cref="NextHeadScan" />).
    ///     Past the head floor it cannot move left, so one that does not fit is not drawn: at a zoom
    ///     where notes crowd, "↗0.3" would cover the next note's fret number. The
    ///     bend curve still shows the bend.
    /// </summary>
    public static bool FitsBeforeNextHead(float labelX, float boxWidth, float nextHeadX, float headRadius) =>
        labelX + boxWidth <= nextHeadX - headRadius - LabelGap;

    public enum Collision
    {
        /// <summary>No earlier label of this note is in the way.</summary>
        None,

        /// <summary>This label is the bigger step: it takes the earlier label's place.</summary>
        ReplacePrevious,

        /// <summary>The earlier label is the bigger step: this one is not drawn.</summary>
        SkipThis
    }

    /// <summary>
    ///     Whether a label starting at <paramref name="labelX" /> fits after the previous label of the
    ///     same note (which ends at <paramref name="previousRight" />; <see cref="float.NegativeInfinity" />
    ///     when there is none).
    /// </summary>
    public static Collision Resolve(float previousRight, float previousStep, float labelX, float step)
    {
        if (labelX >= previousRight + LabelGap) return Collision.None;
        return step >= previousStep ? Collision.ReplacePrevious : Collision.SkipThis;
    }

    /// <summary>
    ///     <see cref="Resolve" /> against EVERY label of the note still standing, not only the last
    ///     one. A later, wider label pulled left by the sustain clamp can reach past the label it
    ///     replaces into the one before, and checking only the last would let it land on that one.
    ///     The standing labels never overlap each other and run left to
    ///     right, so the ones this label collides with are a TAIL of <paramref name="rights" />.
    ///     <para>
    ///         Returns <see cref="Collision.None" /> (<paramref name="keep" /> = all),
    ///         <see cref="Collision.ReplacePrevious" /> when this is at least the biggest step it
    ///         collides with (<paramref name="keep" /> = how many standing labels survive; drop the
    ///         rest and append this), or <see cref="Collision.SkipThis" />.
    ///     </para>
    /// </summary>
    public static Collision ResolveAgainst(ReadOnlySpan<float> rights, ReadOnlySpan<float> steps, float labelX,
        float step, out int keep)
    {
        keep = rights.Length;
        var biggest = float.NegativeInfinity;
        while (keep > 0 && labelX < rights[keep - 1] + LabelGap)
        {
            keep--;
            biggest = Math.Max(biggest, steps[keep]);
        }

        if (keep == rights.Length) return Collision.None;
        if (step >= biggest) return Collision.ReplacePrevious;
        keep = rights.Length;
        return Collision.SkipThis;
    }
}
