// ReSharper disable InconsistentNaming -- private static brush/pen fields use _camelCase to pair with same-named public PascalCase properties.
using System.Globalization;
using Avalonia;
using Avalonia.Media;
using DotCharter.Rendering.Data;
using DotCharter.UI.Theming;

namespace DotCharter.UI.Controls.Themes;

// Palette source for every 2D Avalonia-drawn chart surface (timeline layers,
// minimap, mini fretboard). Brush instances are created once and mutated in
// place when the active theme changes, so consumers that cache brush
// references pick up new colors automatically at the next redraw.
public static class ChartTheme
{
    private const int MaxTextCacheSize = 1000;

    // ─── Mutable color slots (seeded with defaults that match Dark Slate) ──
    // Legacy palette that isn't theme-driven — kept as constants for generic
    // brushes some layers reference (gold highlight, white, etc.).
    public static Color BlackColor = Color.Parse("#000000");
    public static Color GoldColor = Color.Parse("#FBBF24");
    public static Color SpringGreenColor = Color.Parse("#34D399");
    public static Color EmeraldColor = Color.Parse("#10B981");
    public static Color SkyBlueColor = Color.Parse("#38BDF8");
    public static Color Sky600Color = Color.Parse("#0284C7");
    public static Color Blue800Color = Color.Parse("#1E40AF");
    public static Color AmberColor = Color.Parse("#F59E0B");
    public static Color WhiteSmokeColor = Color.Parse("#F4F4F5");
    public static Color GrayColor = Color.Parse("#A1A1AA");
    public static Color LightGrayColor = Color.Parse("#D4D4D8");
    public static Color WhiteColor = Colors.White;
    public static Color CyanColor = Color.Parse("#22D3EE");
    public static Color MagentaColor = Color.Parse("#E879F9");
    public static Color LightGreenColor = Color.Parse("#A3E635");
    public static Color YellowColor = Color.Parse("#FDE047");
    public static Color Purple800Color = Color.Parse("#5B46BC");
    public static Color Purple900Color = Color.Parse("#463888");
    public static Color Slate700Color = Color.Parse("#334155");
    public static Color Slate900Color = Color.Parse("#0F172A");
    public static Color ChordLinkerColor = Color.FromArgb(70, 200, 200, 210);
    public static Color ViewportBgColor = Color.FromArgb(30, 255, 255, 255);
    public static Color ActiveWaveformColor = Color.Parse("#663B82F6");
    public static Color GuideColor = Color.FromArgb(80, 63, 63, 70);
    public static Color BeatColor = Color.Parse("#3F3F46");
    public static Color AnchorColor = Color.Parse("#FBBF24");
    public static Color BookmarkColor = Color.Parse("#22C55E");

    // Theme-driven chart slots — default values mirror the DarkSlate preset so
    // things look right before ApplyTheme runs (e.g. design-time previewer).
    public static Color MutedNoteColor = Color.Parse("#3F3F46");
    public static Color TopMarginBgColor = Color.Parse("#18181B");
    public static Color BottomMarginBgColor = Color.Parse("#09090B");
    public static Color HoverLaneColor = Color.FromArgb(60, 255, 255, 255);
    public static Color CursorLineColor = Color.FromArgb(160, 255, 255, 255);
    public static Color PlayheadTimeBgColor = Color.Parse("#E11D48");
    public static Color HandShapeBgColor = Color.FromArgb(32, 56, 189, 248);
    public static Color HandShapeBarColor = Color.Parse("#0284C7");
    // an arpeggio bar, and the edge/tint of a bar the inspector has a
    // finding against (E05/D09) — the shared Warning token, not a chart-only one.
    public static Color HandShapeArpeggioColor = Color.Parse("#C040FF");
    public static Color HandShapeWarningColor = Color.Parse("#F59E0B");

    // what a pending bulk chord edit would change — its outline on the 2D
    // timeline and its marks on the song map.
    public static Color EditTargetColor = Color.Parse("#FF4FA3");
    public static Color AnchorFhpBgColor = Color.FromArgb(60, 37, 144, 232);
    public static Color MinimapWaveformColor = Color.Parse("#333B82F6");
    public static Color PhraseBgColor = Color.FromArgb(80, 30, 41, 59);
    public static Color InactiveWaveformColor = Color.Parse("#333B82F6");
    public static Color SectionBgColor = Color.FromArgb(255, 165, 54, 178);
    public static Color PhraseEventBgColor = Color.FromArgb(255, 179, 118, 54);
    public static Color BorderColor = Color.Parse("#27272A");
    public static Color MinimapBorderColor = Color.Parse("#52525B");
    public static Color MeasureColor = Color.Parse("#71717A");

    public static Color[] BaseStringColors =
    [
        Color.Parse("#FF3333"), Color.Parse("#FFD700"), Color.Parse("#3388FF"),
        Color.Parse("#FF8C00"), Color.Parse("#22CC22"), Color.Parse("#9933FF"),
        Color.Parse("#C8C8C8"), Color.Parse("#787878")
    ];

    /// <summary>
    ///     Palette index of string <paramref name="stringIndex" /> on an instrument of
    ///     <paramref name="stringCount" /> strings — every 2D surface indexes
    ///     <see cref="BaseStringColors" />, the lane tints and <see cref="StringBrushes" />
    /// through it, never with the raw string index (a 7-string's low E stays red,
    ///     its low B is grey). See <see cref="StringColorSlots" />.
    /// </summary>
    public static int StringSlot(int stringIndex, int stringCount) => StringColorSlots.For(stringIndex, stringCount);

    /// <summary>String <paramref name="stringIndex" />'s brush on an instrument of <paramref name="stringCount" /> strings.</summary>
    public static ISolidColorBrush StringBrushFor(int stringIndex, int stringCount)
        => MutableStringBrushes[StringColorSlots.For(stringIndex, stringCount)];

    /// <summary>
    ///     The 8th string's grey, derived from the 7th's so a theme or a colour-blind preset
    ///     that re-colours the extra string moves both, and the 8th stays the darker of the two.
    /// </summary>
    internal static Color EighthStringColorFrom(Color seventh)
        => Color.FromArgb(seventh.A, (byte)(seventh.R * 0.6), (byte)(seventh.G * 0.6), (byte)(seventh.B * 0.6));

    // Pre-baked alpha-80 variant of BaseStringColors used as the lane guide
    // tint. Building these once eliminates the per-frame `Color.FromArgb`
    // call inside LaneLayer (one per visible string × every frame).
    public static Color[] BaseStringLaneColors = BuildLaneTints(BaseStringColors, 80);
    // Brighter variant used when a lane is actively hovered for visual hierarchy.
    public static Color[] BaseStringLaneHoverColors = BuildLaneTints(BaseStringColors, 180);
    // Whisper-faint (~4% alpha) lane wash for the 0.14.3 string-lane tinting
    // toggle — one full-lane rect per string when enabled.
    public static Color[] BaseStringLaneTintColors = BuildLaneTints(BaseStringColors, 10);

    private static Color[] BuildLaneTints(Color[] src, byte alpha)
    {
        var arr = new Color[src.Length];
        for (var i = 0; i < src.Length; i++)
            arr[i] = Color.FromArgb(alpha, src[i].R, src[i].G, src[i].B);
        return arr;
    }

    // ─── Brush / Pen instances (mutated in place on theme change) ──────────
    private static readonly SolidColorBrush MutableGoldBrush = new(GoldColor);
    private static readonly SolidColorBrush MutableSpringGreenBrush = new(SpringGreenColor);
    private static readonly SolidColorBrush MutableSkyBlueBrush = new(SkyBlueColor);
    private static readonly SolidColorBrush MutableWhiteSmokeBrush = new(WhiteSmokeColor);
    private static readonly SolidColorBrush MutableGrayBrush = new(GrayColor);
    private static readonly SolidColorBrush MutableLightGrayBrush = new(LightGrayColor);
    private static readonly SolidColorBrush MutableWhiteBrush = new(Colors.White);
    private static readonly SolidColorBrush MutableCyanBrush = new(CyanColor);
    private static readonly SolidColorBrush MutableMagentaBrush = new(MagentaColor);
    private static readonly SolidColorBrush MutableLightGreenBrush = new(LightGreenColor);
    private static readonly SolidColorBrush MutableYellowBrush = new(YellowColor);
    private static readonly SolidColorBrush MutableMutedNoteBrush = new(MutedNoteColor);
    private static readonly SolidColorBrush MutableTopMarginBgBrush = new(TopMarginBgColor);
    private static readonly SolidColorBrush MutableBottomMarginBgBrush = new(BottomMarginBgColor);
    private static readonly SolidColorBrush MutableHoverBrush = new(HoverLaneColor);
    private static readonly SolidColorBrush MutablePlayheadTimeBgBrush = new(PlayheadTimeBgColor);
    private static readonly SolidColorBrush MutableHandShapeBgBrush = new(HandShapeBgColor);
    private static readonly SolidColorBrush MutableHandShapeBarBrush = new(HandShapeBarColor);
    private static readonly SolidColorBrush MutableChordLinkerBrush = new(ChordLinkerColor);
    private static readonly SolidColorBrush MutablePhraseBgBrush = new(PhraseBgColor);
    private static readonly SolidColorBrush MutableViewportBgBrush = new(ViewportBgColor);
    private static readonly SolidColorBrush MutableActiveWaveformBrush = new(ActiveWaveformColor);
    private static readonly SolidColorBrush MutableInactiveWaveformBrush = new(InactiveWaveformColor);
    private static readonly SolidColorBrush MutableSectionBgBrush = new(SectionBgColor);
    private static readonly SolidColorBrush MutablePhraseEventBgBrush = new(PhraseEventBgColor);
    private static readonly SolidColorBrush MutableEditTargetBrush = new(EditTargetColor);

    private static readonly SolidColorBrush GuidePenBrush = new(GuideColor);
    private static readonly SolidColorBrush BorderPenBrush = new(BorderColor);
    private static readonly SolidColorBrush MeasurePenBrush = new(MeasureColor);

    private static readonly SolidColorBrush[] MutableStringBrushes =
        BaseStringColors.Select(c => new SolidColorBrush(c)).ToArray();

    private static readonly Pen MutableGuidePen = new(GuidePenBrush);
    private static readonly Pen MutableBorderPen = new(BorderPenBrush, 1.5);
    private static readonly Pen MutableMeasurePen = new(MeasurePenBrush, 2);

    private static FormattedText[]? _fretTexts;
    private static FormattedText[]? _fretTextsBlack;
    private static readonly Dictionary<TextCacheKey, FormattedText> TextCache = [];

    public static Typeface DefaultTypeface => Typeface.Default;
    public static Typeface BoldTypeface => new("Arial", FontStyle.Normal, FontWeight.Bold);

    // ─── Public accessors (stable call-site surface) ───────────────────────
    public static ISolidColorBrush GoldBrush => MutableGoldBrush;
    public static ISolidColorBrush SpringGreenBrush => MutableSpringGreenBrush;
    public static ISolidColorBrush SkyBlueBrush => MutableSkyBlueBrush;
    public static ISolidColorBrush WhiteSmokeBrush => MutableWhiteSmokeBrush;
    public static ISolidColorBrush GrayBrush => MutableGrayBrush;
    public static ISolidColorBrush LightGrayBrush => MutableLightGrayBrush;
    public static ISolidColorBrush WhiteBrush => MutableWhiteBrush;
    public static ISolidColorBrush CyanBrush => MutableCyanBrush;
    public static ISolidColorBrush MagentaBrush => MutableMagentaBrush;
    public static ISolidColorBrush LightGreenBrush => MutableLightGreenBrush;
    public static ISolidColorBrush YellowBrush => MutableYellowBrush;
    public static ISolidColorBrush MutedNoteBrush => MutableMutedNoteBrush;
    public static ISolidColorBrush TopMarginBgBrush => MutableTopMarginBgBrush;
    public static ISolidColorBrush BottomMarginBgBrush => MutableBottomMarginBgBrush;
    public static ISolidColorBrush HoverBrush => MutableHoverBrush;
    public static ISolidColorBrush PlayheadTimeBgBrush => MutablePlayheadTimeBgBrush;
    public static ISolidColorBrush HandShapeBgBrush => MutableHandShapeBgBrush;
    public static ISolidColorBrush HandShapeBarBrush => MutableHandShapeBarBrush;
    public static ISolidColorBrush ChordLinkerBrush => MutableChordLinkerBrush;
    public static ISolidColorBrush PhraseBgBrush => MutablePhraseBgBrush;
    public static ISolidColorBrush ViewportBgBrush => MutableViewportBgBrush;
    public static ISolidColorBrush ActiveWaveformBrush => MutableActiveWaveformBrush;
    public static ISolidColorBrush InactiveWaveformBrush => MutableInactiveWaveformBrush;
    public static ISolidColorBrush SectionBgBrush => MutableSectionBgBrush;
    public static ISolidColorBrush PhraseEventBgBrush => MutablePhraseEventBgBrush;
    public static ISolidColorBrush EditTargetBrush => MutableEditTargetBrush;

    public static IPen GuidePen => MutableGuidePen;
    public static IPen BorderPen => MutableBorderPen;
    public static IPen MeasurePen => MutableMeasurePen;
    public static IReadOnlyList<ISolidColorBrush> StringBrushes => MutableStringBrushes;

    public static FormattedText[] FretTexts =>
        _fretTexts ??= BuildFretTexts(MutableWhiteBrush);

    public static FormattedText[] FretTextsBlack =>
        _fretTextsBlack ??= BuildFretTexts(MutableTopMarginBgBrush);

    private static FormattedText[] BuildFretTexts(IBrush brush)
    {
        return Enumerable.Range(0, 31).Select(i =>
            new FormattedText(i.ToString(), CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, BoldTypeface, 12, brush)).ToArray();
    }

    // Raised after brushes are repainted so chart layers can InvalidateVisual.
    public static event Action? ThemeChanged;

    /// <summary>Drops every subscriber, for <c>App.ReleaseForIsolatedTeardown</c>
    /// (test hosts that boot an app per dispatch): each belongs to a control of
    /// the app being released, and one whose window was never closed would root
    /// that window's tree for the rest of the process.</summary>
    internal static void DropSubscribersForIsolatedTeardown() => ThemeChanged = null;

    // Subscribes to IThemeService.ThemeChanged and keeps every brush + the
    // shared 3D StringColors array aligned with the active palette.
    public static void Attach(IThemeService themeService)
    {
        if (themeService == null) throw new ArgumentNullException(nameof(themeService));
        Apply(themeService);
        themeService.ThemeChanged += () => Apply(themeService);
    }

    private static void Apply(IThemeService svc)
    {
        // Chart-dedicated tokens
        MutedNoteColor = svc.GetColor(ThemeTokens.ChartMuted);
        TopMarginBgColor = svc.GetColor(ThemeTokens.ChartTopMarginBg);
        BottomMarginBgColor = svc.GetColor(ThemeTokens.ChartBottomMarginBg);
        HoverLaneColor = svc.GetColor(ThemeTokens.ChartHover);
        CursorLineColor = svc.GetColor(ThemeTokens.ChartCursor);
        PlayheadTimeBgColor = svc.GetColor(ThemeTokens.ChartPlayhead);
        HandShapeBgColor = svc.GetColor(ThemeTokens.HandShapeBg);
        HandShapeBarColor = svc.GetColor(ThemeTokens.HandShapeBar);
        HandShapeArpeggioColor = svc.GetColor(ThemeTokens.HandShapeArpeggio);
        HandShapeWarningColor = svc.GetColor(ThemeTokens.Warning);
        EditTargetColor = svc.GetColor(ThemeTokens.EditTarget);
        AnchorFhpBgColor = svc.GetColor(ThemeTokens.AnchorFhp);
        PhraseBgColor = svc.GetColor(ThemeTokens.PhraseBg);
        InactiveWaveformColor = svc.GetColor(ThemeTokens.ChartWaveformInactive);
        MinimapWaveformColor = InactiveWaveformColor;
        SectionBgColor = svc.GetColor(ThemeTokens.Section);
        PhraseEventBgColor = svc.GetColor(ThemeTokens.PhraseEvent);
        BorderColor = svc.GetColor(ThemeTokens.ChartBorder);
        MinimapBorderColor = svc.GetColor(ThemeTokens.ChartMinimapBorder);
        MeasureColor = svc.GetColor(ThemeTokens.ChartMeasure);
        BeatColor = MutedNoteColor;
        ActiveWaveformColor = svc.GetColor(ThemeTokens.WaveformAccent);
        Purple800Color = svc.GetColor(ThemeTokens.ChartWaveformPrimary);
        Purple900Color = svc.GetColor(ThemeTokens.ChartWaveformSecondary);
        GoldColor = svc.GetColor(ThemeTokens.Phrase);
        AnchorColor = GoldColor;

        // String colors — shared between the 2D timeline and the 3D Vulkan view.
        BaseStringColors[0] = svc.GetColor(ThemeTokens.String1);
        BaseStringColors[1] = svc.GetColor(ThemeTokens.String2);
        BaseStringColors[2] = svc.GetColor(ThemeTokens.String3);
        BaseStringColors[3] = svc.GetColor(ThemeTokens.String4);
        BaseStringColors[4] = svc.GetColor(ThemeTokens.String5);
        BaseStringColors[5] = svc.GetColor(ThemeTokens.String6);
        BaseStringColors[StringColorSlots.SeventhStringSlot] = svc.GetColor(ThemeTokens.StringExtra);
        BaseStringColors[StringColorSlots.EighthStringSlot] =
            EighthStringColorFrom(BaseStringColors[StringColorSlots.SeventhStringSlot]);
        for (var i = 0; i < MutableStringBrushes.Length; i++)
            MutableStringBrushes[i].Color = BaseStringColors[i];

        // Refresh the precomputed alpha-tinted lane variants so LaneLayer
        // doesn't drift away from the active palette.
        for (var i = 0; i < BaseStringColors.Length; i++)
        {
            var c = BaseStringColors[i];
            BaseStringLaneColors[i] = Color.FromArgb(80, c.R, c.G, c.B);
            BaseStringLaneHoverColors[i] = Color.FromArgb(180, c.R, c.G, c.B);
            BaseStringLaneTintColors[i] = Color.FromArgb(10, c.R, c.G, c.B);
        }

        // Push into the 3D renderer’s palette too.
        for (var i = 0; i < FretboardConstants.StringColors.Length; i++)
            FretboardConstants.StringColors[i] = BaseStringColors[i];

        // Push color changes into the shared brush instances.
        MutableGoldBrush.Color = GoldColor;
        MutableSpringGreenBrush.Color = SpringGreenColor;
        MutableSkyBlueBrush.Color = SkyBlueColor;
        MutableWhiteSmokeBrush.Color = WhiteSmokeColor;
        MutableGrayBrush.Color = GrayColor;
        MutableLightGrayBrush.Color = LightGrayColor;
        MutableCyanBrush.Color = CyanColor;
        MutableMagentaBrush.Color = MagentaColor;
        MutableLightGreenBrush.Color = LightGreenColor;
        MutableYellowBrush.Color = YellowColor;
        MutableMutedNoteBrush.Color = MutedNoteColor;
        MutableTopMarginBgBrush.Color = TopMarginBgColor;
        MutableBottomMarginBgBrush.Color = BottomMarginBgColor;
        MutableHoverBrush.Color = HoverLaneColor;
        MutablePlayheadTimeBgBrush.Color = PlayheadTimeBgColor;
        MutableHandShapeBgBrush.Color = HandShapeBgColor;
        MutableHandShapeBarBrush.Color = HandShapeBarColor;
        MutableChordLinkerBrush.Color = ChordLinkerColor;
        MutablePhraseBgBrush.Color = PhraseBgColor;
        MutableViewportBgBrush.Color = ViewportBgColor;
        MutableActiveWaveformBrush.Color = ActiveWaveformColor;
        MutableInactiveWaveformBrush.Color = InactiveWaveformColor;
        MutableSectionBgBrush.Color = SectionBgColor;
        MutablePhraseEventBgBrush.Color = PhraseEventBgColor;
        MutableEditTargetBrush.Color = EditTargetColor;
        GuidePenBrush.Color = GuideColor;
        BorderPenBrush.Color = BorderColor;
        MeasurePenBrush.Color = MeasureColor;

        // FretText arrays bake the brush reference at construction. The white
        // variant rides along automatically via the shared MutableWhiteBrush, but the
        // black variant's brush color was just mutated, so rebuild both to
        // guarantee correct glyph colors (also clears stale color cache keys).
        _fretTexts = null;
        _fretTextsBlack = null;
        TextCache.Clear();

        ThemeChanged?.Invoke();
    }

    public static FormattedText GetCachedText(string text, double size, ISolidColorBrush brush, bool isBold = false,
        bool useCache = true)
    {
        if (!useCache)
        {
            return new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                isBold ? BoldTypeface : DefaultTypeface, size, brush);
        }

        var key = new TextCacheKey(text, size, brush.Color.ToUInt32(), isBold);
        if (!TextCache.TryGetValue(key, out var ft))
        {
            if (TextCache.Count > MaxTextCacheSize) TextCache.Clear();

            ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                isBold ? BoldTypeface : DefaultTypeface, size, brush);
            TextCache[key] = ft;
        }

        return ft;
    }

    public static void DrawCenteredText(DrawingContext context, FormattedText ft, Rect bounds)
    {
        var x = bounds.X + (bounds.Width - ft.Width) / 2.0;
        var y = bounds.Y + (bounds.Height - ft.Height) / 2.0;
        context.DrawText(ft, new Point(x, y));
    }

    private record struct TextCacheKey(string Text, double Size, uint Color, bool IsBold);
}
