using Avalonia.Media;
using DotCharter.UI.Controls.Layers.Helpers;

namespace DotCharter.Tests.UI.Controls;

/// <summary>
///     Section colours. The timeline's SECTIONS chips are the 3D view's section purple — the owner
///     asked for it (2026-09-24) after a night of per-name colours that matched the song map but not
///     the 3D view beside it. The song map keeps its per-name band.
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
    public void TheSongMap_StillColoursSectionsByName()
    {
        Assert.Equal(SectionColors.IndexFor("chorus"), SectionColors.IndexFor("CHORUS"));
        string[] names = ["intro", "verse", "chorus", "bridge", "solo", "outro", "prechorus", "breakdown"];
        Assert.True(names.Select(SectionColors.IndexFor).Distinct().Count() >= 3,
            "the usual section names collapse onto fewer than three colours");
    }

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
    public void TheSongMapAndTheTimeline_ColourSectionsThroughTheHelper()
    {
        var controls = UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Controls"));
        var map = UiSourceTree.ReadAllText(Path.Combine(controls, "MinimapOverviewControl.cs"));
        var events = UiSourceTree.ReadAllText(Path.Combine(controls, "Layers", "EventLayer.cs"));
        var hud = UiSourceTree.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Rendering", "Components", "HUDComponent.cs"));

        Assert.Contains("SectionBandPalette[SectionColors.IndexFor(name)]", map);
        Assert.Contains("SectionColors.ChipFill(", events);
        Assert.Contains("SectionColors.DropLine()", events);
        Assert.Contains("HudEventBadges.SectionColor", hud);
        Assert.DoesNotContain("_sectionFill", events);
    }

    private static double Contrast(Color a, Color b)
    {
        static double L(Color c)
        {
            static double Ch(byte v)
            {
                var s = v / 255.0;
                return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }

            return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
        }

        var (hi, lo) = (Math.Max(L(a), L(b)), Math.Min(L(a), L(b)));
        return (hi + 0.05) / (lo + 0.05);
    }
}
