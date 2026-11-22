using Avalonia.Media;

namespace DotCharter.UI.Theming;

/// <summary>
///     A named override for the seven chart string-colour tokens
///     (String1..String6 + StringExtra). Applied on top of the active theme so
///     both the 2D timeline and the 3D fretboard (which read the same tokens via
///     <c>ChartTheme</c>) pick it up. The <see cref="StringColorPresets.DefaultId" /> preset carries
///     no overrides — the theme's own string colours are used.
///     <para>
///         <see cref="Id" /> is the stable identifier <c>AppConfig.StringColorPresetId</c> persists;
///         <see cref="NameKey" /> is the resx key of its caption. The catalogue's own entries carry
///         the key as <see cref="DisplayName" />: a picker shows <see cref="Localize" />'d copies.
///     </para>
/// </summary>
public sealed class StringColorPreset
{
    public StringColorPreset(string id, string nameKey, IReadOnlyDictionary<string, Color> colors, string? displayName = null)
    {
        Id = id;
        NameKey = nameKey;
        Colors = colors;
        DisplayName = displayName ?? nameKey;
    }

    public string Id { get; }
    public string NameKey { get; }
    public string DisplayName { get; }
    public IReadOnlyDictionary<string, Color> Colors { get; }

    /// <summary>This preset with its caption in the UI language of <paramref name="localization" />.</summary>
    public StringColorPreset Localize(ILocalizationService localization) => new(Id, NameKey, Colors, localization[NameKey]);
}

/// <summary>
///     Accessibility presets for the fretboard string colours. The built-in
///     palette relies on a red/green pair (low-E vs A/G-strings) that
///     deuteranopia and protanopia (the most common colour-vision deficiencies)
///     struggle to separate; these presets swap in published colour-blind-safe
///     palettes whose seven hues stay mutually distinguishable.
/// </summary>
public static class StringColorPresets
{
    public const string DefaultId = "default";

    private static readonly string[] StringTokens =
    [
        ThemeTokens.String1, ThemeTokens.String2, ThemeTokens.String3,
        ThemeTokens.String4, ThemeTokens.String5, ThemeTokens.String6,
        ThemeTokens.StringExtra
    ];

    private static readonly Dictionary<string, StringColorPreset> ById;

    static StringColorPresets()
    {
        All =
        [
            new StringColorPreset(DefaultId, "StringPalette_Default", new Dictionary<string, Color>()),
            // Okabe & Ito (2008) — the canonical universal colour-blind-safe
            // qualitative palette; distinguishable under deutan/protan/tritan.
            MakePreset("okabe-ito", "StringPalette_OkabeIto",
                "#D55E00", "#F0E442", "#56B4E9", "#E69F00", "#009E73", "#CC79A7", "#999999"),
            // IBM Carbon design colour-blind-safe palette (5 core hues) padded
            // with a teal + neutral grey for the 6th/extra strings.
            MakePreset("ibm", "StringPalette_Ibm",
                "#648FFF", "#FFB000", "#DC267F", "#FE6100", "#785EF0", "#009E73", "#A0A0A0")
        ];
        ById = All.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<StringColorPreset> All { get; }

    /// <summary>Every preset, captioned in the UI language of <paramref name="localization" />, in catalogue order.</summary>
    public static IReadOnlyList<StringColorPreset> Localized(ILocalizationService localization) =>
        [.. All.Select(p => p.Localize(localization))];

    public static StringColorPreset GetById(string? id) => !string.IsNullOrWhiteSpace(id) && ById.TryGetValue(id, out var p) ? p : ById[DefaultId];

    /// <summary>True if <paramref name="tokenKey" /> is one of the seven string
    /// tokens a preset governs (so the settings save can leave those out of the
    /// free-form palette overrides).</summary>
    public static bool IsStringToken(string tokenKey) => Array.IndexOf(StringTokens, tokenKey) >= 0;

    /// <summary>Hex overrides (token → "#AARRGGBB") ready to merge into
    /// <c>ThemeService.ApplyPreset</c>. Empty for the default preset.</summary>
    public static IReadOnlyDictionary<string, string> GetOverrides(string? id)
    {
        var preset = GetById(id);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in preset.Colors)
            map[kv.Key] = "#" + kv.Value.ToString().TrimStart('#');
        return map;
    }

    private static StringColorPreset MakePreset(string id, string nameKey, params string[] hex)
    {
        var colors = new Dictionary<string, Color>(StringComparer.Ordinal);
        for (var i = 0; i < StringTokens.Length && i < hex.Length; i++)
            colors[StringTokens[i]] = Color.Parse(hex[i]);
        return new StringColorPreset(id, nameKey, colors);
    }
}
