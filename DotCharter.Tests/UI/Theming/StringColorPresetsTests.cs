using Avalonia.Media;
using DotCharter.UI.Theming;

namespace DotCharter.Tests.UI.Theming;

/// <summary>
///     Tests for the colour-blind-safe fretboard string-colour presets
///     (0.13.0). Verifies the default is a no-op, a real preset overrides all
///     seven string tokens, and that applying it through <see cref="ThemeService" />
///     actually changes the resolved String* colours the fretboard reads.
/// </summary>
public sealed class StringColorPresetsTests
{
    private static readonly string[] StringTokens =
    [
        ThemeTokens.String1, ThemeTokens.String2, ThemeTokens.String3,
        ThemeTokens.String4, ThemeTokens.String5, ThemeTokens.String6,
        ThemeTokens.StringExtra
    ];

    [Fact]
    public void Default_HasNoOverrides()
    {
        Assert.Empty(StringColorPresets.GetOverrides(StringColorPresets.DefaultId));
        Assert.Empty(StringColorPresets.GetOverrides(null));
        Assert.Empty(StringColorPresets.GetOverrides("nonexistent-id"));
    }

    [Fact]
    public void OkabeIto_OverridesEveryStringToken()
    {
        var overrides = StringColorPresets.GetOverrides("okabe-ito");
        Assert.Equal(StringTokens.Length, overrides.Count);
        foreach (var token in StringTokens)
            Assert.True(overrides.ContainsKey(token), $"missing override for {token}");
    }

    /// <summary>#595: a palette's caption follows the UI language; its persisted id does not.</summary>
    [Fact]
    public void Captions_FollowTheUiLanguage_AndTheIdsStayStable()
    {
        var english = StringColorPresets.Localized(FakeLocalization.English());
        var german = StringColorPresets.Localized(FakeLocalization.For("de-DE"));

        Assert.Equal(StringColorPresets.All.Select(p => p.Id), german.Select(p => p.Id));
        Assert.Equal("Default (theme)", english[0].DisplayName);
        Assert.Equal("Standard (Design)", german[0].DisplayName);
        Assert.Equal("Okabe–Ito (farbenblind-sicher)", german[1].DisplayName);
        Assert.All(german, p => Assert.NotEqual(p.NameKey, p.DisplayName));
    }

    [Fact]
    public void IsStringToken_TrueOnlyForStringTokens()
    {
        Assert.True(StringColorPresets.IsStringToken(ThemeTokens.String1));
        Assert.True(StringColorPresets.IsStringToken(ThemeTokens.StringExtra));
        Assert.False(StringColorPresets.IsStringToken(ThemeTokens.Accent));
        Assert.False(StringColorPresets.IsStringToken(ThemeTokens.AppBackground));
    }

    [Fact]
    public void ApplyingPresetThroughThemeService_ChangesResolvedStringColors()
    {
        var service = new ThemeService();
        var baseline = service.GetColor(ThemeTokens.String1);

        var overrides = StringColorPresets.GetOverrides("okabe-ito");
        service.ApplyPreset(ThemePresets.Default, overrides);

        var expected = Color.Parse(overrides[ThemeTokens.String1]);
        Assert.Equal(expected, service.GetColor(ThemeTokens.String1));
        Assert.NotEqual(baseline, service.GetColor(ThemeTokens.String1));

        // Non-string tokens still resolve from the preset (not disturbed).
        Assert.Equal(ThemePresets.Default.Colors[ThemeTokens.Accent], service.GetColor(ThemeTokens.Accent));
    }
}
