using Avalonia.Media;

namespace DotCharter.UI.Controls.Layers.Helpers;

/// <summary>
/// How the 2D timeline dims a repeat chord (<c>ChordFlags.Repeat</c>): it draws like the chord
///     it repeats — panel, heads, fret numbers, sustains — at <see cref="Dim" /> of their usual brightness,
///     so a run of strums reads "same chord again". Technique glyphs (mutes, accent, harmonic, HOPO,
///     slap/pop, bends, vibrato/tremolo, slides) and the selection/hover ink keep their full colour, so what
///     differs between two strums of one shape stays readable. The 3D preview keeps the game's frame-only box.
///     <para>
///         Heads, numbers and tubes are DARKENED at their own opacity (<see cref="Darken" />), never made
///         translucent: a translucent head would let a selected panel's white wash through and the fret numbers
///         vanish into it. Only the panel, itself a translucent wash, FADES
///         (<see cref="Fade" />).
///     </para>
/// </summary>
internal static class RepeatChordInk
{
    /// <summary>A repeat chord's head, number, sustain and panel strength, as a fraction of a normal chord's.</summary>
    internal const float Dim = 0.5f;

    /// <summary>The colour at <see cref="Dim" /> brightness, same opacity, when <paramref name="dimmed" />.</summary>
    internal static Color Darken(Color c, bool dimmed) =>
        dimmed ? Color.FromArgb(c.A, (byte)(c.R * Dim), (byte)(c.G * Dim), (byte)(c.B * Dim)) : c;

    /// <summary>The colour at <see cref="Dim" /> opacity when <paramref name="dimmed" /> — for washes only.</summary>
    internal static Color Fade(Color c, bool dimmed) => dimmed ? Color.FromArgb((byte)(c.A * Dim), c.R, c.G, c.B) : c;
}
