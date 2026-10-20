using DotCharter.Tests.Rendering.Headless;
using DotCharter.Services.Export.Sheet;
using SkiaSharp;

namespace DotCharter.Tests.Services.Export.Sheet;

/// <summary>
/// Golden-frame (structural) verification that multi-voice charts really reach
/// the rendered sheet: the same chart with and without a notation sidecar
/// voice must produce visibly different frames with MORE ink (the second
/// rhythm line), and AlphaTab's beat bounds must keep the 1:1 per-bar zip with
/// the exporter's voice-0 beat map — the invariant that turns a click on a
/// glyph into the right domain note. CPU raster (AlphaSkia), so it runs in the
/// default gate — no GPU env var needed (structural tolerances, never
/// byte-exact, per the golden-frame policy).
/// </summary>
[Collection("AlphaTab")]
public class SheetMultiVoiceRenderTests
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

        // Chart voice: straight quarters on the G string.
        for (var i = 0; i < bars * 4; i++)
            level.Notes.Add(new GuitarNote { Position = new FractionalPosition(i), StringIndex = 2, Fret = 5 });

        return (project, arr, level, beats);
    }

    private static void AddSidecar(Arrangement arr, int bars)
    {
        var voice = new NotationVoice { VoiceIndex = 1 };
        for (var bar = 0; bar < bars; bar++)
        {
            // Two halves per bar on the low E — a rhythm line clearly distinct
            // from the chart voice's quarters.
            voice.Notes.Add(new GuitarNote
            {
                Position = new FractionalPosition(bar * 4), StringIndex = 0, Fret = 3,
                NotationDuration = new Fraction(2, 1)
            });
            voice.Notes.Add(new GuitarNote
            {
                Position = new FractionalPosition(bar * 4 + 2), StringIndex = 0, Fret = 5,
                NotationDuration = new Fraction(2, 1)
            });
        }

        arr.NotationVoices.Add(voice);
    }

    private static async Task<(byte[] Bgra, int Width, int Height, SheetMusicRenderer.RenderResult Result)>
        Render(string tex)
    {
        var result = await new SheetMusicRenderer()
            .RenderAsync(tex, 1400, 1.3, SheetMusicRenderer.StavesBoth, CancellationToken.None);
        Assert.NotNull(result);

        using var surface = SKSurface.Create(new SKImageInfo(result.Width, result.Height));
        surface.Canvas.Clear(SKColors.Black);
        foreach (var chunk in result.Chunks)
            surface.Canvas.DrawImage(chunk.Image, (float)chunk.X, (float)chunk.Y, SKSamplingOptions.Default);
        using var img = surface.Snapshot();
        var info = new SKImageInfo(result.Width, result.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        Assert.True(img.ReadPixels(bitmap.PeekPixels(), 0, 0));
        var bgra = bitmap.Bytes;

        return (bgra, result.Width, result.Height, result);
    }

    [Fact]
    public async Task TwoVoiceChart_RendersASecondRhythmLine()
    {
        var (project, arr, level, beats) = Fixture(2);
        var single = AlphaTexExporter.Export(project, arr, level, beats);
        var (singleBgra, sw, sh, singleResult) = await Render(single.AlphaTex);
        using var _ = singleResult;

        AddSidecar(arr, 2);
        var multi = AlphaTexExporter.Export(project, arr, level, beats);
        var (multiBgra, mw, mh, multiResult) = await Render(multi.AlphaTex);
        using var __ = multiResult;

        // Both frames rendered something.
        Assert.True(GoldenFrame.NonBlackRatio(singleBgra) > 0.01);
        Assert.True(GoldenFrame.NonBlackRatio(multiBgra) > 0.01);

        // The sidecar voice adds a second rhythm line: measurably more ink.
        // Structural tolerance, not a pixel diff: the exact percentage moves
        // with unrelated engraving (tempo marker, dynamics), so the threshold
        // is deliberately loose — the exact CONTENT proof (two voices, right
        // frets/durations) lives in AlphaTexMultiVoiceTests at the model level.
        var singleInk = GoldenFrame.NonBlackRatio(singleBgra) * sw * sh;
        var multiInk = GoldenFrame.NonBlackRatio(multiBgra) * mw * mh;
        Assert.True(multiInk > singleInk * 1.03,
            $"expected more ink with the second voice (single={singleInk:F0}, multi={multiInk:F0})");

        // And when sizes match, the frames differ structurally.
        if (sw == mw && sh == mh)
            Assert.True(GoldenFrame.DifferenceRatio(singleBgra, multiBgra) > 0.005);
    }

    [Fact]
    public async Task BeatBounds_KeepTheVoice0Zip_WithASidecarPresent()
    {
        var (project, arr, level, beats) = Fixture(2);
        AddSidecar(arr, 2);
        var export = AlphaTexExporter.Export(project, arr, level, beats);
        var (_, _, _, result) = await Render(export.AlphaTex);
        using var resultLease = result;

        // The renderer filters bounds to voice 0, so per bar the bounds count
        // must equal the exporter's beat-map count — the exact-click mapping
        // contract (a mismatch silently degrades clicks to time-projection).
        var beatsPerBar = export.BeatMap.GroupBy(b => b.MeasureIndex)
            .ToDictionary(g => g.Key, g => g.Count());
        var boundsPerBar = result.BeatBounds.GroupBy(b => b.BarIndex)
            .ToDictionary(g => g.Key, g => g.Count());

        Assert.Equal(beatsPerBar.Count, boundsPerBar.Count);
        foreach (var (bar, count) in beatsPerBar)
            Assert.Equal(count, boundsPerBar[bar]);
    }
}
