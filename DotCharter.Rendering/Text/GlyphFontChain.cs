using SkiaSharp;

namespace DotCharter.Rendering.Text;

/// <summary>
///     The font a 3D glyph-sheet character is drawn with: the primary typeface
///     when it has the glyph, otherwise the installed font the system fallback
///     picks for that character (Yu Gothic / Meiryo for kana, Malgun Gothic for
///     Hangul, Microsoft YaHei for Han without kana, Segoe UI Symbol for ♪).
///     <para>
///         Without it every character the primary lacks was drawn with the
///         primary anyway, and Skia renders a missing glyph as the font's
///         <c>.notdef</c> box: Japanese lyrics in the 3D highway were a row of
///         identical tofu. The 2D timeline atlas already fell back per
///         character; the 3D sheet never did.
///     </para>
///     <para>
///         One chain lives for ONE sheet rasterization (a rebuild, which only a
///         newly seen character triggers), never per frame. Fallback typefaces
///         are deliberately NOT disposed: SkiaSharp hands back the same managed
///         wrapper for the same native typeface, so disposing one could pull it
///         out from under Avalonia's own text fallback. The fonts built on them
///         are ours and are disposed.
///     </para>
/// </summary>
internal sealed class GlyphFontChain : IDisposable
{
    private readonly string[]? _bcp47;
    private readonly Dictionary<string, SKFont> _fallbackFonts = new(StringComparer.Ordinal);
    private readonly SKTypeface _primaryTypeface;
    private readonly float _size;

    public GlyphFontChain(SKTypeface primaryTypeface, float size, string? locale)
    {
        _primaryTypeface = primaryTypeface;
        _size = size;
        _bcp47 = locale is null ? null : [locale];
        Primary = CreateFont(primaryTypeface, size);
    }

    public SKFont Primary { get; }

    /// <summary>Fallback families this chain has used — test and log seam.</summary>
    public IReadOnlyCollection<string> FallbackFamilies => _fallbackFonts.Keys;

    public void Dispose()
    {
        Primary.Dispose();
        foreach (var font in _fallbackFonts.Values) font.Dispose();
        _fallbackFonts.Clear();
    }

    /// <summary>
    ///     The font to draw <paramref name="c" /> with. The primary when it
    ///     covers the character (or nothing anywhere does — then the primary's
    ///     own missing-glyph mark is the honest result).
    /// </summary>
    public SKFont For(char c)
    {
        if (c == ' ' || Primary.ContainsGlyph(c)) return Primary;

        var match = MatchFallback(_primaryTypeface, c, _bcp47);
        if (match is null) return Primary;

        var family = match.FamilyName;
        if (_fallbackFonts.TryGetValue(family, out var known)) return known;

        var font = CreateFont(match, _size);
        _fallbackFonts[family] = font;
        return font;
    }

    /// <summary>
    ///     The system's choice of an installed typeface that HAS
    ///     <paramref name="c" />, in the primary's weight, or <c>null</c>.
    /// </summary>
    internal static SKTypeface? MatchFallback(SKTypeface primary, char c, string[]? bcp47)
    {
        var match = SKFontManager.Default.MatchCharacter(primary.FamilyName, primary.FontWeight,
            primary.FontWidth, primary.FontSlant, bcp47, c);
        if (match is null) return null;
        using var probe = new SKFont(match);
        return probe.ContainsGlyph(c) ? match : null;
    }

    /// <summary>
    ///     Pure: the locale that steers Han fallback, decided over the WHOLE
    ///     character set because one Han character alone does not say which
    ///     language it belongs to. Any kana means Japanese (Chinese text never
    ///     contains kana), Hangul means Korean, Han without either reads as
    ///     Simplified Chinese. <c>null</c> leaves the choice to the user's
    ///     locale — nothing in the set needs steering.
    /// </summary>
    internal static string? FallbackLocale(IEnumerable<char> chars)
    {
        var han = false;
        var hangul = false;
        foreach (var c in chars)
        {
            if (IsKana(c)) return "ja";
            if (IsHangul(c)) hangul = true;
            else if (IsHan(c)) han = true;
        }

        if (hangul) return "ko";
        return han ? "zh-Hans" : null;
    }

    private static bool IsKana(char c) =>
        c is >= '぀' and <= 'ヿ' or >= 'ㇰ' and <= 'ㇿ' or >= 'ｦ' and <= 'ﾟ';

    private static bool IsHangul(char c) =>
        c is >= '가' and <= '힯' or >= 'ᄀ' and <= 'ᇿ' or >= '㄰' and <= '㆏';

    private static bool IsHan(char c) =>
        c is >= '一' and <= '鿿' or >= '㐀' and <= '䶿' or >= '豈' and <= '﫿';

    private static SKFont CreateFont(SKTypeface typeface, float size) =>
        new(typeface, size) { Subpixel = true, Edging = SKFontEdging.SubpixelAntialias };
}
