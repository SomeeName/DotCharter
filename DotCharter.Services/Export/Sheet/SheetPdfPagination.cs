namespace DotCharter.Services.Export.Sheet;

/// <summary>
///     Where each rendered system row lands on the printed page. Pure and
///     Skia-free on purpose: the notation raster behind it is native, so this is
///     the only half of <see cref="SheetPdfExporter" />'s page flow that can be
///     gated.
///     <para>
///         <b>A row taller than the printable area must be handled, not
///         LOST.</b> A loop that breaks to a fresh page
///         when the row does not fit under the cursor, guarded by
///         <c>cursorY &gt; margin</c>, finds the guard false on the fresh page, where
///         the cursor IS the margin, and blits an oversized row
///         from the top margin straight off the bottom of the sheet.
///         Nothing warns; the PDF just ends each such system early. A
///         many-staff score (7-/8-string with both staves), a large display
///         scale or a narrow page all reach it, and a chart that reaches it once
///         reaches it on EVERY system.
///     </para>
///     <para>
///         The fix is to scale the row down to the printable height rather than
///         to split it. Splitting an image row cuts through noteheads, beams and
///         the tab staff at whatever y the page happens to end on, which is worse
///         than a smaller-but-whole system — and the row is a bitmap here, so
///         there is no re-layout to ask for.
///     </para>
/// </summary>
internal static class SheetPdfPagination
{
    /// <summary>One rendered system row, in RASTER pixels (the units
    /// <c>SheetMusicRenderer.RenderResult</c> reports).</summary>
    internal readonly record struct Row(double X, double Width, double Height);

    /// <summary>Where one row goes, in PDF points. <see cref="Page" /> counts from
    /// 0 = the page the header is on.</summary>
    internal readonly record struct Placement(int Row, int Page, float X, float Y, float Width, float Height);

    /// <summary>
    ///     Flows <paramref name="rows" /> down the page.
    /// </summary>
    /// <param name="rows">System rows, top-to-bottom, in raster pixels.</param>
    /// <param name="pxToPt">Raster pixels → PDF points.</param>
    /// <param name="marginPt">Left margin, and the top of every page after the first.</param>
    /// <param name="pageBottomPt">The first y a row may not cross.</param>
    /// <param name="firstCursorPt">Where page 0 starts — under the title block.</param>
    /// <param name="rowGapPt">Breathing room between two rows on one page.</param>
    internal static List<Placement> Lay(
        IReadOnlyList<Row> rows,
        double pxToPt,
        float marginPt,
        float pageBottomPt,
        float firstCursorPt,
        float rowGapPt)
    {
        var placements = new List<Placement>(rows.Count);
        var printable = pageBottomPt - marginPt;
        var page = 0;
        var cursorY = firstCursorPt;

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var hPt = (float)(row.Height * pxToPt);
            var wPt = (float)(row.Width * pxToPt);
            if (hPt <= 0) continue;

            // A row no whole page could hold is shrunk to exactly one page — with
            // its width and its x offset, so the system keeps its proportions and
            // its indent instead of being squashed or clipped.
            var shrink = printable > 0 && hPt > printable ? printable / hPt : 1f;

            // Break BEFORE placing, and only when there is already something on
            // this page: a fresh page cannot help a row that starts at the margin.
            if (cursorY + hPt * shrink > pageBottomPt && cursorY > marginPt + 0.5f)
            {
                page++;
                cursorY = marginPt;
            }

            var x = marginPt + (float)(row.X * pxToPt) * shrink;
            placements.Add(new Placement(i, page, x, cursorY, wPt * shrink, hPt * shrink));
            cursorY += hPt * shrink + rowGapPt;
        }

        return placements;
    }
}
