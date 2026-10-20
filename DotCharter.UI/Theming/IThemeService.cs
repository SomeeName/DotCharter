using Avalonia.Media;

namespace DotCharter.UI.Theming;

public interface IThemeService
{
    ThemePreset CurrentPreset { get; }
    IReadOnlyDictionary<string, Color> CurrentColors { get; }
    event Action? ThemeChanged;

    void ApplyPreset(ThemePreset preset, IReadOnlyDictionary<string, string>? overridesHex = null);
    void SetColor(string tokenKey, Color color);
    Color GetColor(string tokenKey);

    // There is deliberately NO ResetToPreset() here (deleted 0.60, zero callers).
    // The gesture EXISTS and works — Settings ▸ Appearance ▸ "Reset to preset"
    // (AppearanceSettingsViewModel.ResetColorsCommand) — but it must NOT go
    // through a bare re-apply of CurrentPreset: that would drop the colour-blind-
    // safe string-colour preset, which is layered on top of the theme's String*
    // tokens. It calls Platform/AppearanceTheme.ResetColors, which re-applies the
    // theme WITH those overrides and nothing else; the tab's Save then derives
    // AppConfig.ThemeColorOverrides by diffing live colours against the preset
    // baseline, so the reset persists as an empty override set on Save.

}
