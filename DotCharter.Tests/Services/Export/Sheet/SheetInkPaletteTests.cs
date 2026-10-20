using System.Text.RegularExpressions;
using AlphaTab.Model;
using DotCharter.Services.Export.Sheet;

namespace DotCharter.Tests.Services.Export.Sheet;

/// <summary>
///     The sheet PDF is engraved by the SAME <see cref="SheetMusicRenderer" />
///     that draws the on-screen strip, and that renderer hard-coded one palette:
///     <c>#E8E8E8</c> glyphs, chosen for the dark backdrop <c>SheetDrawOp</c>
///     fills behind a chunk. The chunks are TRANSPARENT, and
///     <c>SheetPdfExporter.WritePdf</c> never fills a page background — so every
///     staff line, note head, stem and fret digit landed on white paper at about
///     1.15:1 while the exporter's own title block, drawn in
///     <c>SKColors.Black</c>, came out fine.
///     <para>
///         Nothing caught it: <c>SheetPdfExporterTests</c> says outright that the
///         notation raster goes through AlphaSkia (native) and "cannot be
///         produced deterministically headless, so it is verified manually" —
///         only the page geometry and header lines are gated. The palette
///         DECISION is pure, so that is what this pins.
///     </para>
/// </summary>
public sealed class SheetInkPaletteTests
{
    /// <summary>WCAG relative luminance, then the standard contrast ratio. The
    /// numbers below are ratios against the ground each ink is read on.</summary>
    private static double Luminance(Color c)
    {
        static double Channel(int v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel((int)c.R) + 0.7152 * Channel((int)c.G) + 0.0722 * Channel((int)c.B);
    }

    private static double Contrast(Color ink, Color ground)
    {
        var a = Luminance(ink);
        var b = Luminance(ground);
        var (hi, lo) = a > b ? (a, b) : (b, a);
        return (hi + 0.05) / (lo + 0.05);
    }

    [Fact]
    public void PrintInk_IsReadableOnPaper()
    {
        var (glyph, dim) = SheetMusicRenderer.InkFor(SheetMusicRenderer.InkPalette.Print);

        // The pre-fix value scored 1.15 here, which is why the notation was
        // invisible in every exported PDF. 4.5 is WCAG AA for body text.
        Assert.True(Contrast(glyph, SheetMusicRenderer.PrintGround) >= 4.5,
            $"notation ink on paper is only {Contrast(glyph, SheetMusicRenderer.PrintGround):F2}:1");
        Assert.True(Contrast(dim, SheetMusicRenderer.PrintGround) >= 4.5,
            $"bar numbers on paper are only {Contrast(dim, SheetMusicRenderer.PrintGround):F2}:1");
    }

    [Fact]
    public void ScreenInk_IsUnchanged_AndStillReadableOnTheDarkBackdrop()
    {
        var (glyph, dim) = SheetMusicRenderer.InkFor(SheetMusicRenderer.InkPalette.Screen);

        // Byte-for-byte the values the strip has always drawn with: the fix must
        // not have moved the on-screen appearance while making print work.
        Assert.Equal(232, (int)glyph.R);
        Assert.Equal(170, (int)dim.R);
        Assert.True(Contrast(glyph, SheetMusicRenderer.ScreenGround) >= 4.5);
        Assert.True(Contrast(dim, SheetMusicRenderer.ScreenGround) >= 4.5);
    }

    [Fact]
    public void TheTwoPalettes_AreNotTheSameInk()
        => Assert.NotEqual(
            (int)SheetMusicRenderer.InkFor(SheetMusicRenderer.InkPalette.Print).Glyph.R,
            (int)SheetMusicRenderer.InkFor(SheetMusicRenderer.InkPalette.Screen).Glyph.R);

    /// <summary>
    ///     …and the exporter actually ASKS for it. Without this the palette could
    ///     be perfect and the PDF still white-on-white, which is the state this
    ///     whole class exists to end.
    /// </summary>
    [Fact]
    public void ThePdfExporter_RendersWithThePrintPalette()
    {
        var source = UiSourceTree.ReadAllText(Path.Combine(
            SourceRoot(), "DotCharter.Services", "Export", "Sheet", "SheetPdfExporter.cs"));

        var call = Regex.Match(source, @"RenderPagedAsync\((?:[^()]|\([^()]*\))*\)",
            RegexOptions.Singleline);
        Assert.True(call.Success, "SheetPdfExporter no longer calls RenderPagedAsync — repoint this lint.");
        Assert.Contains("InkPalette.Print", call.Value, StringComparison.Ordinal);
    }

    /// <summary>The on-screen views must NOT ask for it — print ink on the dark
    /// backdrop is the same fault mirrored.</summary>
    [Fact]
    public void TheOnScreenSheetView_DoesNotAskForPrintInk()
    {
        var source = SheetMusicViewSource.ReadAllText();

        Assert.Contains("RenderPagedAsync(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("InkPalette.Print", source, StringComparison.Ordinal);
    }

    private static string SourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DotCharter.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
