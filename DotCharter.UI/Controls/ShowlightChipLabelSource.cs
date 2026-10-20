using DotCharter.Rendering.Data;

namespace DotCharter.UI.Controls;

/// <summary>
///     Builds the localized <see cref="ShowlightChipLabels" /> the editor 3D
///     preview's show-light chip prints. Names come from
///     <see cref="ShowlightCatalog" /> (a plus beam is its colour plus "+"); each
///     colour has one resx key, listed here so a grep finds every use.
/// </summary>
public sealed class ShowlightChipLabelSource
{
    /// <summary>Catalogue colour name (without the plus) → its resx key.</summary>
    public static readonly IReadOnlyDictionary<string, string> ColourKeys = new Dictionary<string, string>
    {
        ["Green"] = "Preview3D_ShowlightColor_Green",
        ["Pink"] = "Preview3D_ShowlightColor_Pink",
        ["Teal"] = "Preview3D_ShowlightColor_Teal",
        ["Orange"] = "Preview3D_ShowlightColor_Orange",
        ["Blue"] = "Preview3D_ShowlightColor_Blue",
        ["Lime"] = "Preview3D_ShowlightColor_Lime",
        ["Magenta"] = "Preview3D_ShowlightColor_Magenta",
        ["Light Green"] = "Preview3D_ShowlightColor_LightGreen",
        ["Red"] = "Preview3D_ShowlightColor_Red",
        ["Light Blue"] = "Preview3D_ShowlightColor_LightBlue",
        ["Yellow"] = "Preview3D_ShowlightColor_Yellow",
        ["Purple"] = "Preview3D_ShowlightColor_Purple"
    };

    private readonly ILocalizationService _localization;
    private volatile ShowlightChipLabels? _cached;

    public ShowlightChipLabelSource(ILocalizationService localization)
    {
        _localization = localization;
        // A language switch raises PropertyChanged; the next frame rebuilds the labels.
        localization.PropertyChanged += (_, _) => _cached = null;
    }

    /// <summary>The labels for the current UI language, rebuilt only after it
    /// changes. Called from the render thread once per staged frame.</summary>
    public ShowlightChipLabels Current() => _cached ??= Build(_localization);

    public static ShowlightChipLabels Build(ILocalizationService loc)
    {
        var names = new Dictionary<int, string>();
        foreach (var def in ShowlightCatalog.AllDefinitions)
        {
            if (def.Lane == ShowlightLane.Lasers || def.Note == ShowlightPalette.BeamsOff) continue;
            var plus = def.DisplayName.EndsWith('+');
            var colour = plus ? def.DisplayName[..^1] : def.DisplayName;
            names[def.Note] = loc[ColourKeys[colour]] + (plus ? "+" : "");
        }

        return new ShowlightChipLabels(
            loc["Preview3D_ShowlightChip"],
            loc["Preview3D_ShowlightChip_On"],
            loc["Preview3D_ShowlightChip_Off"],
            loc["Preview3D_ShowlightChip_None"],
            names);
    }
}
