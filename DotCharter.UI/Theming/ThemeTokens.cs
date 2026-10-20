using System.Collections.ObjectModel;
using System.Reflection;

namespace DotCharter.UI.Theming;

public static class ThemeTokens
{
    public const string AppBackground = "AppBackgroundColor";
    public const string PanelBackground = "PanelBackgroundColor";
    public const string CardBackground = "CardBackgroundColor";
    public const string SurfaceElevated = "SurfaceElevatedColor";
    public const string SurfaceHover = "SurfaceHoverColor";
    public const string SurfaceHoverStrong = "SurfaceHoverStrongColor";
    public const string SurfaceActive = "SurfaceActiveColor";
    public const string SurfaceStrong = "SurfaceStrongColor";

    public const string ToolbarTop = "ToolbarTopColor";
    public const string ToolbarBottom = "ToolbarBottomColor";

    public const string BorderDefault = "BorderDefaultColor";
    public const string BorderSubtle = "BorderSubtleColor";
    public const string BorderStrong = "BorderStrongColor";
    public const string BorderDeep = "BorderDeepColor";

    public const string TextPrimary = "TextPrimaryColor";
    public const string TextSecondary = "TextSecondaryColor";
    public const string TextMuted = "TextMutedColor";
    public const string TextDim = "TextDimColor";
    public const string TextOnAccent = "TextOnAccentColor";

    public const string Accent = "AccentColorValue";
    public const string AccentHover = "AccentHoverColor";
    public const string AccentSubtle = "AccentSubtleColor";
    public const string AccentGradientEnd = "AccentGradientEndColor";
    public const string AccentGradientEndHover = "AccentGradientEndHoverColor";
    public const string AccentSelected = "AccentSelectedColor";
    public const string AccentSelectedHover = "AccentSelectedHoverColor";
    public const string AccentFaint = "AccentFaintColor";

    public const string Success = "SuccessColorValue";
    public const string Warning = "WarningColorValue";
    public const string Danger = "DangerColorValue";
    public const string DangerBg = "DangerBgColor";
    public const string DangerBorder = "DangerBorderColor";
    public const string DangerHoverBg = "DangerHoverBgColor";
    public const string Info = "InfoColorValue";
    public const string WaveformAccent = "WaveformAccentColor";

    // NOTE: the amp-sim rig accents (AmpOrange/AmpTeal/Rig* brushes in
    // Styles/Resources.axaml) are intentionally NOT registered here — they are
    // fixed brand colours (like the MedalGold/Silver/Bronze podium brushes), so
    // they stay out of the per-theme preset dictionaries and the theme-switch
    // completeness gate.

    // ─── Chart renderer tokens (shared between 2D timeline and 3D fretboard) ───
    // Strings (low-to-high; index 6 = extra/bass)
    public const string String1 = "ChartString1Color";
    public const string String2 = "ChartString2Color";
    public const string String3 = "ChartString3Color";
    public const string String4 = "ChartString4Color";
    public const string String5 = "ChartString5Color";
    public const string String6 = "ChartString6Color";
    public const string StringExtra = "ChartStringExtraColor";

    // Event overlays
    public const string Phrase = "ChartPhraseColor";
    public const string PhraseBg = "ChartPhraseBgColor";
    public const string Section = "ChartSectionColor";
    public const string PhraseEvent = "ChartPhraseEventColor";

    // Shapes
    public const string HandShapeBg = "ChartHandShapeBgColor";
    public const string HandShapeBar = "ChartHandShapeBarColor";
    public const string HandShapeArpeggio = "ChartHandShapeArpeggioColor";
    public const string EditTarget = "ChartEditTargetColor";
    public const string AnchorFhp = "ChartAnchorFhpColor";

    // Timeline chrome
    public const string ChartTopMarginBg = "ChartTopMarginBgColor";
    public const string ChartBottomMarginBg = "ChartBottomMarginBgColor";
    public const string ChartBorder = "ChartBorderColor";
    public const string ChartMinimapBorder = "ChartMinimapBorderColor";
    public const string ChartMeasure = "ChartMeasureColor";
    public const string ChartHover = "ChartHoverColor";
    public const string ChartCursor = "ChartCursorColor";
    public const string ChartPlayhead = "ChartPlayheadColor";
    public const string ChartMuted = "ChartMutedColor";
    public const string ChartWaveformInactive = "ChartWaveformInactiveColor";
    public const string ChartWaveformPrimary = "ChartWaveformPrimaryColor";
    public const string ChartWaveformSecondary = "ChartWaveformSecondaryColor";

    // ── Play Mode / rig chrome (the "Fire" layer) ────────────────────────
    //
    // The grounds, the text ladder and the accent family. Everything else in
    // that layer is either translucent — it inherits whatever ground is under
    // it — or an IDENTITY colour (strings, gear categories, stems, EQ bands,
    // medals) that a theme has no business recolouring.

    public const string FireGround = "FireGroundColor";
    public const string FireGroundScreen = "FireGroundScreenColor";
    public const string FireInk = "FireInkColor";
    public const string FireSurfaceChrome = "FireSurfaceChromeColor";
    public const string FireSurfaceChromeAlt = "FireSurfaceChromeAltColor";
    public const string FireSurfaceMenu = "FireSurfaceMenuColor";

    public const string FireTextBright = "FireTextBrightColor";
    public const string FireTextPrimary = "FireTextPrimaryColor";
    public const string FireTextBody = "FireTextBodyColor";
    public const string FireTextSecondary = "FireTextSecondaryColor";
    public const string FireTextMuted = "FireTextMutedColor";
    public const string FireTextDim = "FireTextDimColor";
    public const string FireTextFaint = "FireTextFaintColor";
    public const string FireTextOnAccent = "FireTextOnAccentColor";
    public const string FireTextOnAccentInk = "FireTextOnAccentInkColor";

    public const string FireLink = "FireLinkColor";
    public const string FireAccent = "FireAccentColor";
    public const string FireAccentStrong = "FireAccentStrongColor";
    public const string FireAccentDeep = "FireAccentDeepColor";
    public const string FireAccentBright = "FireAccentBrightColor";
    public const string FireAccentPale = "FireAccentPaleColor";
    public const string FireAccentPaper = "FireAccentPaperColor";

    public const string FireSuccess = "FireSuccessColor";
    public const string FireWarning = "FireWarningColor";
    public const string FireDanger = "FireDangerColor";

    /// <summary>
    /// Every token key the theme system knows about. Used by tests and
    /// <see cref="ThemePresets"/> to assert that every preset resolves
    /// every token, so a theme switch can never leave dangling resources.
    /// Generated by reflection over the <c>public const string</c> fields
    /// of this class so the registry can never drift from the declarations
    /// (adding a new token automatically opts it into the backfill and
    /// preset-completeness tests). Wrapped in a <see cref="ReadOnlyCollection{T}"/>
    /// so callers can't mutate the underlying array.
    /// </summary>
    public static readonly IReadOnlyList<string> All =
        new ReadOnlyCollection<string>(GetAllTokenKeys());

    private static string[] GetAllTokenKeys()
    {
        var fields = typeof(ThemeTokens).GetFields(
            BindingFlags.Public |
            BindingFlags.Static |
            BindingFlags.DeclaredOnly);

        // Keep declaration order so log output and inspector lists stay
        // grouped the way the file is organised (Surfaces → Borders →
        // Text → Accent → … → Chart). MetadataToken approximates that
        // for fields declared in source order.
        Array.Sort(fields, static (left, right) => left.MetadataToken.CompareTo(right.MetadataToken));

        var keys = new List<string>(fields.Length);
        foreach (var field in fields)
        {
            if (field is { IsLiteral: true, IsInitOnly: false } && field.FieldType == typeof(string))
                keys.Add((string)field.GetRawConstantValue()!);
        }

        return keys.ToArray();
    }
}

public sealed record ThemeTokenInfo(string Key, string DisplayName, string Category);
