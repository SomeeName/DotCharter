using DotCharter.Localization;
using DotCharter.Rendering.Data;
using DotCharter.Rendering.Engines;
using DotCharter.UI.Controls;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     The editor 3D preview's show-light chip: "Fog: Green · Beams: off ·
///     Lasers: on", localized, shown in the editor and never in Play Mode.
/// </summary>
[Collection("LocalizationSingleton")]
public sealed class ShowlightChipTests
{
    private static ShowlightChipLabels English()
    {
        LocalizationService.Instance.SetConfiguredCulture("en-US");
        return ShowlightChipLabelSource.Build(LocalizationService.Instance);
    }

    [Fact]
    public void The_chip_names_the_fog_the_beams_and_the_lasers()
    {
        var labels = English();
        Assert.Equal("Fog: Green · Beams: off · Lasers: off", labels.Compose(24, null, null));
        Assert.Equal("Fog: Green · Beams: off · Lasers: off", labels.Compose(24, ShowlightPalette.BeamsOff, 67));
        Assert.Equal("Fog: Green · Beams: Pink · Lasers: off", labels.Compose(24, 49, 67));
        Assert.Equal("Fog: Purple · Beams: Yellow+ · Lasers: on", labels.Compose(35, 58, 66));
        Assert.Equal("Fog: Light Blue · Beams: Teal · Lasers: on", labels.Compose(33, 55, 66));
        Assert.Equal("Fog: none · Beams: Red+ · Lasers: off", labels.Compose(null, 56, null));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("ja-JP")]
    public void Every_catalogue_colour_has_a_translation_in_every_language(string culture)
    {
        try
        {
            LocalizationService.Instance.SetConfiguredCulture(culture);
            var labels = ShowlightChipLabelSource.Build(LocalizationService.Instance);
            foreach (var def in ShowlightCatalog.AllDefinitions)
            {
                if (def.Lane == ShowlightLane.Lasers || def.Note == ShowlightPalette.BeamsOff) continue;
                var name = Assert.Contains(def.Note, labels.Names);
                Assert.False(string.IsNullOrWhiteSpace(name));
                Assert.DoesNotContain("Preview3D_", name);
                if (ShowlightPalette.IsBeamPlus(def.Note)) Assert.EndsWith("+", name);
            }

            foreach (var word in new[] { labels.Format, labels.On, labels.Off, labels.None })
                Assert.DoesNotContain("Preview3D_", word);
            Assert.Contains("{0}", labels.Format);
            Assert.Contains("{1}", labels.Format);
            Assert.Contains("{2}", labels.Format);
            Assert.False(string.IsNullOrEmpty(labels.Compose(24, 49, 66)));
        }
        finally
        {
            LocalizationService.Instance.SetConfiguredCulture("en-US");
        }
    }

    [Fact]
    public void German_reads_german()
    {
        try
        {
            LocalizationService.Instance.SetConfiguredCulture("de-DE");
            var labels = ShowlightChipLabelSource.Build(LocalizationService.Instance);
            Assert.Equal("Nebel: Grün · Strahlen: aus · Laser: an", labels.Compose(24, 42, 66));
        }
        finally
        {
            LocalizationService.Instance.SetConfiguredCulture("en-US");
        }
    }

    [Fact]
    public void The_chip_text_is_registered_with_the_glyph_atlas()
    {
        var snapshot = new FrameSnapshot();
        var before = Fretboard3DEngine.ComputeRegisterSignature(snapshot);
        snapshot.ShowlightChipText = "フォグ: 緑 · ビーム: オフ · レーザー: オフ";
        Assert.NotEqual(before, Fretboard3DEngine.ComputeRegisterSignature(snapshot));
    }

    [Fact]
    public void Play_mode_hands_the_renderer_no_chip_labels()
    {
        var src = Preview3DRenderDriverSource.ReadAllText();
        Assert.Contains(
            "_updateContext.ShowlightChipLabels = _gameplayPresentation ? null : _showlightChipLabels?.Current();",
            src);
    }

    [Fact]
    public void The_chip_reserves_the_top_left_corner_and_scales_with_the_frame()
    {
        Assert.True(ShowlightChipLayout.ReservedTop(900) > ShowlightChipLayout.PlateHeight(900));
        Assert.True(ShowlightChipLayout.TextScale(1800) > ShowlightChipLayout.TextScale(900));
        var (x, y) = ShowlightChipLayout.TextOrigin(900);
        Assert.True(x > ShowlightChipLayout.Margin(900) && y >= ShowlightChipLayout.Margin(900));
    }
}
