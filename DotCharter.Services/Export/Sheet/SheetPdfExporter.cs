using System.Text;
using DotCharter.Localization;
using SkiaSharp;

namespace DotCharter.Services.Export.Sheet;

/// <summary>Physical page size for <see cref="SheetPdfExporter"/>.</summary>
public enum SheetPageFormat
{
    A4,
    Letter
}

/// <summary>
/// Prints the active arrangement level to a paginated PDF: a title block +
/// tuning legend on page 1, then the notation flowed across A4/Letter pages.
///
/// The notation itself is the same AlphaTab + AlphaSkia raster the on-screen
/// strip uses (rendered here in PAGE layout so systems wrap at barlines), drawn
/// into a <see cref="SKDocument"/> PDF via SkiaSharp — no extra dependency.
/// </summary>
public sealed class SheetPdfExporter(ILocalizationService localization)
{
    // 1 pt = 1/72 inch. Half-inch margins; a slightly deeper top margin on page 1
    // is added by the title block itself.
    private const float MarginPt = 36f;

    // Rasterize the notation at 2x the page width so the embedded bitmaps stay
    // crisp when the PDF is printed / zoomed, then draw them down to page points.
    private const double RenderOversample = 2.0;

    /// <summary>
    /// Writes the PDF to <paramref name="path"/> ATOMICALLY: the render streams
    /// into a sibling <c>.tmp</c> and only a complete document is moved into
    /// place.
    /// <para>The call site must not open the user's chosen path with
    /// <see cref="FileMode.Create"/> and hand that stream straight to
    /// <see cref="ExportAsync"/>. <c>Create</c> truncates on OPEN, so anything
    /// that threw afterwards — a chart the exporter chokes on, a cancelled
    /// render, a full disk, a read-only path failing mid-write — would leave a
    /// truncated or zero-byte ".pdf" sitting there looking like a finished
    /// export. Worse, exporting OVER a good PDF would destroy it before the work
    /// that was going to replace it had even started.</para>
    /// <para><see cref="AtomicFile.ReplaceWithRetry"/> is the sanctioned swap
    /// (bounded exponential retry — a plain <c>File.Move</c> throws on the first
    /// transient AV/indexer lock and orphans the temp file).</para>
    /// </summary>
    public async Task ExportToFileAsync(
        ChartProject? project,
        Arrangement arrangement,
        ChartLevel level,
        IList<BeatMarker> beats,
        int staves,
        SheetPageFormat format,
        string path,
        CancellationToken cancellationToken = default)
    {
        // Nothing below touches the disk before the token is honoured: an export
        // the user already cancelled must not so much as CREATE the folder.
        cancellationToken.ThrowIfCancellationRequested();
        RefusePlayOnly(project, arrangement);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temp = path + AtomicFile.TempSuffix;
        try
        {
            // Async handle: File.Create opens a SYNCHRONOUS stream, so every
            // await inside ExportAsync would still write on the calling thread.
            await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None,
                             bufferSize: 4096, useAsync: true))
            {
                await ExportAsync(project, arrangement, level, beats, staves, format, stream, cancellationToken)
                    .ConfigureAwait(false);
            }

            AtomicFile.ReplaceWithRetry(temp, path);
        }
        catch
        {
            // Never leave the half-written temp behind next to the real file;
            // the throw below is the real news.
            BestEffortDelete.File(temp);
            throw;
        }
    }

    /// <summary>Play-only content is never exported. The sheet PANE draws the same
    /// chart through <c>AlphaTexExporter</c> and stays unguarded: that is viewing.</summary>
    private static void RefusePlayOnly(ChartProject? project, Arrangement arrangement)
    {
        PlayOnlyExportGuard.ThrowIfPlayOnly(project, "Sheet PDF export");
        PlayOnlyExportGuard.ThrowIfPlayOnly(arrangement, "Sheet PDF export");
    }

    public async Task ExportAsync(
        ChartProject? project,
        Arrangement arrangement,
        ChartLevel level,
        IList<BeatMarker> beats,
        int staves,
        SheetPageFormat format,
        Stream output,
        CancellationToken cancellationToken = default)
    {
        RefusePlayOnly(project, arrangement);
        var (pageW, _) = PageSizePt(format);
        var contentW = pageW - MarginPt * 2;

        // OFF the caller's thread. The caller is the UI thread (the File menu
        // command), AlphaTexExporter.Export walks the whole level and builds the
        // document synchronously, and everything before the first await ran
        // there — a long chart froze the window before the render had even
        // started, while both sibling exporters (Guitar Pro, EOF) already do
        // their serialization on a worker. The token is passed to Task.Run as
        // well as into the body, so an export cancelled before it starts does no
        // work at all.
        var export = await Task.Run(
            () => AlphaTexExporter.Export(project, arrangement, level, beats), cancellationToken)
            .ConfigureAwait(false);
        var result = await new SheetMusicRenderer()
            .RenderPagedAsync(export.AlphaTex, contentW * RenderOversample, 1.1, staves, cancellationToken,
                SheetMusicRenderer.InkPalette.Print)
            .ConfigureAwait(false);

        try
        {
            WritePdf(result, BuildHeaderLines(project, arrangement, level, localization), format, output);
        }
        finally
        {
            result?.Release();
        }
    }

    internal static (float Width, float Height) PageSizePt(SheetPageFormat format) => format switch
    {
        SheetPageFormat.Letter => (612f, 792f),
        _ => (595.276f, 841.89f)
    };

    /// <summary>The title/subtitle/info lines printed in the page-1 header. Pure
    /// (no Skia) so it can be unit-tested.</summary>
    internal static (string Title, string Subtitle, string Info) BuildHeaderLines(
        ChartProject? project, Arrangement arrangement, ChartLevel level, ILocalizationService localization)
    {
        var title = project?.Chart.SongName;
        if (string.IsNullOrWhiteSpace(title)) title = localization["Project_Untitled"];

        var subtitle = project?.Chart.Artist ?? string.Empty;

        var info = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(arrangement.Name)) info.Append(arrangement.Name);
        AppendPart(info, BuildTuningLegend(arrangement, localization));
        if (arrangement.Capo > 0) AppendPart(info, localization.Format("SheetPdf_Header_Capo", arrangement.Capo));
        if (arrangement.Levels.Count > 1)
            AppendPart(info, localization.Format("SheetPdf_Header_Difficulty", level.DifficultyIndex));

        return (title.Trim(), subtitle.Trim(), info.ToString());
    }

    /// <summary>"Tuning: E A D G B E" (low → high). Pure, unit-testable.</summary>
    internal static string BuildTuningLegend(Arrangement arrangement, ILocalizationService localization)
    {
        var tuning = arrangement.Tuning;
        if (tuning == null || tuning.Length == 0) return string.Empty;
        return localization.Format("SheetPdf_Header_Tuning", string.Join(' ', tuning));
    }

    private static void AppendPart(StringBuilder sb, string part)
    {
        if (string.IsNullOrEmpty(part)) return;
        if (sb.Length > 0) sb.Append("   ·   ");
        sb.Append(part);
    }

    private static void WritePdf(
        SheetMusicRenderer.RenderResult? result,
        (string Title, string Subtitle, string Info) header,
        SheetPageFormat format,
        Stream output)
    {
        var (pageW, pageH) = PageSizePt(format);
        var contentW = pageW - MarginPt * 2;
        var pageBottom = pageH - MarginPt;

        var typeface = SKTypeface.FromFamilyName("Arial") ?? SKTypeface.Default;
        using var titleFont = new SKFont(typeface, 20f);
        using var subFont = new SKFont(typeface, 12f);
        using var infoFont = new SKFont(typeface, 10f);
        using var ink = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        using var dim = new SKPaint { Color = new SKColor(90, 90, 90), IsAntialias = true };
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);

        using var doc = SKDocument.CreatePdf(output);

        var canvas = doc.BeginPage(pageW, pageH);
        var cursorY = DrawHeader(canvas, header,
            titleFont, subFont, infoFont, ink, dim, pageW);

        if (result == null || result.Chunks.Count == 0)
        {
            canvas.DrawText("No notation to display.", MarginPt, cursorY + 16, SKTextAlign.Left, infoFont, dim);
            doc.EndPage();
            doc.Close();
            return;
        }

        var pxToPt = result.Width > 0 ? contentW / result.Width : 1.0 / RenderOversample;

        // Chunks are the score's system rows in top-to-bottom order.
        var chunks = result.Chunks.OrderBy(c => c.Y).ThenBy(c => c.X).ToList();
        const float rowGapPt = 6f;

        // The flow itself is pure and gated — see SheetPdfPagination for why a
        // row taller than the printable area must be SCALED here and not simply
        // drawn off the bottom of the sheet.
        var rows = chunks.Select(c => new SheetPdfPagination.Row(c.X, c.Width, c.Height)).ToList();
        var placements = SheetPdfPagination.Lay(rows, pxToPt, MarginPt, pageBottom, cursorY, rowGapPt);

        var currentPage = 0;
        foreach (var p in placements)
        {
            while (currentPage < p.Page)
            {
                doc.EndPage();
                canvas = doc.BeginPage(pageW, pageH);
                currentPage++;
            }

            var dst = new SKRect(p.X, p.Y, p.X + p.Width, p.Y + p.Height);
            canvas.DrawImage(chunks[p.Row].Image, dst, sampling);
        }

        doc.EndPage();
        doc.Close();
    }

    private static float DrawHeader(
        SKCanvas canvas,
        (string Title, string Subtitle, string Info) header,
        SKFont titleFont,
        SKFont subFont,
        SKFont infoFont,
        SKPaint ink,
        SKPaint dim,
        float pageW)
    {
        var (title, subtitle, info) = header;
        var y = MarginPt + titleFont.Size;

        canvas.DrawText(title, MarginPt, y, SKTextAlign.Left, titleFont, ink);
        y += subFont.Size + 6;

        if (!string.IsNullOrEmpty(subtitle))
        {
            canvas.DrawText(subtitle, MarginPt, y, SKTextAlign.Left, subFont, dim);
            y += infoFont.Size + 6;
        }

        if (!string.IsNullOrEmpty(info))
        {
            canvas.DrawText(info, MarginPt, y, SKTextAlign.Left, infoFont, dim);
            y += 8;
        }

        // Divider rule under the title block.
        y += 4;
        using var rule = new SKPaint { Color = new SKColor(180, 180, 180), StrokeWidth = 0.75f };
        canvas.DrawLine(MarginPt, y, pageW - MarginPt, y, rule);
        return y + 14;
    }
}
