// ReSharper disable InconsistentNaming -- private static lookup field uses _camelCase consistent with project style.
using Avalonia.Media;

namespace DotCharter.UI.Theming;

/// <summary>
///     The app's ONE theme.
///     <para>
///         There were six colour presets and a community-pack mechanism until
///         0.62. The owner's call: "i want to remove all color presets there is
///         no presets one theme for now for easy of coding and that is our new
///         graphite". A single theme is a straight simplification — every
///         surface can be designed against known values instead of against a
///         range, and a token that goes wrong goes wrong once.
///     </para>
///     <para>
///         <b>Graphite is MATERIALISED here, not derived.</b> A derived
///         `DarkSlate().With(…)` would set only 46 of the 86 tokens; the other 40
///         — the whole text ladder, every string colour, every chart colour —
///         were Dark Slate's, so removing it could never be a plain delete.
///         The two dictionaries were merged mechanically from the source, and
///         six values were corrected to the ones
///         the Graphite spec names (the text ladder, and the two extreme
///         borders); everything else carries the value it already had.
///     </para>
///     <para>
///         Per-token USER overrides (`AppConfig.ThemeColorOverrides`) and the
///         colour-blind string presets are untouched: customising one colour is
///         not a preset, and the string presets are an accessibility feature
///         that happens to write theme tokens.
///     </para>
/// </summary>
public static class ThemePresets
{
    /// <summary>The one theme, complete. Every token in <see cref="ThemeTokens.All" />.</summary>
    public static ThemePreset Default { get; } = new("graphite", "Graphite", new Dictionary<string, Color>
    {
        [ThemeTokens.AppBackground] = C("#111317"),
        [ThemeTokens.PanelBackground] = C("#15181D"),
        [ThemeTokens.CardBackground] = C("#181A1E"),
        [ThemeTokens.SurfaceElevated] = C("#232830"),
        [ThemeTokens.SurfaceHover] = C("#191D23"),
        [ThemeTokens.SurfaceHoverStrong] = C("#272D36"),
        [ThemeTokens.SurfaceActive] = C("#323946"),
        [ThemeTokens.SurfaceStrong] = C("#3C4552"),
        [ThemeTokens.ToolbarTop] = C("#181B21"),
        [ThemeTokens.ToolbarBottom] = C("#131519"),
        [ThemeTokens.BorderDefault] = C("#2F3236"),
        [ThemeTokens.BorderSubtle] = C("#24272B"),
        [ThemeTokens.BorderStrong] = C("#3A3E44"),
        [ThemeTokens.BorderDeep] = C("#0A0C10"),
        [ThemeTokens.TextPrimary] = C("#E8EBEF"),
        [ThemeTokens.TextSecondary] = C("#AEB6C1"),
        [ThemeTokens.TextMuted] = C("#929BA8"),
        [ThemeTokens.TextDim] = C("#7A8390"),
        [ThemeTokens.TextOnAccent] = C("#0A1522"),
        [ThemeTokens.Accent] = C("#7AB6FF"),
        [ThemeTokens.AccentHover] = C("#A8CEFF"),
        [ThemeTokens.AccentSubtle] = C("#1E2733"),
        [ThemeTokens.AccentGradientEnd] = C("#5E9FF2"),
        [ThemeTokens.AccentGradientEndHover] = C("#7AB6FF"),
        [ThemeTokens.AccentSelected] = C("#25313F"),
        [ThemeTokens.AccentSelectedHover] = C("#2D3B4C"),
        [ThemeTokens.AccentFaint] = C("#337AB6FF"),
        [ThemeTokens.Success] = C("#10B981"),
        [ThemeTokens.Warning] = C("#F59E0B"),
        [ThemeTokens.Danger] = C("#EF4444"),
        [ThemeTokens.DangerBg] = C("#2A1012"),
        [ThemeTokens.DangerBorder] = C("#4C1518"),
        [ThemeTokens.DangerHoverBg] = C("#3A161A"),
        [ThemeTokens.Info] = C("#60A5FA"),
        [ThemeTokens.WaveformAccent] = C("#2563EB"),
        [ThemeTokens.String1] = C("#FF3333"),
        [ThemeTokens.String2] = C("#FFD700"),
        [ThemeTokens.String3] = C("#3388FF"),
        [ThemeTokens.String4] = C("#FF8C00"),
        [ThemeTokens.String5] = C("#22CC22"),
        [ThemeTokens.String6] = C("#9933FF"),
        [ThemeTokens.StringExtra] = C("#C8C8C8"),
        [ThemeTokens.Phrase] = C("#FBBF24"),
        [ThemeTokens.PhraseBg] = C("#501E293B"),
        [ThemeTokens.Section] = C("#A536B2"),
        [ThemeTokens.PhraseEvent] = C("#B37636"),
        [ThemeTokens.HandShapeBg] = C("#2038BDF8"),
        [ThemeTokens.HandShapeBar] = C("#0284C7"),
        // RsCharter PREVIEW_3D_ARPEGGIO (192,64,255): the 3D bracket's purple.
        [ThemeTokens.HandShapeArpeggio] = C("#C040FF"),
        // a bulk chord edit's preview — no string, selection or warning hue.
        [ThemeTokens.EditTarget] = C("#FF4FA3"),
        [ThemeTokens.AnchorFhp] = C("#3C2590E8"),
        [ThemeTokens.ChartTopMarginBg] = C("#131519"),
        [ThemeTokens.ChartBottomMarginBg] = C("#0D0F13"),
        [ThemeTokens.ChartBorder] = C("#24272B"),
        [ThemeTokens.ChartMinimapBorder] = C("#929BA8"),
        [ThemeTokens.ChartMeasure] = C("#929BA8"),
        [ThemeTokens.ChartHover] = C("#3CFFFFFF"),
        [ThemeTokens.ChartCursor] = C("#A0FFFFFF"),
        [ThemeTokens.ChartPlayhead] = C("#E11D48"),
        [ThemeTokens.ChartMuted] = C("#3F3F46"),
        [ThemeTokens.ChartWaveformInactive] = C("#333B82F6"),
        [ThemeTokens.ChartWaveformPrimary] = C("#5B46BC"),
        [ThemeTokens.ChartWaveformSecondary] = C("#463888"),
        [ThemeTokens.FireGround] = C("#0D0F13"),
        [ThemeTokens.FireGroundScreen] = C("#111317"),
        [ThemeTokens.FireInk] = C("#0A0C10"),
        [ThemeTokens.FireSurfaceChrome] = C("#15181D"),
        [ThemeTokens.FireSurfaceChromeAlt] = C("#171A20"),
        [ThemeTokens.FireSurfaceMenu] = C("#FF1B1F25"),
        [ThemeTokens.FireTextBright] = C("#FFFFFF"),
        [ThemeTokens.FireTextPrimary] = C("#E8EBEF"),
        [ThemeTokens.FireTextBody] = C("#D3D8DF"),
        [ThemeTokens.FireTextSecondary] = C("#AEB6C1"),
        [ThemeTokens.FireTextMuted] = C("#929BA8"),
        [ThemeTokens.FireTextDim] = C("#7A8390"),
        [ThemeTokens.FireTextFaint] = C("#687080"),
        [ThemeTokens.FireTextOnAccent] = C("#0A1522"),
        [ThemeTokens.FireTextOnAccentInk] = C("#0A1522"),
        [ThemeTokens.FireLink] = C("#7AB6FF"),
        [ThemeTokens.FireAccent] = C("#7AB6FF"),
        [ThemeTokens.FireAccentStrong] = C("#5E9FF2"),
        [ThemeTokens.FireAccentDeep] = C("#3C7BD1"),
        [ThemeTokens.FireAccentBright] = C("#A8CEFF"),
        [ThemeTokens.FireAccentPale] = C("#CFE2FF"),
        [ThemeTokens.FireAccentPaper] = C("#E8EEF6"),
        [ThemeTokens.FireSuccess] = C("#5BD08F"),
        [ThemeTokens.FireWarning] = C("#F2B557"),
        [ThemeTokens.FireDanger] = C("#F07A7A")
    });

    /// <summary>
    ///     Kept as a one-element list so the callers that enumerate "the
    ///     themes" still compile and still say something true. It is not a
    ///     picker feed any more — Settings has no theme picker.
    /// </summary>
    public static IReadOnlyList<ThemePreset> All { get; } = new[] { Default };

    /// <summary>
    ///     The tokens the Appearance tab lets a user recolour, in display order.
    ///     <see cref="ThemeTokenInfo.DisplayName" /> and
    ///     <see cref="ThemeTokenInfo.Category" /> are localization keys; the
    ///     category is the group heading the tab draws them under.
    /// </summary>
    public static IReadOnlyList<ThemeTokenInfo> CustomizableTokens { get; } = new[]
    {
        new ThemeTokenInfo(ThemeTokens.AppBackground, "ThemeToken_AppBackground", "ThemeTokenGroup_Surface"),
        new ThemeTokenInfo(ThemeTokens.PanelBackground, "ThemeToken_PanelBackground", "ThemeTokenGroup_Surface"),
        new ThemeTokenInfo(ThemeTokens.CardBackground, "ThemeToken_CardBackground", "ThemeTokenGroup_Surface"),
        new ThemeTokenInfo(ThemeTokens.SurfaceElevated, "ThemeToken_SurfaceElevated", "ThemeTokenGroup_Surface"),
        new ThemeTokenInfo(ThemeTokens.SurfaceHover, "ThemeToken_SurfaceHover", "ThemeTokenGroup_Surface"),
        new ThemeTokenInfo(ThemeTokens.ToolbarTop, "ThemeToken_ToolbarTop", "ThemeTokenGroup_Surface"),
        new ThemeTokenInfo(ThemeTokens.ToolbarBottom, "ThemeToken_ToolbarBottom", "ThemeTokenGroup_Surface"),
        new ThemeTokenInfo(ThemeTokens.BorderDefault, "ThemeToken_BorderDefault", "ThemeTokenGroup_Surface"),
        new ThemeTokenInfo(ThemeTokens.BorderSubtle, "ThemeToken_BorderSubtle", "ThemeTokenGroup_Surface"),
        new ThemeTokenInfo(ThemeTokens.TextPrimary, "ThemeToken_TextPrimary", "ThemeTokenGroup_Text"),
        new ThemeTokenInfo(ThemeTokens.TextSecondary, "ThemeToken_TextSecondary", "ThemeTokenGroup_Text"),
        new ThemeTokenInfo(ThemeTokens.TextMuted, "ThemeToken_TextMuted", "ThemeTokenGroup_Text"),
        new ThemeTokenInfo(ThemeTokens.Accent, "ThemeToken_Accent", "ThemeTokenGroup_Accent"),
        new ThemeTokenInfo(ThemeTokens.AccentHover, "ThemeToken_AccentHover", "ThemeTokenGroup_Accent"),
        new ThemeTokenInfo(ThemeTokens.AccentGradientEnd, "ThemeToken_AccentGradientEnd", "ThemeTokenGroup_Accent"),
        new ThemeTokenInfo(ThemeTokens.Success, "ThemeToken_Success", "ThemeTokenGroup_Accent"),
        new ThemeTokenInfo(ThemeTokens.Warning, "ThemeToken_Warning", "ThemeTokenGroup_Accent"),
        new ThemeTokenInfo(ThemeTokens.Danger, "ThemeToken_Danger", "ThemeTokenGroup_Accent"),
        new ThemeTokenInfo(ThemeTokens.Info, "ThemeToken_Info", "ThemeTokenGroup_Accent"),
        new ThemeTokenInfo(ThemeTokens.String1, "ThemeToken_String1", "ThemeTokenGroup_Strings"),
        new ThemeTokenInfo(ThemeTokens.String2, "ThemeToken_String2", "ThemeTokenGroup_Strings"),
        new ThemeTokenInfo(ThemeTokens.String3, "ThemeToken_String3", "ThemeTokenGroup_Strings"),
        new ThemeTokenInfo(ThemeTokens.String4, "ThemeToken_String4", "ThemeTokenGroup_Strings"),
        new ThemeTokenInfo(ThemeTokens.String5, "ThemeToken_String5", "ThemeTokenGroup_Strings"),
        new ThemeTokenInfo(ThemeTokens.String6, "ThemeToken_String6", "ThemeTokenGroup_Strings"),
        new ThemeTokenInfo(ThemeTokens.StringExtra, "ThemeToken_StringExtra", "ThemeTokenGroup_Strings"),
        new ThemeTokenInfo(ThemeTokens.Phrase, "ThemeToken_Phrase", "ThemeTokenGroup_Chart"),
        new ThemeTokenInfo(ThemeTokens.Section, "ThemeToken_Section", "ThemeTokenGroup_Chart"),
        new ThemeTokenInfo(ThemeTokens.PhraseEvent, "ThemeToken_PhraseEvent", "ThemeTokenGroup_Chart"),
        new ThemeTokenInfo(ThemeTokens.HandShapeBar, "ThemeToken_HandShapeBar", "ThemeTokenGroup_Chart"),
        new ThemeTokenInfo(ThemeTokens.HandShapeArpeggio, "ThemeToken_HandShapeArpeggio", "ThemeTokenGroup_Chart"),
        new ThemeTokenInfo(ThemeTokens.EditTarget, "ThemeToken_EditTarget", "ThemeTokenGroup_Chart"),
        new ThemeTokenInfo(ThemeTokens.ChartPlayhead, "ThemeToken_ChartPlayhead", "ThemeTokenGroup_Chart"),
        new ThemeTokenInfo(ThemeTokens.ChartMeasure, "ThemeToken_ChartMeasure", "ThemeTokenGroup_Chart"),
        new ThemeTokenInfo(ThemeTokens.WaveformAccent, "ThemeToken_WaveformAccent", "ThemeTokenGroup_Chart"),
        new ThemeTokenInfo(ThemeTokens.ChartWaveformPrimary, "ThemeToken_ChartWaveformPrimary", "ThemeTokenGroup_Chart"),
        new ThemeTokenInfo(ThemeTokens.ChartWaveformSecondary, "ThemeToken_ChartWaveformSecondary", "ThemeTokenGroup_Chart")
    };

    private static Color C(string hex) => Color.Parse(hex);
}
