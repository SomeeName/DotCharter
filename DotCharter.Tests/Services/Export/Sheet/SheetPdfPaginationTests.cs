using DotCharter.Services.Export.Sheet;

namespace DotCharter.Tests.Services.Export.Sheet;

/// <summary>
///     The page flow of the sheet PDF. Pure — the notation raster it places is
///     native and cannot be produced deterministically headless, which is exactly
///     why the flow was extracted from <c>SheetPdfExporter.WritePdf</c>.
/// </summary>
public class SheetPdfPaginationTests
{
    // A4 at half-inch margins, in points.
    private const float Margin = 36f;
    private const float PageBottom = 841.89f - 36f;
    private const float Gap = 6f;
    private const float Printable = PageBottom - Margin;

    private static List<SheetPdfPagination.Placement> Lay(
        IEnumerable<double> rowHeightsPt, float firstCursor = 150f) =>
        SheetPdfPagination.Lay(
            rowHeightsPt.Select(h => new SheetPdfPagination.Row(0, 500, h)).ToList(),
            pxToPt: 1.0, Margin, PageBottom, firstCursor, Gap);

    [Fact]
    public void RowsThatFit_StayOnOnePage_UnderTheHeader()
    {
        var placed = Lay(new double[] { 100, 100, 100 });

        Assert.Equal(3, placed.Count);
        Assert.All(placed, p => Assert.Equal(0, p.Page));
        Assert.Equal(150f, placed[0].Y);
        Assert.Equal(150f + 100 + Gap, placed[1].Y);
        Assert.Equal(Margin + (float)(0 * 1.0), placed[0].X);
    }

    [Fact]
    public void ARowThatWouldCrossTheBottom_StartsAFreshPageAtTheMargin()
    {
        var placed = Lay(new double[] { 600, 200 });

        Assert.Equal(0, placed[0].Page);
        Assert.Equal(1, placed[1].Page);
        Assert.Equal(Margin, placed[1].Y);
    }

    /// <summary>
    ///     THE regression. A system row taller than a whole printable page used
    ///     to be drawn from the top margin straight off the bottom of the sheet:
    ///     the break was guarded by "there is already something on this page", so
    ///     on the fresh page the guard was false and the overflow was simply
    ///     lost, silently, on every system of such a chart.
    /// </summary>
    [Fact]
    public void ARowTallerThanThePage_IsScaledToFit_NeverDrawnOffTheBottom()
    {
        var placed = Lay(new double[] { 2000 });

        var only = Assert.Single(placed);
        Assert.True(only.Y + only.Height <= PageBottom + 0.01f,
            $"the row runs to {only.Y + only.Height}pt, past the {PageBottom}pt page bottom");
        Assert.Equal(Printable, only.Height, 2);
        // Scaled, not squashed: the aspect ratio survives.
        Assert.Equal(500.0 * (Printable / 2000.0), only.Width, 2);
    }

    [Fact]
    public void NoRowEverCrossesTheBottom_WhateverTheMix()
    {
        var placed = Lay(new double[] { 120, 900, 3000, 40, 700, 60 });

        Assert.Equal(6, placed.Count);
        Assert.All(placed, p => Assert.True(p.Y + p.Height <= PageBottom + 0.01f,
            $"row {p.Row} runs to {p.Y + p.Height}pt on page {p.Page}"));
    }

    /// <summary>Pages only ever move forward, one at a time — the draw loop walks
    /// <c>BeginPage</c> up to each placement's page and cannot skip one.</summary>
    [Fact]
    public void PageNumbers_AreNonDecreasingAndGapless()
    {
        var placed = Lay(new double[] { 300, 300, 300, 300, 300, 300, 300 });

        var page = 0;
        foreach (var p in placed)
        {
            Assert.InRange(p.Page, page, page + 1);
            page = p.Page;
        }

        Assert.True(page >= 2, "seven half-page rows must not all land on one page");
    }

    [Fact]
    public void ZeroHeightRows_AreDroppedWithoutAdvancingTheCursor()
    {
        var placed = Lay(new double[] { 100, 0, 100 });

        Assert.Equal(2, placed.Count);
        Assert.Equal(0, placed[0].Row);
        Assert.Equal(2, placed[1].Row);
        Assert.Equal(150f + 100 + Gap, placed[1].Y);
    }

    /// <summary>A scaled row keeps its indent: the x offset is in the same raster
    /// units as the width and has to shrink with it, or a centred system slides
    /// right as it shrinks.</summary>
    [Fact]
    public void AScaledRow_ScalesItsXOffsetToo()
    {
        var placed = SheetPdfPagination.Lay(
            [new SheetPdfPagination.Row(100, 500, 2000)],
            pxToPt: 1.0, Margin, PageBottom, 150f, Gap);

        var shrink = Printable / 2000f;
        Assert.Equal(Margin + 100f * shrink, Assert.Single(placed).X, 2);
    }
}
