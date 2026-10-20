using System.IO.Compression;
using System.Text.RegularExpressions;
using DotCharter.Services.Export.GuitarPro;
using DotCharter.Services.Parsing.GuitarPro;
using DotCharter.Tests.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Export.GuitarPro;

/// <summary>
///     A charted PICK SCRAPE leaves through <see cref="GpExporter" /> as Guitar Pro's
///     own pick slide (#637) — gpif <c>Slide</c> flag 64 toward the nut, 128 toward
///     the bridge — and nothing it implies: no dead note, no slide-out, no tremolo
///     mark. Re-imported, it is the scrape it was: the same frets, fret-hand mute,
///     unpitched slide, tap, tremolo, ignore and split chord, in the same direction.
///     The import charts a scrape (<see cref="GpPickScrape.Apply" />) and the export
///     recognises one (<see cref="GpPickScrape.IsChartedScrape" />) through the same
///     <see cref="GpPickScrape.IsScrape" />, so the two directions cannot disagree.
///     Scores come from <see cref="PickScrapeScenes" />, the import tests' fixtures;
///     the exporter takes <c>AlphaTabRuntime.RunExclusive</c> itself.
/// </summary>
public sealed class GpExporterPickScrapeTests
{
    /// <summary>
    ///     An FHP 5 riff, then an E+A scrape UP for a quarter (beat 4) and a low-E
    ///     scrape DOWN for a quarter (beat 5): the two shapes the import tests' scenes
    ///     do not cover — a multi-string scrape toward the bridge and a one-string
    ///     scrape toward the nut.
    /// </summary>
    private static PickScrapeScenes.ScrapeBeat[][] BothWays() =>
    [
        [new("Quarter", [new(0, 5), new(1, 7)]), new("Quarter", [new(1, 7)]), new("Quarter", [new(0, 5)]),
            new("Quarter", [new(0, 5), new(1, 7)])],
        [new("Quarter", [new(0, 0, GpPickScrape.UpFlag), new(1, 0, GpPickScrape.UpFlag)]),
            new("Quarter", [new(0, 0, GpPickScrape.DownFlag)]), new("Half", [])]
    ];

    private static PickScrapeScenes.ScrapeBeat[][] Scene(string name) => name switch
    {
        "Shinbatsu" => PickScrapeScenes.Shinbatsu(),
        "TowardBridge" => PickScrapeScenes.TowardBridge(),
        "BothWays" => BothWays(),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null)
    };

    /// <summary>
    ///     GP → import → <see cref="GpExporter" /> → import gives back every scrape
    ///     field for field, and leaves the rest of the chart (the riff's notes and the
    ///     generated anchors) as it was. The counts pin each scene's shape, so a scene
    ///     that stopped importing scrapes cannot pass by comparing two empty lists.
    /// </summary>
    [Theory]
    // Shinbatsu: three A+D eighth notes and an E+A half note, all toward the nut,
    // each a split chord.
    [InlineData("Shinbatsu", 0, 8, 0, 4)]
    // Toward the bridge: two one-string scrapes, a quarter and an eighth.
    [InlineData("TowardBridge", 2, 0, 2, 0)]
    // An E+A split chord toward the bridge, then one string toward the nut.
    [InlineData("BothWays", 1, 1, 2, 1)]
    public void RoundTrip_KeepsEveryScrape(string scene, int singleNotes, int towardNut, int towardBridge,
        int splitChords)
    {
        var bars = Scene(scene);
        var arrangement = PickScrapeScenes.Import(PickScrapeScenes.Gpif(bars));
        var level = Assert.Single(arrangement.Levels);

        var before = Scrapes(level);
        Assert.Equal(towardNut + towardBridge, before.Length);
        Assert.Equal(towardNut, before.Count(s => s.SlideTo < s.Fret));
        Assert.Equal(singleNotes, before.Count(s => s.Kind == "note"));
        Assert.Equal(splitChords, level.Chords.Count(GpPickScrape.IsScrapeChord));

        var reimported = Reimport(arrangement, level, bars.Length);
        var after = Assert.Single(reimported.Levels);

        Assert.Equal(before, Scrapes(after));
        Assert.Equal(splitChords, after.Chords.Count(GpPickScrape.IsScrapeChord));
        Assert.Equal(Plain(level), Plain(after));
        Assert.Equal(level.Anchors.Select(a => (a.Position, a.Fret, a.Width)),
            after.Anchors.Select(a => (a.Position, a.Fret, a.Width)));
    }

    /// <summary>
    ///     What the gpif itself says: one pick-slide flag per scrape string — 64
    ///     toward the nut, 128 toward the bridge — and none of what the scrape
    ///     implies (the <c>Muted</c> dead note, the 4 / 8 slide-out, a beat
    ///     <c>Tremolo</c>). A re-import alone would not show those: the scrape bit
    ///     outranks them on the way in.
    /// </summary>
    [Theory]
    [InlineData("Shinbatsu", 8, 0)]
    [InlineData("TowardBridge", 0, 2)]
    [InlineData("BothWays", 1, 2)]
    public void Export_WritesPickSlides_AndNothingTheyImply(string scene, int down, int up)
    {
        var bars = Scene(scene);
        var arrangement = PickScrapeScenes.Import(PickScrapeScenes.Gpif(bars));

        var gpif = Gpif(arrangement, Assert.Single(arrangement.Levels), bars.Length);

        var flags = SlideFlags(gpif);
        Assert.Equal(down, flags.Count(f => f == GpPickScrape.DownFlag));
        Assert.Equal(up, flags.Count(f => f == GpPickScrape.UpFlag));
        Assert.Equal(down + up, flags.Count);
        Assert.DoesNotContain("<Property name=\"Muted\">", gpif, StringComparison.Ordinal);
        Assert.DoesNotContain("<Tremolo>", gpif, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Not a scrape: a fret-hand-muted note with an unpitched slide-out — alone,
    ///     tapped and tremolo-picked but not ignored, or every scrape technique but
    ///     the mute — stays a dead note (when muted) with an ordinary slide-out down
    ///     (gpif flag 4), never a pick slide, and re-imports as the same slide.
    /// </summary>
    [Theory]
    [InlineData(false, false, MuteType.Full)]
    [InlineData(true, false, MuteType.Full)]
    [InlineData(true, true, MuteType.None)]
    public void MutedSlideOut_IsNotAScrape(bool tapTremolo, bool ignore, MuteType mute)
    {
        var arrangement = new Arrangement { Name = "Lead", StringCount = 6 };
        arrangement.TuningPitches.Clear();
        for (var i = 0; i < 6; i++) arrangement.TuningPitches.Add(new TuningOffset(0));
        var level = new ChartLevel();
        level.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(0),
            StringIndex = 2,
            Fret = 7,
            SlideTo = 2,
            UnpitchedSlide = true,
            Mute = mute,
            Hopo = tapTremolo ? HopoType.Tap : HopoType.None,
            Tremolo = tapTremolo,
            Ignore = ignore,
            Sustain = new FractionalPosition(1)
        });
        Assert.False(GpPickScrape.IsChartedScrape(level.Notes[0]));

        var gpif = Gpif(arrangement, level, bars: 1);
        Assert.Equal(new[] { 4 }, SlideFlags(gpif));
        Assert.Equal(mute == MuteType.Full, gpif.Contains("<Property name=\"Muted\">", StringComparison.Ordinal));

        var note = Assert.Single(Assert.Single(Reimport(arrangement, level, bars: 1).Levels).Notes);
        Assert.False(GpPickScrape.IsScrape(note));
        Assert.Equal((7, 2, true, mute), (note.Fret, note.SlideTo, note.UnpitchedSlide, note.Mute));
    }

    // ── helpers ───────────────────────────────────────────────────────────

    /// <summary>One scrape string as the chart holds it.</summary>
    private sealed record ScrapeString(string Kind, FractionalPosition Position, int String, int Fret, int? SlideTo,
        MuteType Mute, bool UnpitchedSlide, HopoType Hopo, bool Tremolo, bool Ignore, bool LinkNext,
        FractionalPosition Sustain, bool Split, bool ChordIgnore, int ChordSize);

    private static ScrapeString[] Scrapes(ChartLevel level)
    {
        var notes = level.Notes.Where(GpPickScrape.IsScrape).Select(n => new ScrapeString("note", n.Position,
            n.StringIndex, n.Fret, n.SlideTo, n.Mute, n.UnpitchedSlide, n.Hopo, n.Tremolo, n.Ignore, n.LinkNext,
            n.Sustain, false, false, 1));
        var chords = level.Chords.Where(c => c.ChordNotes.Any(GpPickScrape.IsScrape)).SelectMany(c =>
            c.ChordNotes.Select(n => new ScrapeString("chord", c.Position, n.StringIndex, n.Fret, n.SlideTo, n.Mute,
                n.UnpitchedSlide, n.Hopo, n.Tremolo, n.Ignore, n.LinkNext, n.Sustain, c.SplitIntoNotes, c.Ignore,
                c.ChordNotes.Count)));
        return notes.Concat(chords).OrderBy(s => s.Position).ThenBy(s => s.String).ToArray();
    }

    /// <summary>Every sound that is not a scrape: position, string and fret.</summary>
    private static (FractionalPosition, int, int)[] Plain(ChartLevel level)
        => level.Notes.Where(n => !GpPickScrape.IsScrape(n)).Select(n => (n.Position, n.StringIndex, n.Fret))
            .Concat(level.Chords.Where(c => !c.ChordNotes.Any(GpPickScrape.IsScrape))
                .SelectMany(c => c.ChordNotes.Select(n => (c.Position, n.StringIndex, n.Fret))))
            .OrderBy(t => t.Item1).ThenBy(t => t.Item2).ToArray();

    private static List<BeatMarker> Beats44(int bars)
    {
        var beats = new List<BeatMarker>(bars * 4);
        for (var i = 0; i < bars * 4; i++)
        {
            beats.Add(new BeatMarker
            {
                Time = i * 500.0,
                Bpm = 120,
                BeatsInMeasure = 4,
                NoteDenominator = 4,
                IsMeasure = i % 4 == 0
            });
        }

        return beats;
    }

    /// <summary>The score.gpif inside the exported <c>.gp</c>.</summary>
    private static string Gpif(Arrangement arrangement, ChartLevel level, int bars)
    {
        var bytes = GpExporter.ExportToBytes(arrangement, level, Beats44(bars), "Scrape", "DotCharter");
        using var archive = new ZipArchive(new MemoryStream(bytes));
        var entry = archive.Entries.Single(e => e.FullName.EndsWith("score.gpif", StringComparison.OrdinalIgnoreCase));
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    /// <summary>Every note's <c>Slide</c> flags, in document order.</summary>
    private static List<int> SlideFlags(string gpif)
        => Regex.Matches(gpif, @"<Property name=""Slide"">\s*<Flags>(\d+)</Flags>")
            .Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
            .ToList();

    /// <summary>Exports through <see cref="GpExporter" /> and imports the file back
    /// through the real <see cref="GpImporter" />.</summary>
    private static Arrangement Reimport(Arrangement arrangement, ChartLevel level, int bars)
    {
        var bytes = GpExporter.ExportToBytes(arrangement, level, Beats44(bars), "Scrape", "DotCharter");
        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-gpscrape-export-{Guid.NewGuid():N}.gp");
        try
        {
            File.WriteAllBytes(path, bytes);
            var importer = new GpImporter();
            var track = Assert.Single(importer.GetAvailableTracks(path));
            return Assert.Single(importer.ImportTracks(path, [track.Id]));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
