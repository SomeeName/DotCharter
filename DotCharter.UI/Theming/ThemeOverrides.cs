using Avalonia.Media;

namespace DotCharter.UI.Theming;

/// <summary>
///     The one place that answers "which colours has the user changed away from
///     the preset?" — the free-form palette overrides that
///     <c>AppConfig.ThemeColorOverrides</c> persists and that
///     <c>ThemeService.ApplyPreset</c> layers back on.
///     <para>
///         It exists because the answer was computed in TWO places that
///         disagreed. <c>SettingsViewModel.SaveAppearance</c> diffed the live
///         colours against the preset (correct), while
///         <c>ApplySelectedTheme</c> — the method the Appearance tab's
///         colour-blind string-preset ComboBox ran on every change — re-applied
///         the preset with ONLY the seven <c>String*</c> overrides. That is a
///         full reset: every colour the user had picked in the "Customize"
///         grid went back to the preset's value, and the Save that followed
///         diffed the now-identical colours and persisted an EMPTY override
///         set, so the customization was gone for good. Play Mode's twin of the
///         same row (<c>PlayModeSettingsViewModel.OnSelectedStringColorPresetChanged</c>)
///         had always merged the persisted overrides first, which is why one of
///         the two surfaces preserved the palette and the other destroyed it.
///     </para>
/// </summary>
public static class ThemeOverrides
{
    /// <summary>Token → <c>"#AARRGGBB"</c> for every colour that currently
    /// differs from its preset baseline.</summary>
    /// <param name="current">The live colours (<c>IThemeService.CurrentColors</c>).</param>
    /// <param name="presetColors">The active preset's own table.</param>
    /// <param name="excludeStringTokens">
    ///     Drop the seven <c>String*</c> tokens. They are governed by the
    ///     colour-blind string-colour preset rather than by the customize grid,
    ///     so baking the currently-applied ones into the free-form set would
    ///     freeze them: switching back to "Default (theme)" could then never
    ///     restore the theme's own string colours.
    /// </param>
    public static Dictionary<string, string> FreeForm(
        IReadOnlyDictionary<string, Color> current,
        IReadOnlyDictionary<string, Color> presetColors,
        bool excludeStringTokens)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(presetColors);

        var overrides = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in current)
        {
            if (excludeStringTokens && StringColorPresets.IsStringToken(kv.Key)) continue;
            if (!presetColors.TryGetValue(kv.Key, out var baseline) || baseline != kv.Value)
                overrides[kv.Key] = ToHex(kv.Value);
        }

        return overrides;
    }

    /// <summary>
    ///     The override set to hand <c>ThemeService.ApplyPreset</c> when the user
    ///     picks a colour-blind string-colour preset: the customize grid's own
    ///     colours, KEPT, with the preset's seven <c>String*</c> colours layered
    ///     on top. An empty string preset ("Default (theme)") contributes
    ///     nothing, so the String* tokens fall back to the theme's own values.
    /// </summary>
    public static Dictionary<string, string> ForStringColorPreset(
        IReadOnlyDictionary<string, Color> current,
        IReadOnlyDictionary<string, Color> presetColors,
        string? stringColorPresetId)
    {
        var overrides = FreeForm(current, presetColors, excludeStringTokens: true);
        foreach (var kv in StringColorPresets.GetOverrides(stringColorPresetId))
            overrides[kv.Key] = kv.Value;
        return overrides;
    }

    /// <summary>The hex spelling the config file and
    /// <c>ThemeService.ApplyPreset</c> both use.</summary>
    public static string ToHex(Color color) => "#" + color.ToString().TrimStart('#');
}
