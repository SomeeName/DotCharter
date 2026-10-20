using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
///     Opt-in diagnostic over a REAL tab (env <c>DOTCHARTER_GP_PROBE</c> =
///     .gp path): imports every track through the real pipeline and dumps
///     the artifacts behind the 2026-07-18 owner reports — absurd-span chord
///     templates (the fret-1-inside-a-fret-11-chord), wide anchors, and
///     slide notes with their sustains. No-op without the env var.
/// </summary>
public sealed class GpImportProbe(ITestOutputHelper output)
{
    [Fact]
    public void Dump_import_quality_for_probe_gp()
    {
        var path = Environment.GetEnvironmentVariable("DOTCHARTER_GP_PROBE");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(path),
            "Opt-in: set DOTCHARTER_GP_PROBE to a .gp/.gp5 path to run this probe.");
        Assert.SkipWhen(!File.Exists(path),
            $"DOTCHARTER_GP_PROBE points at '{path}', which does not exist.");

        var importer = new GpImporter();
        var tracks = importer.GetAvailableTracks(path);
        output.WriteLine($"TRACKS: {string.Join(" | ", tracks.Select(t => $"{t.Id}:{t.Name}"))}");
        var result = importer.ImportTracksWithMetadata(path, tracks.Select(t => t.Id).ToList());

        // A dump that found nothing must read as a FAILURE, not as "no problems":
        // an importer that returned zero tracks/arrangements would otherwise
        // produce an empty report indistinguishable from a clean one.
        Assert.NotEmpty(tracks);
        Assert.NotEmpty(result.Arrangements);

        foreach (var arr in result.Arrangements)
        {
            output.WriteLine($"ARRANGEMENT '{arr.Name}' templates={arr.ChordTemplates.Count} levels={arr.Levels.Count}");

            for (var i = 0; i < arr.ChordTemplates.Count; i++)
            {
                var t = arr.ChordTemplates[i];
                var fretted = t.Frets.Values.Where(f => f > 0).ToList();
                if (fretted.Count < 2) continue;
                var span = fretted.Max() - fretted.Min();
                if (span > 5)
                {
                    output.WriteLine(
                        $"  ABSURD template[{i}] id={t.Id} name='{t.Name}' " +
                        $"frets={string.Join(",", t.Frets.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"))} " +
                        $"fingers={string.Join(",", t.Fingers.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"))}");
                }
            }

            var level = arr.Levels[^1];
            foreach (var a in level.Anchors.Where(a => a.Width > 4).Take(12))
                output.WriteLine($"  WIDE anchor p={a.Position} fret={a.Fret} width={a.Width}");

            var slides = level.Notes.Where(n => n.SlideTo is > 0).Take(15).ToList();
            foreach (var n in slides)
            {
                var end = n.EndPosition;
                var target = level.Notes.FirstOrDefault(t =>
                    t.StringIndex == n.StringIndex && t.Position == end);
                output.WriteLine(
                    $"  SLIDE p={n.Position} string={n.StringIndex} fret={n.Fret}->{n.SlideTo} " +
                    $"sustain={n.Sustain} linkNext={n.LinkNext} | target=" +
                    (target == null
                        ? "NONE"
                        : $"fret {target.Fret} sustain={target.Sustain} linkNext={target.LinkNext}"));
            }
        }
    }
}
