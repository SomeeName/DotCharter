using Avalonia.Media;
using DotCharter.Services.Infrastructure.Appearance;
using DotCharter.UI.Converters;
using DotCharter.UI.Theming;

namespace DotCharter.UI.Platform;

/// <summary>
///     <see cref="IAppearanceTheme" /> over the one theme: <see cref="IThemeService" />
///     applies, <see cref="ThemePresets" /> names the tokens, <see cref="StringColorPresets" />
///     holds the palettes and <see cref="ThemeOverrides" /> answers what the user changed.
///     Colours cross the seam as hex strings; <c>Color</c> never leaves this file.
/// </summary>
public sealed class AppearanceTheme(IThemeService theme) : IAppearanceTheme
{
    public IReadOnlyList<ThemeTokenEntry> Tokens { get; } =
        [.. ThemePresets.CustomizableTokens.Select(t => new ThemeTokenEntry(t.Key, t.DisplayName, t.Category))];

    public IReadOnlyList<StringPaletteEntry> StringPalettes { get; } =
        [.. StringColorPresets.All.Select(p => new StringPaletteEntry(p.Id, p.DisplayName))];

    public string DefaultStringPaletteId => StringColorPresets.DefaultId;

    public string GetHex(string tokenKey) => HexColorConverter.ToHex(theme.GetColor(tokenKey));

    public void SetHex(string tokenKey, string hex)
    {
        if (Color.TryParse(hex, out var color)) theme.SetColor(tokenKey, color);
    }

    /// <summary>Keeps the customized colours: <see cref="ThemeOverrides.ForStringColorPreset" />, never the reset path.</summary>
    public void ApplyStringPalette(string paletteId) =>
        theme.ApplyPreset(theme.CurrentPreset,
            ThemeOverrides.ForStringColorPreset(theme.CurrentColors, theme.CurrentPreset.Colors, paletteId));

    public void ResetColors(string paletteId)
    {
        var overrides = StringColorPresets.GetOverrides(paletteId);
        theme.ApplyPreset(ThemePresets.Default, overrides.Count > 0 ? overrides : null);
    }

    public void ApplyPersisted(IReadOnlyDictionary<string, string> freeFormOverrides, string paletteId)
    {
        var overrides = new Dictionary<string, string>(freeFormOverrides);
        foreach (var kv in StringColorPresets.GetOverrides(paletteId)) overrides[kv.Key] = kv.Value;
        theme.ApplyPreset(ThemePresets.Default, overrides);
    }

    public IReadOnlyDictionary<string, string> FreeFormOverrides(bool excludeStringTokens) =>
        ThemeOverrides.FreeForm(theme.CurrentColors, theme.CurrentPreset.Colors, excludeStringTokens);
}
