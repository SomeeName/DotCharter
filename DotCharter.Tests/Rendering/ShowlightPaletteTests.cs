using Avalonia.Media;
using DotCharter.Rendering.Data;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     The stage lights' colours must tell every showlight apart at a glance: the
///     RSCharter values made green / light green / lime and blue / light blue
///     near-identical. Distances are CIE76 ΔE in L*a*b*.
/// </summary>
public class ShowlightPaletteTests
{
    private const double MinFogDistance = 25;
    private const double MinBeamDistance = 35;
    private const double MinBeamLightness = 50;

    [Fact]
    public void Every_pair_of_fog_colours_is_clearly_different() => AssertSpread(ShowlightCatalog.FogDefinitions.Select(d => d.Note), ShowlightPalette.FogColor, MinFogDistance);

    [Fact]
    public void Every_pair_of_ordinary_beam_colours_is_clearly_different() => AssertSpread(Enumerable.Range(48, 8), ShowlightPalette.BeamColor, MinBeamDistance);

    [Fact]
    public void Every_pair_of_plus_beam_colours_is_clearly_different() => AssertSpread(Enumerable.Range(56, 4), ShowlightPalette.BeamColor, MinBeamDistance);

    [Fact]
    public void Beam_colours_are_bright_enough_to_read_on_the_dark_sky()
    {
        foreach (var note in Enumerable.Range(48, 12))
        {
            var (l, _, _) = Lab(ShowlightPalette.BeamColor(note));
            Assert.True(l >= MinBeamLightness, $"beam {note} L* {l:F0} < {MinBeamLightness}");
        }
    }

    [Fact]
    public void Every_catalogue_note_has_a_colour_and_the_projection_uses_it()
    {
        foreach (var def in ShowlightCatalog.AllDefinitions)
        {
            var c = ShowlightPalette.ColorFromNote(def.Note);
            Assert.Equal(255, c.A);
            switch (def.Lane)
            {
                case ShowlightLane.Fog:
                    Assert.Equal(ShowlightPalette.FogColor(def.Note), c);
                    break;
                case ShowlightLane.Beams when def.Note != ShowlightPalette.BeamsOff:
                    Assert.Equal(ShowlightPalette.BeamColor(def.Note), c);
                    break;
            }
        }
    }

    [Fact]
    public void Exactly_the_four_plus_beams_are_plus()
    {
        var plus = ShowlightCatalog.BeamDefinitions.Where(d => ShowlightPalette.IsBeamPlus(d.Note))
            .Select(d => d.DisplayName).ToArray();
        Assert.Equal(["Red+", "Blue+", "Yellow+", "Purple+"], plus);
    }

    private static void AssertSpread(IEnumerable<int> notes, Func<int, Color> colour, double min)
    {
        var list = notes.ToArray();
        for (var i = 0; i < list.Length; i++)
        {
            for (var j = i + 1; j < list.Length; j++)
            {
                var d = DeltaE(colour(list[i]), colour(list[j]));
                Assert.True(d >= min, $"notes {list[i]} and {list[j]} are only ΔE {d:F1} apart (< {min})");
            }
        }
    }

    private static double DeltaE(Color a, Color b)
    {
        var (l1, a1, b1) = Lab(a);
        var (l2, a2, b2) = Lab(b);
        return Math.Sqrt((l1 - l2) * (l1 - l2) + (a1 - a2) * (a1 - a2) + (b1 - b2) * (b1 - b2));
    }

    private static (double L, double A, double B) Lab(Color c)
    {
        static double Lin(byte v)
        {
            var s = v / 255.0;
            return s > 0.04045 ? Math.Pow((s + 0.055) / 1.055, 2.4) : s / 12.92;
        }

        static double F(double t) => t > 0.008856 ? Math.Cbrt(t) : 7.787 * t + 16.0 / 116;

        double r = Lin(c.R), g = Lin(c.G), b = Lin(c.B);
        var x = (0.4124 * r + 0.3576 * g + 0.1805 * b) / 0.95047;
        var y = 0.2126 * r + 0.7152 * g + 0.0722 * b;
        var z = (0.0193 * r + 0.1192 * g + 0.9505 * b) / 1.08883;
        return (116 * F(y) - 16, 500 * (F(x) - F(y)), 200 * (F(y) - F(z)));
    }
}
