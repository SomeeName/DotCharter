using AlphaTab;
using AlphaTab.Importer;
using AlphaTab.Model;
using DotCharter.Services.Export.Sheet;
using SkiaSharp;

namespace DotCharter.Tests.Services.Export.Sheet;

/// <summary>
/// Golden-frame guard for the tab-only rhythm engraving fixes:
/// <list type="bullet">
///   <item>BEAMS CONNECT — a straight-eighths run must render with beam bars
///   joining adjacent stems (TabRhythmMode.ShowWithBars). The shipped
///   ShowWithBeams mode gave every beat its own detached flag, so nothing in
///   the tab strip ever read as engraved rhythm. Structural assertion: below
///   the tab staff there is a horizontal ink run at least as long as the gap
///   between two adjacent rendered beats — flags are glyph-width blobs and
///   can never span it.</item>
///   <item>SLIDES ARE SHIFT SLIDES — plain chart slides emit `ss` (clean
///   diagonal connector, GP-style), not `sl` (legato), whose per-slide slur
///   arc + "sl." label stacked into the tangled curves around chord names the
///   notation view shipped with.</item>
/// </list>
/// CPU raster (AlphaSkia) with structural tolerances — runs in the default
/// gate, no GPU env var.
/// </summary>
[Collection("AlphaTab")]
public class SheetTabBeamingRenderTests
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

        return (project, arr, level, beats);
    }

    [Fact]
    public async Task StraightEighths_TabOnly_RenderConnectedBeamGroups()
    {
        var (project, arr, level, beats) = Fixture(1);
        for (var i = 0; i < 8; i++)
        {
            level.Notes.Add(new GuitarNote
            {
                Position = new FractionalPosition(i / 2, new Fraction(i % 2, 2)),
                StringIndex = 2,
                Fret = 5
            });
        }

        var export = AlphaTexExporter.Export(project, arr, level, beats);
        var result = await new SheetMusicRenderer()
            .RenderAsync(export.AlphaTex, 1200, 1.3, SheetMusicRenderer.StavesTab, CancellationToken.None);
        Assert.NotNull(result);
        using var _ = result;

        // Compose the chunks and read pixels back.
        var info = new SKImageInfo(result.Width, result.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        surface.Canvas.Clear(SKColors.Black);
        foreach (var chunk in result.Chunks)
            surface.Canvas.DrawImage(chunk.Image, (float)chunk.X, (float)chunk.Y, SKSamplingOptions.Default);
        using var img = surface.Snapshot();
        using var bitmap = new SKBitmap(info);
        Assert.True(img.ReadPixels(bitmap.PeekPixels(), 0, 0));
        var bgra = bitmap.Bytes;

        // Adjacent-beat spacing from the REAL beat bounds — the yardstick a
        // beam must span. (Flags are ~a glyph wide; the spacing is far wider.)
        var bounds = result.BeatBounds.Where(b => b.BarIndex == 0).OrderBy(b => b.BeatIndex).ToList();
        Assert.True(bounds.Count >= 8, $"expected 8 beat bounds, got {bounds.Count}");
        var spacing = bounds[1].X - bounds[0].X;
        Assert.True(spacing > 8, $"degenerate beat spacing {spacing}");

        // The rhythm stems + beams live BELOW the tab staff. Count horizontal
        // ink runs of BEAM length: longer than any flag glyph (which is what
        // ShowWithBeams degenerates to) but far shorter than a staff line
        // (full width) — eighth PAIRS beam per quarter group, so a beam run is
        // roughly one beat spacing wide. The bottom band still contains staff
        // lines, which is why a plain longest-run scan can't tell the modes
        // apart (verified: it passes in both).
        var w = result.Width;
        var h = result.Height;
        var lo = spacing * 0.7;
        var hi = spacing * 2.5;
        var beamRuns = 0;
        for (var y = (int)(h * 0.60); y < h; y++)
        {
            var run = 0;
            for (var x = 0; x <= w; x++)
            {
                var lit = false;
                if (x < w)
                {
                    var o = (y * w + x) * 4;
                    lit = bgra[o] > 40 || bgra[o + 1] > 40 || bgra[o + 2] > 40;
                }

                if (lit)
                {
                    run++;
                }
                else
                {
                    if (run >= lo && run <= hi) beamRuns++;
                    run = 0;
                }
            }
        }

        Assert.True(beamRuns >= 3,
            $"no beam-length ink runs below the tab staff (found {beamRuns} in [{lo:F0},{hi:F0}]px, " +
            $"beat spacing={spacing:F0}px) — eighths are rendering as detached flags again");
    }

    [Fact]
    public void PlainSlides_EmitShiftSlides_NeverLegatoArcs()
    {
        var (project, arr, level, beats) = Fixture(1);
        level.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(0), StringIndex = 2, Fret = 8, SlideTo = 10,
            Sustain = new FractionalPosition(1)
        });
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(1), StringIndex = 2, Fret = 10 });

        var export = AlphaTexExporter.Export(project, arr, level, beats);

        var importer = new AlphaTexImporter();
        importer.InitFromString(export.AlphaTex, new Settings());
        var note = importer.ReadScore().Tracks[0].Staves[0].Bars[0].Voices[0].Beats[0].Notes[0];

        Assert.Equal(SlideOutType.Shift, note.SlideOutType); // ss — clean connector line
        Assert.DoesNotContain("{sl}", export.AlphaTex);      // never the arc+label form
    }
}
