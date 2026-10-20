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
