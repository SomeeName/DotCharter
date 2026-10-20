using Avalonia;

namespace DotCharter.Tone.UI.Controls.Fire;

/// <summary>
/// The pure half of <see cref="FireProgress"/>: how a 0..1 ratio becomes either
/// one fill width or N discrete steps.
/// </summary>
public static class FireProgressGeometry
{
    /// <summary>Continuous track height (spec: "3–6px track"). 6 is the
    /// default; <see cref="ThinTrackHeight"/> is the row-inline case.</summary>
    public const double TrackHeight = 6.0;

    /// <inheritdoc cref="TrackHeight"/>
    public const double ThinTrackHeight = 3.0;

    /// <summary>One segment of the stepped variant: 3 px tall, 5 px apart.</summary>
    public const double SegmentHeight = 3.0;

    /// <inheritdoc cref="SegmentHeight"/>
    public const double SegmentGap = 5.0;

    /// <summary>How far past the fill the glow reaches. A DrawingContext has no
    /// blur, so the glow is a wider, fainter pass under the fill.</summary>
    public const double GlowSpread = 6.0;

    /// <summary>What the control asks for when nothing constrains its width. A
    /// progress bar is a stretch-to-fit element; this is only the floor that
    /// keeps it from measuring to nothing in an auto-sized parent.</summary>
    public const double MinimumTrackWidth = 40.0;

    /// <summary>The 0..1 ratio, clamped. NaN reads as empty rather than
    /// propagating into a width.</summary>
    public static double Ratio(double value)
        => double.IsNaN(value) ? 0.0 : Math.Clamp(value, 0.0, 1.0);

    /// <summary>Width of the continuous fill.</summary>
    public static double FillWidth(double value, double trackWidth)
        => Math.Max(0.0, Ratio(value) * Math.Max(0.0, trackWidth));

    /// <summary>
    /// How many of <paramref name="count"/> segments are filled at
    /// <paramref name="value"/>. Rounds to nearest, so "2 of 5 done" bound as
    /// 0.4 fills exactly two — a floor would show one and a ceiling three.
    /// <para>This is also the segmented variant's REPAINT GATE.</para>
    /// </summary>
    public static int FilledSegments(double value, int count)
        => count <= 0
            ? 0
            : Math.Clamp((int)Math.Round(Ratio(value) * count, MidpointRounding.AwayFromZero), 0, count);

    /// <summary>The rect of one segment in a row <paramref name="totalWidth"/>
    /// wide. Segments share the leftover width evenly after the gaps.</summary>
    public static Rect SegmentBounds(
        int index, int count, double totalWidth, double height, double gap)
    {
        if (count <= 0) return default;
        var span = Math.Max(0.0, (totalWidth - (count - 1) * gap) / count);
        return new Rect(index * (span + gap), 0, span, height);
    }
}
