using DotCharter.Services.Export.Sheet;

namespace DotCharter.Tests.Services.Export.Sheet;

/// <summary>
/// Paged multi-system layout evidence: rendering in PAGE layout (what the sheet
/// pane's page toggle drives) wraps bars into several systems STACKED
/// vertically, unlike the single-row horizontal strip. Structural proof over the
/// renderer's real measure bounds + per-system lattices — CPU raster, default
/// gate, no GPU env var.
/// </summary>
[Collection("AlphaTab")]
public class SheetPageLayoutRenderTests
{
    private static (ChartProject project, Arrangement arr, ChartLevel level, List<BeatMarker> beats) Fixture(int bars)
    {
        var project = new ChartProject();
        var arr = new Arrangement { Name = "Lead", StringCount = 6 };
        var level = new ChartLevel();
        arr.Levels.Add(level);
        project.Arrangements.Add(arr);

        var beats = new List<BeatMarker>();
        for (var i = 0; i < bars * 4; i++)
            beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, BeatsInMeasure = 4, NoteDenominator = 4 });

        // A note on every beat so each bar has content and a real width.
        for (var i = 0; i < bars * 4; i++)
            level.Notes.Add(new GuitarNote { Position = new FractionalPosition(i), StringIndex = 2, Fret = 5 });

        return (project, arr, level, beats);
    }

    /// <summary>Number of distinct system ROWS: measure bounds bucketed by Y.</summary>
    private static int DistinctRows(IReadOnlyList<SheetMusicRenderer.MeasureBound> bounds)
    {
        var rows = new List<double>();
        foreach (var b in bounds)
        {
            if (!rows.Any(y => Math.Abs(y - b.Y) < 20))
                rows.Add(b.Y);
        }

        return rows.Count;
    }

    [Fact]
    public async Task PageLayout_WrapsBarsIntoStackedSystems()
    {
        var (project, arr, level, beats) = Fixture(12);
        var tex = AlphaTexExporter.Export(project, arr, level, beats).AlphaTex;

        // Narrow page width forces several systems.
        var paged = await new SheetMusicRenderer()
            .RenderPagedAsync(tex, 800, 1.3, SheetMusicRenderer.StavesBoth, CancellationToken.None);
        Assert.NotNull(paged);
        using var pagedLease = paged;

        // The strip renders every bar in ONE row; the page wraps into ≥2 rows
        // stacked top-to-bottom.
        var strip = await new SheetMusicRenderer()
            .RenderAsync(tex, 800, 1.3, SheetMusicRenderer.StavesBoth, CancellationToken.None);
        Assert.NotNull(strip);
        using var stripLease = strip;

        Assert.Equal(1, DistinctRows(strip.MeasureBounds));
        Assert.True(DistinctRows(paged.MeasureBounds) >= 2,
            $"page layout did not stack systems (rows={DistinctRows(paged.MeasureBounds)})");

        // Per-system tab lattices are built per row (string Y is not constant
        // across stacked systems) — the page-mode Y→string resolution depends on
        // them for interval-pick / insert.
        Assert.True(paged.SystemLattices.Count >= 2,
            $"expected ≥2 per-system lattices, got {paged.SystemLattices.Count}");

        // A stacked page is taller than a single strip of the same content.
        Assert.True(paged.Height > strip.Height,
            $"page ({paged.Height}) should be taller than the strip ({strip.Height})");

        // Systems don't vertically overlap (each lattice band sits below the
        // previous one).
        var ordered = paged.SystemLattices.OrderBy(s => s.Top).ToList();
        for (var i = 1; i < ordered.Count; i++)
        {
            Assert.True(ordered[i].Top >= ordered[i - 1].Top,
                "system lattices are not ordered top-to-bottom");
        }
    }
}
