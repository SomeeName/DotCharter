using System.Diagnostics;
using SkiaSharp;

namespace DotCharter.Rendering.Text;

public struct GlyphInfo
{
    public float U1, V1, U2, V2, Width, Height, Advance, OffsetX;
}

/// <summary>
///     Backend-agnostic 2D chart font atlas: SkiaSharp rasterization, glyph
///     registration/metrics and the rebuild gate. The upload subclass
///     (<c>Vulkan2D.VulkanTimelineFontAtlas</c> — plus the headless
///     renderer's instance) only implements the GPU texture upload
///     (<see cref="UploadAtlas" />) — every metric the batcher consumes
///     (<see cref="GlyphMap" />, <see cref="AsciiGlyphs" />,
///     <see cref="MeasureTextWidth" />, <see cref="RowHeight" />,
///     <see cref="TextCenterYOffset" />) is computed here, so text geometry
///     is a pure function of the base class regardless of who uploads.
/// </summary>
public abstract class ChartFontAtlas : IDisposable
{
    /// <summary>Fixed square glyph-sheet dimension in texels.</summary>
    public const int AtlasSize = 2048;

    /// <summary>The characters every atlas starts with — standard ASCII plus the
    /// notation symbols the chart draws. Everything past this is registered from
    /// the OPEN PROJECT's own text (lyrics, section/phrase names, tone names), so
    /// it is project-scoped and <see cref="ResetToSeedCharacters" /> takes it back
    /// off on a project switch.</summary>
    private const string SeedCharacters =
        "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz #b-.,:;()[]{}%=\\|/ノ½?";

    /// <summary>How many distinct characters a freshly reset atlas holds.</summary>
    public static int SeedCharacterCount { get; } = new HashSet<char>(SeedCharacters).Count;

    // Dynamic hashset initialized with standard ASCII and basic symbols
    private readonly HashSet<char> _charsToRender = [.. SeedCharacters.ToCharArray()];

    /// <summary>Distinct characters the sheet is packed for right now — test seam
    /// and the number the overflow warning prints.</summary>
    public int RegisteredCharacterCount => _charsToRender.Count;

    public readonly Dictionary<char, GlyphInfo> GlyphMap = [];

    // Hot-path fast lookup for ASCII glyphs. Indexed by char value 0..127.
    // Mirrors GlyphMap entries for printable ASCII chars; non-ASCII falls
    // back to GlyphMap. AsciiValid[c] indicates a populated slot.
    public readonly GlyphInfo[] AsciiGlyphs = new GlyphInfo[128];
    public readonly bool[] AsciiValid = new bool[128];

    private bool _fontBold;
    private string _fontFamily = "Default";
    private float _fontSize = 14f;
    private bool _overflowWarned;

    /// <summary>
    ///     Physical pixels per logical (DIP) unit of the surface this atlas is
    ///     drawn onto — the TopLevel's <c>RenderScaling</c>. The glyph sheet is
    ///     RASTERIZED at <c>fontSize × renderScale</c> while every metric it
    ///     hands back (advance, width, row height, offsets) is divided back
    ///     down, so the batcher keeps emitting quads in logical units and the
    ///     GPU viewport (which is in physical pixels) samples a sheet that has
    ///     the pixels it actually needs. A hardcoded 1.0 would be wrong: on a
    ///     150 % display every 2D label would be rasterized at 14 px and stretched
    ///     to 21 px — soft text on every HiDPI machine.
    /// </summary>
    private float _renderScale = 1.0f;

    /// <summary>Physical-pixels-per-DIP the sheet is rasterized for. Clamped:
    /// below 1 there is nothing to gain, and past 4 the fixed 2048² sheet
    /// starts overflowing (the guard below would silently blank glyphs).</summary>
    public float RenderScale => _renderScale;

    public float TextCenterYOffset { get; private set; }
    public float RowHeight { get; private set; }
    private bool NeedsRebuild { get; set; } = true;

    /// <summary>
    ///     Sets the surface scale (Avalonia's <c>TopLevel.RenderScaling</c>).
    ///     Cheap to call every frame — it only repacks when the value actually
    ///     changes (moving the window to a differently-scaled monitor).
    /// </summary>
    public void SetRenderScale(float renderScale)
    {
        var clamped = Math.Clamp(renderScale, 1.0f, 4.0f);
        // ReSharper disable once CompareOfFloatsByEqualityOperator
        if (clamped == _renderScale) return;
        _renderScale = clamped;
        _overflowWarned = false;
        NeedsRebuild = true;
    }

    public void SetChartFont(string fontFamily, float fontSize, bool isBold)
    {
        _fontFamily = string.IsNullOrWhiteSpace(fontFamily) ? "Default" : fontFamily;
        _fontSize = Math.Max(8f, fontSize);
        _fontBold = isBold;
        // A different font/size repacks from scratch — allow one fresh
        // overflow warning if the new metrics still don't fit.
        _overflowWarned = false;
        NeedsRebuild = true;
    }

    public void RegisterCharacters(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var added = false;
        foreach (var c in text)
        {
            if (char.IsControl(c)) continue;
            if (_charsToRender.Add(c)) added = true;
        }

        if (added) NeedsRebuild = true;
    }

    /// <summary>
    ///     Drops every character registered from a project's content and goes
    ///     back to <see cref="SeedCharacters" />.
    ///     <para>
    ///         Called on a project switch (the <c>ActiveProject</c> notification
    ///         — see <c>docs/agents/ui/project-lifecycle.md</c>), because the set
    ///         is only ever ADDED to: every lyric, section name, phrase name and
    ///         tone name of every song opened in the session accumulated in it for
    ///         the life of the process. The fixed 2048² sheet then overflows, and
    ///         an overflowed glyph renders BLANK — so this is a correctness fix
    ///         as much as a memory one: a long session eventually loses text on
    ///         the highway it cannot pack.
    ///     </para>
    ///     No-op when nothing project-scoped was registered, so it is cheap to
    ///     call on every switch.
    /// </summary>
    public void ResetToSeedCharacters()
    {
        if (_charsToRender.Count == SeedCharacterCount) return;
        _charsToRender.Clear();
        foreach (var c in SeedCharacters) _charsToRender.Add(c);
        // A fresh set may well fit where the grown one did not.
        _overflowWarned = false;
        NeedsRebuild = true;
    }

    public void RebuildIfNeeded()
    {
        if (!NeedsRebuild) return;
        BuildAtlas();
        NeedsRebuild = false;
    }

    /// <summary>
    ///     Uploads the freshly rasterized glyph sheet
    ///     (<see cref="AtlasSize" />² RGBA8888, unpremultiplied — white glyphs
    ///     on transparent) to the backend texture, replacing any previous one.
    ///     Called on the thread that called <see cref="RebuildIfNeeded" />
    ///     (the render/UI thread of the owning viewport).
    /// </summary>
    protected abstract void UploadAtlas(SKBitmap bitmap);

    public abstract void Dispose();

    private void BuildAtlas()
    {
        var renderScale = _renderScale;
        var scaledFontSize = _fontSize * renderScale;

        using var bitmap = new SKBitmap(AtlasSize, AtlasSize, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var canvas = new SKCanvas(bitmap);

        canvas.Clear(new SKColor(255, 255, 255, 0));

        var weight = _fontBold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal;
        using var defaultTypeface = _fontFamily == "Default"
            ? SKTypeface.Default
            : SKTypeface.FromFamilyName(_fontFamily, weight, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright) ??
              SKTypeface.Default;

        using var defaultFont = new SKFont(defaultTypeface, scaledFontSize);
        defaultFont.Subpixel = true;
        defaultFont.Edging = SKFontEdging.SubpixelAntialias;

        using var paint = new SKPaint();
        paint.Color = SKColors.White;
        paint.IsAntialias = true;

        var metrics = defaultFont.Metrics;
        var ascent = (float)Math.Floor(metrics.Ascent);
        var descent = (float)Math.Ceiling(metrics.Descent);
        var rowHeight = descent - ascent;
        var baselineOffset = -ascent;

        defaultFont.MeasureText("0", out var bounds);
        TextCenterYOffset = (baselineOffset + bounds.MidY) / renderScale;
        RowHeight = rowHeight / renderScale;

        float x = 0;
        float y = 0;
        var overflowed = 0;
        GlyphMap.Clear();
        Array.Clear(AsciiValid);

        // Pack in CODE-POINT ORDER, not HashSet order. A HashSet enumerates in
        // insertion order (modulo collisions), so two sessions that registered
        // the same characters in a different sequence — which is just "the user
        // opened a different chart first" — packed the sheet differently and
        // produced different UVs for identical text. That made the 2D
        // golden-frame byte-stability claim hold only within one process, and it
        // makes any atlas comparison across runs meaningless. Sorting costs one
        // array per REBUILD (rare — a font/scale change or a newly seen glyph),
        // never per frame.
        var packOrder = new char[_charsToRender.Count];
        _charsToRender.CopyTo(packOrder);
        Array.Sort(packOrder);

        foreach (var c in packOrder)
        {
            var text = c.ToString();
            var currentTypeface = defaultTypeface;
            var glyphs = defaultFont.GetGlyphs(text);

            if (glyphs.Length > 0 && glyphs[0] == 0)
            {
                var fallback = SKFontManager.Default.MatchCharacter(c);
                if (fallback != null) currentTypeface = fallback;
            }

            using var charFont = new SKFont(currentTypeface, scaledFontSize);
            charFont.Subpixel = true;
            charFont.Edging = SKFontEdging.SubpixelAntialias;

            var advance = charFont.MeasureText(text, out var charBounds);
            if (c == ' ') advance = charFont.MeasureText("a") * 0.4f;

            // WHOLE TEXELS, all three. The sheet is sampled 1:1 by the
            // batcher, so a glyph box that starts or ends on a fractional texel
            // is read back as a bilinear blend of two texel columns however
            // well the destination is aligned — and at a fractional render
            // scale (the global UI scale; 4 x 1.30 = 5.2) that is EVERY glyph.
            // Rounding here also puts the DrawText pen on a whole pixel inside
            // the sheet, so Skia rasterizes each glyph at phase 0 instead of at
            // whatever sub-pixel phase its predecessor's advance left behind.
            var margin = Math.Max(1f, MathF.Round(4f * renderScale));
            var texWidth = MathF.Ceiling(Math.Max(advance, charBounds.Right)) + margin;
            var padding = Math.Max(1f, MathF.Round(8f * renderScale));

            if (x + texWidth > AtlasSize)
            {
                x = 0;
                y += rowHeight + padding;
            }

            // Row-overflow guard: a glyph placed past the bottom of the fixed
            // sheet would get V coordinates > 1.0 (Skia silently clips the
            // draw, and sampling would read unrelated rows). Skip it — the
            // batcher treats absent GlyphMap entries as zero-width, so the
            // character renders blank instead of as garbage — and log once.
            if (y + rowHeight > AtlasSize)
            {
                overflowed++;
                continue;
            }

            if (c != ' ') canvas.DrawText(text, x + margin, y + baselineOffset, SKTextAlign.Left, charFont, paint);

            GlyphMap[c] = new GlyphInfo
            {
                U1 = x / AtlasSize,
                V1 = y / AtlasSize,
                U2 = (x + texWidth) / AtlasSize,
                V2 = (y + rowHeight) / AtlasSize,
                Width = texWidth / renderScale,
                Height = rowHeight / renderScale,
                Advance = advance / renderScale,
                OffsetX = -margin / renderScale
            };

            if (c < 128)
            {
                AsciiGlyphs[c] = GlyphMap[c];
                AsciiValid[c] = true;
            }

            x += texWidth + padding;
        }

        OverflowedGlyphCount = overflowed;
        if (OverflowWarning(overflowed, _overflowWarned, _charsToRender.Count) is { } warning)
        {
            _overflowWarned = true;
            LastOverflowWarning = warning;
            // Debug.WriteLine is COMPILED OUT of a Release build, so for as long
            // as this was the only channel the shipped app dropped glyphs in
            // total silence. Console.Error survives — same channel
            // VulkanValidationDiagnostics uses for a fault the user can hit.
            Debug.WriteLine(warning);
            Console.Error.WriteLine(warning);
        }

        UploadAtlas(bitmap);
    }

    /// <summary>Glyphs the last pack could not fit, and which therefore render
    /// BLANK. Nonzero means text is missing on screen.</summary>
    public int OverflowedGlyphCount { get; private set; }

    /// <summary>The warning text of the last overflow, or null if there has not
    /// been one. Reset with the sheet (a font/scale change or
    /// <see cref="ResetToSeedCharacters" /> may well fit).</summary>
    public string? LastOverflowWarning { get; private set; }

    /// <summary>
    ///     Pure: the once-per-sheet overflow warning, or <c>null</c> when there
    ///     is nothing to say (nothing overflowed, or it has already been said).
    ///     Split out of <see cref="BuildAtlas" /> so the DECISION is testable
    ///     without rasterizing a 2048² sheet.
    /// </summary>
    internal static string? OverflowWarning(int overflowed, bool alreadyWarned, int registeredCharacters)
    {
        if (overflowed <= 0 || alreadyWarned) return null;
        return $"[FONT ATLAS] 2D atlas sheet is full ({AtlasSize}x{AtlasSize}, " +
               $"{registeredCharacters} distinct chars registered): " +
               $"{overflowed} glyph(s) skipped and will render blank.";
    }

    public float MeasureTextWidth(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        float width = 0;
        var ascii = AsciiGlyphs;
        var valid = AsciiValid;
        foreach (var ch in text)
        {
            if (ch < 128)
            {
                if (valid[ch]) width += ascii[ch].Advance;
            }
            else if (GlyphMap.TryGetValue(ch, out var glyph))
            {
                width += glyph.Advance;
            }
        }

        return width;
    }
}
