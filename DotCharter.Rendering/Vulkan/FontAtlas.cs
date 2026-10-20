using DotCharter.Rendering.Text;
using SkiaSharp;

namespace DotCharter.Rendering.Vulkan;

public struct GlyphMetrics
{
    public float U0, V0, U1, V1;
    public float Width, Height;
    public float AdvanceX;
}

public class FontAtlas : IDisposable
{
    /// <summary>Glyph-sheet dimensions in texels. The width is fixed; the
    /// height is <see cref="AtlasHeight" /> until the registered set needs
    /// more, then <see cref="MaxAtlasHeight" /> (see <see cref="Rasterize" />).</summary>
    public const int AtlasWidth = 2048;

    public const int AtlasHeight = 2048;

    public const int MaxAtlasHeight = 4096;

    private readonly HashSet<char> _charsToRender = [];

    /// <summary>How many distinct characters a freshly reset sheet holds —
    /// printable ASCII, which the ctor seeds. Everything past this comes from
    /// the OPEN PROJECT's own text, so <see cref="ResetToSeedCharacters" />
    /// takes it back off on a project switch.</summary>
    public static int SeedCharacterCount { get; } = '~' - ' ' + 1;

    /// <summary>Distinct characters the sheet is packed for right now — test seam.</summary>
    public int RegisteredCharacterCount => _charsToRender.Count;
    private readonly string? _fontFamilyName;
    private readonly float _fontSize;
    private readonly Dictionary<char, GlyphMetrics> _glyphs = [];
    private readonly IVulkanRenderService _vulkan;

    /// <summary>
    ///     <paramref name="fontFamilyName" /> is an installed family name, or
    ///     <c>null</c> for the platform default. Do NOT pass a framework
    ///     sentinel such as Avalonia's <c>FontFamily.Default.Name</c>
    ///     ("$Default") — see <see cref="NormalizeFamilyName" />.
    /// </summary>
    public FontAtlas(IVulkanRenderService vulkan, string? fontFamilyName, float fontSize)
    {
        _vulkan = vulkan;
        _fontFamilyName = fontFamilyName;
        _fontSize = fontSize;

        for (var c = ' '; c <= '~'; c++) _charsToRender.Add(c);
        _charsToRender.Add('?');
    }

    private bool NeedsRebuild { get; set; } = true;

    public VulkanTexture? Texture { get; private set; }

    public void Dispose() => Texture?.Dispose();

    public void RegisterCharacters(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var added = false;
        foreach (var c in text)
        {
            // Invisible characters would only trigger phantom rebuilds, and a
            // surrogate half has no glyph of its own (see TryGetGlyph).
            if (char.IsControl(c) || char.IsSurrogate(c)) continue;

            if (_charsToRender.Add(c)) added = true;
        }

        if (added) NeedsRebuild = true;
    }

    /// <summary>
    ///     Drops every character registered from a project's content and goes
    ///     back to the ASCII seed the ctor lays down. Same rule and same reason
    ///     as <c>Text.ChartFontAtlas.ResetToSeedCharacters</c>: the set is only
    ///     ever added to, so without this every lyric, section name and phrase
    ///     name of every song opened in the session stayed in it for the life of
    ///     the process and the sheet, even grown, eventually stops fitting.
    ///     Driven by the <c>ActiveProject</c> notification (see
    ///     <c>docs/agents/ui/project-lifecycle.md</c>).
    /// </summary>
    public void ResetToSeedCharacters()
    {
        if (_charsToRender.Count == SeedCharacterCount) return;
        _charsToRender.Clear();
        for (var c = ' '; c <= '~'; c++) _charsToRender.Add(c);
        _charsToRender.Add('?');
        NeedsRebuild = true;
        _overflowWarned = false;
        LastOverflowWarning = null;
    }

    /// <summary>
    ///     Repacks the sheet when the character set changed.
    ///     <para>
    ///         <b>Build, swap, THEN dispose.</b> Calling
    ///         <c>Texture?.Dispose()</c> FIRST would free the image, the view,
    ///         the sampler <i>and the descriptor set</i>
    ///         (<see cref="VulkanTexture.Dispose" />) before rasterizing
    ///         16 MB and uploading. Any throw in between (Skia OOM, a failed
    ///         <c>LoadTexture</c>/<c>AllocateDescriptorSets</c> under VRAM
    ///         pressure) would leave <see cref="Texture" /> pointing at the disposed
    ///         object, and <c>TextBatcher.Flush</c> only null-checks — so the
    ///         next frame would bind a freed descriptor set and image.
    ///         <c>NeedsRebuild</c> would stay true, so it would retry EVERY frame: the
    ///         same "one sustained VRAM shortage leaks per retry" amplifier
    ///         <c>NativeResourceSet</c> was written to stop.
    ///     </para>
    /// </summary>
    public void RebuildIfNeeded()
    {
        if (!NeedsRebuild) return;

        // NEW: Wait for the GPU to finish its current frame to prevent driver TDR crashes!
        _vulkan.WaitDeviceIdle();

        var (pixels, glyphs, overflowed, height) = Rasterize(_charsToRender, _fontFamilyName, _fontSize);
        // May throw. Nothing above has touched the live sheet, so a throw here
        // leaves the previous atlas whole and still bindable.
        var next = _vulkan.LoadTexture(AtlasWidth, (uint)height, pixels, mipmapped: false);

        var previous = Texture;
        Texture = next;
        _glyphs.Clear();
        foreach (var kvp in glyphs) _glyphs[kvp.Key] = kvp.Value;
        previous?.Dispose();

        OverflowedGlyphCount = overflowed;
        if (OverflowWarning(overflowed, _overflowWarned, _charsToRender.Count) is { } warning)
        {
            _overflowWarned = true;
            LastOverflowWarning = warning;
            // Debug.WriteLine is COMPILED OUT of a Release build, so for as long
            // as a bare `break` was the only handling the shipped app rendered
            // '?' for Japanese lyrics and section names in total silence.
            // Console.Error survives — same channel Text.ChartFontAtlas uses.
            Console.Error.WriteLine(warning);
        }

        NeedsRebuild = false;
    }

    /// <summary>Glyphs the last pack could not fit. They fall back to
    /// <c>'?'</c> in <see cref="TryGetGlyph" />, so a nonzero count means the
    /// highway is rendering question marks — CJK first, because packing is
    /// code-point-sorted and ASCII therefore always survives.</summary>
    public int OverflowedGlyphCount { get; private set; }

    /// <summary>The warning text of the last overflow, or null if there has not
    /// been one. Cleared with the sheet — a project switch back to ASCII may
    /// well fit.</summary>
    public string? LastOverflowWarning { get; private set; }

    private bool _overflowWarned;

    /// <summary>
    ///     Pure: the once-per-sheet overflow warning, or <c>null</c> when there
    ///     is nothing to say. Split out so the DECISION is testable without
    ///     rasterizing a 2048² sheet. Mirrors
    ///     <c>Text.ChartFontAtlas.OverflowWarning</c>.
    /// </summary>
    internal static string? OverflowWarning(int overflowed, bool alreadyWarned, int registeredCharacters)
    {
        if (overflowed <= 0 || alreadyWarned) return null;
        return $"[FONT ATLAS] 3D atlas sheet is full ({AtlasWidth}x{MaxAtlasHeight}, " +
               $"{registeredCharacters} distinct chars registered): " +
               $"{overflowed} glyph(s) skipped and will render as '?'.";
    }

    /// <summary>
    ///     Rasterizes the glyph sheet with SkiaSharp, on the CALLING thread.
    ///     <para>
    ///         This must not wrap the whole packing loop in
    ///         <c>Dispatcher.UIThread.Invoke</c> to use Avalonia's
    ///         <c>RenderTargetBitmap</c>/<c>FormattedText</c>, because
    ///         <see cref="RebuildIfNeeded" /> runs on the RENDER thread, and the
    ///         UI thread blocks on that same render thread whenever the 3D view
    ///         is detached or popped out (<c>Preview3DComposedCanvas</c> →
    ///         <c>WaitForDrain(1500 ms)</c>). A rebuild in flight at that moment
    ///         deadlocks the two against each other — the visible symptom being
    ///         a 1.5 s freeze on every pop-out, and a permanent one if the wait
    ///         is re-entered.
    ///     </para>
    ///     <para>
    ///         Skia has no thread affinity, so there is nothing to marshal.
    ///         <c>Text.ChartFontAtlas</c> already rasterizes this way; the pixel
    ///         format is chosen to match what the texture upload wants
    ///         (RGBA8888, unpremultiplied — white glyphs on transparent), which
    ///         also avoids a per-pixel R/B swap over 16 MB.
    ///     </para>
    /// </summary>
    /// <summary>
    ///     Maps "no specific family" onto the null Skia treats as *the default
    ///     family, honouring the requested style*. A non-matching NAME instead
    ///     makes <c>SKTypeface.FromFamilyName</c> return null, and the
    ///     <c>?? SKTypeface.Default</c> fallback throws the weight away.
    ///     Avalonia's <c>FontFamily.Default.Name</c> is exactly such a
    ///     non-matching name.
    /// </summary>
    internal static string? NormalizeFamilyName(string? fontFamilyName)
    {
        if (string.IsNullOrWhiteSpace(fontFamilyName)) return null;
        // Avalonia's default-family sentinel; matches no installed family.
        return fontFamilyName.StartsWith('$') ? null : fontFamilyName;
    }

    /// <summary>
    ///     The pure rasterization half: characters in, glyph sheet + metrics
    ///     out. Split from the upload so it can be exercised without a GPU (and
    ///     without a UI thread), which is the property the deadlock fix turns
    ///     on. Safe to call from any thread.
    ///     <para>
    ///         Each character is drawn with <see cref="GlyphFontChain" />'s
    ///         choice: the bold primary for what it covers, the system fallback
    ///         font for the rest. Drawing a character the primary lacks with the
    ///         primary is what made every kana and kanji a <c>.notdef</c> box.
    ///     </para>
    ///     <para>
    ///         The sheet is <see cref="AtlasHeight" /> tall unless the packed
    ///         set needs more, and then <see cref="MaxAtlasHeight" /> — a
    ///         kanji-heavy song's lyrics fill the base sheet, and every
    ///         Latin-only chart keeps its byte-identical 2048² sheet.
    ///     </para>
    /// </summary>
    public static (byte[] Pixels, Dictionary<char, GlyphMetrics> Glyphs, int OverflowedGlyphCount, int Height)
        Rasterize(IEnumerable<char> charsToRender, string? fontFamilyName, float fontSize)
    {
        // Pack in code-point order so the sheet is a pure function of the
        // character SET. The caller's collection is a HashSet whose enumeration
        // order follows insertion, i.e. "which chart was opened first" — the
        // same glyphs then landed at different UVs between runs. A lone UTF-16
        // surrogate has no glyph of its own (TryGetGlyph answers for it).
        var packOrder = charsToRender.Where(c => !char.IsSurrogate(c)).ToArray();
        Array.Sort(packOrder);

        // "the platform default family" must be expressed as NULL, not as a
        // sentinel string. Avalonia's FontFamily.Default.Name is "$Default",
        // which matches no installed family, so SKTypeface.FromFamilyName
        // returned null and the `?? SKTypeface.Default` fallback silently
        // dropped the requested BOLD weight for every 3D in-world and HUD
        // label. Skia honours the style when the family name is null.
        using var typeface = SKTypeface.FromFamilyName(NormalizeFamilyName(fontFamilyName),
                                 SKFontStyleWeight.Bold,
                                 SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
                             ?? SKTypeface.Default;
        using var fonts = new GlyphFontChain(typeface, fontSize, GlyphFontChain.FallbackLocale(packOrder));

        // Skia draws from the BASELINE; the old Avalonia path drew from the
        // line box's top-left. Offsetting by the ascent keeps the glyph inside
        // the [y, y + rowHeight] cell the UVs describe.
        var metrics = fonts.Primary.Metrics;
        var ascent = (float)Math.Floor(metrics.Ascent);
        var descent = (float)Math.Ceiling(metrics.Descent);
        var rowHeight = descent - ascent;
        var baselineOffset = -ascent;

        var (cells, cellCount, overflowed) = Pack(packOrder, fonts, rowHeight);
        var height = SheetHeightFor(cellCount == 0 ? 0f : cells[cellCount - 1].Y + rowHeight);

        using var bitmap = new SKBitmap(AtlasWidth, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(255, 255, 255, 0));

        using var paint = new SKPaint();
        paint.Color = SKColors.White;
        paint.IsAntialias = true;

        const float halfPixelX = 0.5f / AtlasWidth;
        var halfPixelY = 0.5f / height;
        var glyphs = new Dictionary<char, GlyphMetrics>(cellCount);
        for (var i = 0; i < cellCount; i++)
        {
            var (c, font, x, y, width) = cells[i];
            glyphs[c] = new GlyphMetrics
            {
                U0 = x / AtlasWidth + halfPixelX,
                V0 = y / height + halfPixelY,
                U1 = (x + width) / AtlasWidth - halfPixelX,
                V1 = (y + rowHeight) / height - halfPixelY,
                Width = width,
                Height = rowHeight,
                AdvanceX = width
            };

            if (c != ' ') canvas.DrawText(c.ToString(), x, y + baselineOffset, SKTextAlign.Left, font, paint);
        }

        canvas.Flush();

        var pixels = new byte[AtlasWidth * height * 4];
        System.Runtime.InteropServices.Marshal.Copy(bitmap.GetPixels(), pixels, 0, pixels.Length);

        return (pixels, glyphs, overflowed, height);
    }

    /// <summary>Pure: the sheet height for packed content
    /// <paramref name="usedHeight" /> texels tall. Vulkan guarantees
    /// <c>maxImageDimension2D</c> ≥ 4096, so the grown sheet binds on every
    /// device.</summary>
    internal static int SheetHeightFor(float usedHeight) => usedHeight <= AtlasHeight ? AtlasHeight : MaxAtlasHeight;

    /// <summary>
    ///     Lays the cells out left to right, row by row, on a sheet up to
    ///     <see cref="MaxAtlasHeight" /> tall. A glyph past the bottom would get
    ///     V &gt; 1 and sample unrelated rows, so it is skipped —
    ///     <see cref="TryGetGlyph" /> falls back to <c>'?'</c> — and COUNTED: a
    ///     bare <c>break</c> once reported nothing, and because packing is
    ///     code-point-sorted the glyphs that fall off are always CJK.
    /// </summary>
    private static ((char C, SKFont Font, float X, float Y, float Width)[] Cells, int Count, int Overflowed) Pack(
        char[] packOrder, GlyphFontChain fonts, float rowHeight)
    {
        const float padding = 16f;
        var cells = new (char C, SKFont Font, float X, float Y, float Width)[packOrder.Length];
        var count = 0;
        var overflowed = 0;
        float x = 0;
        float y = 0;

        // A space has no ink to measure, so it borrows a glyph's advance —
        // same 0.4 factor the previous implementation used.
        var spaceWidth = fonts.Primary.MeasureText("a") * 0.4f;

        foreach (var c in packOrder)
        {
            var font = fonts.For(c);
            var width = c == ' ' ? spaceWidth : font.MeasureText(c.ToString());

            if (x + width + padding > AtlasWidth)
            {
                x = 0;
                y += rowHeight + padding;
            }

            if (y + rowHeight > MaxAtlasHeight)
            {
                overflowed++;
                continue;
            }

            cells[count++] = (c, font, x, y, width);
            x += width + padding;
        }

        return (cells, count, overflowed);
    }

    public bool TryGetGlyph(char c, out GlyphMetrics metrics)
    {
        if (_glyphs.TryGetValue(c, out metrics)) return true;
        // An astral character (emoji) is a surrogate PAIR and the sheet is
        // keyed by UTF-16 unit, so it cannot be drawn: the high half shows ONE
        // '?' and the low half takes no space, instead of two marks.
        if (char.IsLowSurrogate(c)) return false;
        return _glyphs.TryGetValue('?', out metrics);
    }
}
