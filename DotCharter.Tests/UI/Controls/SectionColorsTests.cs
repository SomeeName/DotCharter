using Avalonia.Media;
using DotCharter.UI.Controls.Layers.Helpers;
using DotCharter.UI.Theming;

namespace DotCharter.Tests.UI.Controls;

/// <summary>
///     Section colours on the song map and the timeline.
/// </summary>
public sealed class SectionColorsTests
{
    [Fact]
    public void TheTimelinesSectionChip_IsThe3DViewsSectionPurple()
    {
        var chip = SectionColors.ChipFill(emphasised: false);
        Assert.Equal(DotCharter.Rendering.Data.HudEventBadges.SectionColor, chip);
        Assert.Equal(Color.FromRgb(165, 54, 178), Color.FromRgb(chip.R, chip.G, chip.B));
    }

    [Fact]
    public void EveryRocksmithSectionHasItsOwnColour()
    {
        Assert.Equal(KnownNames.Length, KnownNames.Select(MapColour).Distinct().Count());
        Assert.Equal(MapColour("chorus"), MapColour("CHORUS"));
    }

    [Fact]
    public void EveryPairOfRocksmithSectionColoursIsPerceptuallyDistinct()
    {
        const double minimumDistance = 0.03;
        var names = RsSectionCatalog.Names;
        var colours = names.Select(name => ToOklab(MapColour(name))).ToArray();
        for (var i = 0; i < names.Count; i++)
        {
            for (var j = i + 1; j < names.Count; j++)
            {
                var (l1, a1, b1) = colours[i];
                var (l2, a2, b2) = colours[j];
                var distance = Math.Sqrt(Math.Pow(l1 - l2, 2) + Math.Pow(a1 - a2, 2) + Math.Pow(b1 - b2, 2));
                Assert.True(distance >= minimumDistance,
                    $"{names[i]} and {names[j]} are {distance:F4} apart in OKLab (minimum {minimumDistance:F2})");
            }
        }
    }

    [Fact]
    public void TheChorusFamilySharesAHueWithDistinctShades()
    {
        var family = ChorusFamily();
        Assert.Equal(family.Length, family.Distinct().Count());
        var chorusHue = family[1].ToHsv().H;
        foreach (var colour in family)
        {
            var degrees = Math.Abs(chorusHue - colour.ToHsv().H);
            Assert.InRange(Math.Min(degrees, 360 - degrees), 0, 12);
        }
    }

    [Fact]
    public void UnknownNamesUseALargerPaletteWithoutTakingAFamilyColour()
    {
        var fallback = UnknownColours();
        Assert.Empty(fallback.Intersect(KnownNames.Select(MapColour)));
        Assert.True(fallback.Length > 5);
        Assert.Equal(MapColour("custom riff"), MapColour("CUSTOM RIFF"));
    }

    [Fact]
    public void ACustomNameKeepsItsAssignedColour() =>
        Assert.Equal(Color.Parse("#4C8D7B"), MapColour("lead turn"));

    [Fact]
    public void SongMapFillsKeepLabelsReadableAndStandOutOnBothThemes()
    {
        var darkSurface = ThemePresets.Default.Colors[ThemeTokens.FireSurfaceChrome];
        foreach (var colour in KnownNames.Select(MapColour).Concat(UnknownColours()))
        {
            Assert.True(Contrast(SongMapPalette.LabelBrush.Color, colour) >= 4.5, $"label on {colour}");
            Assert.True(Contrast(darkSurface, colour) >= 3, $"dark surface against {colour}");
            Assert.True(Contrast(Colors.White, colour) >= 3, $"light surface against {colour}");
        }
    }

    private static readonly string[] KnownNames = [.. RsSectionCatalog.Names, "count", "end"];

    private static Color MapColour(string name) => SongMapPalette.FillFor(name).Color;

    private static Color[] ChorusFamily() => [MapColour("prechorus"), MapColour("chorus"), MapColour("postchorus")];

    private static Color[] UnknownColours() => Enumerable.Range(0, 256)
        .Select(i => MapColour($"custom phrase {i}")).Distinct().ToArray();

    /// <summary>The chips' light text must stay readable on the purple, normal and emphasised
    /// (WCAG AA, 4.5:1).</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheChipText_StaysReadable(bool emphasised)
    {
        var text = Color.FromRgb(240, 244, 250);
        var ratio = Contrast(text, SectionColors.ChipFill(emphasised));
        Assert.True(ratio >= 4.5, $"text contrast {ratio:0.00}");
    }

    [Fact]
    public void TheTimeline_ColoursSectionChipsThroughTheHelper()
    {
        var controls = UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Controls"));
        var events = UiSourceTree.ReadAllText(Path.Combine(controls, "Layers", "EventLayer.cs"));
        var hud = UiSourceTree.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Rendering", "Components", "HUDComponent.cs"));

        Assert.Contains("SectionColors.ChipFill(", events);
        Assert.Contains("SectionColors.DropLine()", events);
        Assert.Contains("HudEventBadges.SectionColor", hud);
        Assert.DoesNotContain("_sectionFill", events);
    }

    private static double Contrast(Color a, Color b)
    {
        static double L(Color c) =>
            0.2126 * LinearChannel(c.R) + 0.7152 * LinearChannel(c.G) + 0.0722 * LinearChannel(c.B);

        var (hi, lo) = (Math.Max(L(a), L(b)), Math.Min(L(a), L(b)));
        return (hi + 0.05) / (lo + 0.05);
    }

    private static double LinearChannel(byte value)
    {
        var s = value / 255.0;
        return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }

    // Linear sRGB to OKLab (https://bottosson.github.io/posts/oklab/).
    private static (double L, double A, double B) ToOklab(Color color)
    {
        var r = LinearChannel(color.R);
        var g = LinearChannel(color.G);
        var b = LinearChannel(color.B);
        var l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
        var m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
        var s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
        return (
            0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
            1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
            0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
    }
}
