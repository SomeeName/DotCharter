using AlphaTab;
using AlphaTab.Importer;
using AlphaTab.Model;
using DotCharter.Tests.Rendering.Headless;
using DotCharter.Services.Export.Sheet;
using SkiaSharp;

namespace DotCharter.Tests.Services.Export.Sheet;

/// <summary>
/// Duration EDITING evidence: changing a note's authored
/// <see cref="GuitarNote.NotationDuration"/> (what the sheet duration palette /
/// the +/− keys write) really changes the RENDERED glyph. Same single note,
/// two durations, must raster to visibly different frames — proving the hint
/// reaches the exporter, survives the exact-beat-grid quantization, and paints.
/// CPU raster (AlphaSkia), default gate — no GPU env var, structural tolerance.
/// </summary>
[Collection("AlphaTab")]
public class SheetDurationRenderTests
{
    private static (ChartProject project, Arrangement arr, ChartLevel level, List<BeatMarker> beats) Fixture()
    {
        var project = new ChartProject();
        var arr = new Arrangement { Name = "Lead", StringCount = 6 };
        var level = new ChartLevel();
        arr.Levels.Add(level);
        project.Arrangements.Add(arr);

        var beats = new List<BeatMarker>();
        for (var i = 0; i < 4; i++)
            beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, BeatsInMeasure = 4, NoteDenominator = 4 });

        // One isolated note at the bar start — nothing else, so a whole-note hint
        // is fully expressible (not capped by a following onset).
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(0), StringIndex = 2, Fret = 5 });
        return (project, arr, level, beats);
    }

    private static Duration FirstBeatDuration(string tex)
    {
        var importer = new AlphaTexImporter();
        importer.InitFromString(tex, new Settings());
        return importer.ReadScore().Tracks[0].Staves[0].Bars[0].Voices[0].Beats[0].Duration;
    }

    [Fact]
    public async Task ChangingNotationDuration_ChangesTheRenderedGlyph()
    {
        var (project, arr, level, beats) = Fixture();
        var note = level.Notes[0];

        note.NotationDuration = new Fraction(1, 2); // eighth
        var eighthTex = AlphaTexExporter.Export(project, arr, level, beats).AlphaTex;
        var eighth = await Render(eighthTex);
        using var eighthLease = eighth.Result;

        note.NotationDuration = new Fraction(4, 1); // whole
        var wholeTex = AlphaTexExporter.Export(project, arr, level, beats).AlphaTex;
        var whole = await Render(wholeTex);
        using var wholeLease = whole.Result;

        // Exact proof the hint reached the exporter as the right note value.
        Assert.Equal(Duration.Eighth, FirstBeatDuration(eighthTex));
        Assert.Equal(Duration.Whole, FirstBeatDuration(wholeTex));

        // Both frames drew something.
        Assert.True(GoldenFrame.NonBlackRatio(eighth.Bgra) > 0.01);
        Assert.True(GoldenFrame.NonBlackRatio(whole.Bgra) > 0.01);

        // And the RASTER differs — the changed note value repaints the staff
        // (open whole-note head + no rests vs. a flagged eighth + rest fill).
        var w = Math.Max(eighth.Width, whole.Width);
        var h = Math.Max(eighth.Height, whole.Height);
        var a = Compose(eighth.Result, w, h);
        var b = Compose(whole.Result, w, h);
        var diff = GoldenFrame.DifferenceRatio(a, b);
        Assert.True(diff > 0.003, $"duration change barely altered the frame (diff {diff:P3})");
    }

    private static async Task<(byte[] Bgra, int Width, int Height, SheetMusicRenderer.RenderResult Result)>
        Render(string tex)
    {
        var result = await new SheetMusicRenderer()
            .RenderAsync(tex, 1200, 1.3, SheetMusicRenderer.StavesBoth, CancellationToken.None);
        Assert.NotNull(result);
        var bgra = Compose(result, result.Width, result.Height);
        return (bgra, result.Width, result.Height, result);
    }

    private static byte[] Compose(SheetMusicRenderer.RenderResult result, int width, int height)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        surface.Canvas.Clear(SKColors.Black);
        foreach (var chunk in result.Chunks)
            surface.Canvas.DrawImage(chunk.Image, (float)chunk.X, (float)chunk.Y, SKSamplingOptions.Default);
        using var img = surface.Snapshot();
        using var bitmap = new SKBitmap(info);
        Assert.True(img.ReadPixels(bitmap.PeekPixels(), 0, 0));
        return bitmap.Bytes;
    }
}
