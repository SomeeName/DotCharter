using System.Text.RegularExpressions;
using DotCharter.Services.Export.GuitarPro;
using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Export.GuitarPro;

/// <summary>
///     Guitar Pro writes tab numbers RELATIVE to the track capo; the chart model
///     stores them ABSOLUTE. <c>GpExporter</c> must therefore apply
///     <see cref="GpCapoFrets.ToRelative"/> — the exact inverse of the importers'
///     <c>ToAbsolute</c> — at both of its fret write sites.
///     <para>
///     These assertions read the WRITTEN gpif, never a re-import. A round trip
///     through our own importer adds the capo straight back, so it passes whether
///     or not the exporter subtracts it — which is precisely how both halves
///     stayed wrong in the same direction for so long.
///     </para>
/// </summary>
public sealed class GpExporterCapoTests
{
    private static List<BeatMarker> Beats44(int count, double bpm = 120.0)
    {
        var beats = new List<BeatMarker>(count);
        for (var i = 0; i < count; i++)
        {
            beats.Add(new BeatMarker
            {
                Time = i * 60_000.0 / bpm,
                Bpm = bpm,
                BeatsInMeasure = 4,
                NoteDenominator = 4,
                IsMeasure = i % 4 == 0
            });
        }

        return beats;
    }

    private static Arrangement Arrangement(int capo)
    {
        var arr = new Arrangement { Name = "Lead Test", StringCount = 6, Capo = capo };
        arr.TuningPitches.Clear();
        for (var i = 0; i < 6; i++) arr.TuningPitches.Add(new TuningOffset(0));
        return arr;
    }

    private static FractionalPosition Pos(double beats)
    {
        var beatId = (int)Math.Floor(beats);
        return new FractionalPosition(beatId, new Fraction((int)Math.Round((beats - beatId) * 12), 12));
    }

    private static GuitarNote Note(double positionBeats, int stringIndex, int fret, int? graceFret = null)
    {
        return new GuitarNote
        {
            Position = Pos(positionBeats),
            StringIndex = stringIndex,
            Fret = fret,
            Sustain = Pos(1.0),
            GraceFret = graceFret
        };
    }

    /// <summary>The score.gpif document inside an exported .gp container.</summary>
    private static string Gpif(Arrangement arrangement, ChartLevel level, List<BeatMarker> beats)
    {
        var bytes = GpExporter.ExportToBytes(arrangement, level, beats, "Capo", "DotCharter");
        using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(bytes));
        var entry = archive.Entries.Single(e =>
            e.FullName.EndsWith("score.gpif", StringComparison.OrdinalIgnoreCase));
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    /// <summary>Every note fret the gpif carries, in document order.</summary>
    private static List<int> WrittenFrets(string gpif)
    {
        var notes = gpif[gpif.IndexOf("<Notes>", StringComparison.Ordinal)..];
        return Regex.Matches(notes, @"<Property name=""Fret""><Fret>(-?\d+)</Fret></Property>")
            .Select(m => int.Parse(m.Groups[1].Value))
            .ToList();
    }

    /// <summary>The capo the staff declares — what makes GP read the frets as relative.</summary>
    private static int WrittenCapo(string gpif)
    {
        var m = Regex.Match(gpif, @"<Property name=""CapoFret""><Fret>(-?\d+)</Fret></Property>");
        Assert.True(m.Success, "the staff wrote no CapoFret property");
        return int.Parse(m.Groups[1].Value);
    }

    [Fact]
    public void Export_Capo2_SubtractsTheCapoFromFrettedNotesAndLeavesOpenStringsAt0()
    {
        var arrangement = Arrangement(2);
        var level = new ChartLevel();
        level.Notes.Add(Note(0.0, 0, 0)); // OPEN — means the same in both models
        level.Notes.Add(Note(1.0, 1, 7)); // fretted -> 5
        level.Notes.Add(Note(2.0, 2, 3)); // fretted, just above the capo -> 1
        var chord = new Chord { Position = Pos(3.0) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 3, Fret = 0, Sustain = Pos(1.0) });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 4, Fret = 12, Sustain = Pos(1.0) });
        level.Chords.Add(chord);

        var gpif = Gpif(arrangement, level, Beats44(4));

        // The capo is declared, so GP reads every number below as relative to it.
        Assert.Equal(2, WrittenCapo(gpif));
        Assert.Equal(new[] { 0, 5, 1, 0, 10 }, WrittenFrets(gpif));
    }

    [Fact]
    public void Export_Capo2_GraceFretsConvertToo()
    {
        // A grace hint is a fret like any other — EmitGrace is the exporter's
        // second (and easily forgotten) fret write site.
        var arrangement = Arrangement(2);
        var level = new ChartLevel();
        level.Notes.Add(Note(0.0, 0, 7, graceFret: 5)); // grace 5 -> 3, main 7 -> 5

        var chord = new Chord { Position = Pos(2.0) };
        chord.ChordNotes.Add(new ChordNote
        { StringIndex = 1, Fret = 9, Sustain = Pos(1.0), GraceFret = 4 }); // 4 -> 2, 9 -> 7
        chord.ChordNotes.Add(new ChordNote
        { StringIndex = 2, Fret = 0, Sustain = Pos(1.0), GraceFret = 0 }); // open grace stays 0
        level.Chords.Add(chord);

        var gpif = Gpif(arrangement, level, Beats44(4));

        // Grace beat precedes its main beat, so document order is grace-then-note.
        Assert.Equal(new[] { 3, 5, 2, 0, 7, 0 }, WrittenFrets(gpif));
    }

    [Fact]
    public void Export_Capo0_IsAStrictNoOp()
    {
        // Capo 0 is nearly every chart: the conversion must be exactly the
        // Math.Max(0, fret) it replaced, byte-for-byte.
        var level = new ChartLevel();
        level.Notes.Add(Note(0.0, 0, 0));
        level.Notes.Add(Note(1.0, 1, 3));
        level.Notes.Add(Note(2.0, 2, 12, graceFret: 10));
        var beats = Beats44(4);

        var gpif = Gpif(Arrangement(0), level, beats);

        Assert.Equal(0, WrittenCapo(gpif));
        // Written frets are the domain frets verbatim.
        Assert.Equal(new[] { 0, 3, 10, 12 }, WrittenFrets(gpif));

        // ...and at the conversion itself, capo 0 reproduces the old expression
        // for every input it could ever see, including the negative frets the
        // old Math.Max clamped.
        for (var fret = -5; fret <= GpCapoFrets.MaxAbsoluteFret; fret++)
            Assert.Equal(Math.Max(0, fret), GpCapoFrets.ToRelative(fret, 0));
    }

    [Fact]
    public void ExportImportExport_OnACapoedChart_ReproducesTheSameRelativeFrets()
    {
        // The fixed point that the missing inverse broke. Export writes relative
        // numbers, import reads them back as absolute, and re-exporting must
        // land on the ORIGINAL relative numbers again. With only one half of the
        // conversion the frets ratchet up by `capo` on every round trip.
        var arrangement = Arrangement(2);
        var level = new ChartLevel();
        level.Notes.Add(Note(0.0, 0, 0)); // open
        level.Notes.Add(Note(1.0, 1, 5)); // -> 3
        level.Notes.Add(Note(2.0, 2, 12)); // -> 10
        var beats = Beats44(4);

        var firstFrets = WrittenFrets(Gpif(arrangement, level, beats));
        Assert.Equal(new[] { 0, 3, 10 }, firstFrets);

        var bytes = GpExporter.ExportToBytes(arrangement, level, beats, "Capo", "DotCharter");
        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-gpcapo-{Guid.NewGuid():N}.gp");
        GpImportResult result;
        try
        {
            File.WriteAllBytes(path, bytes);
            var importer = new GpImporter();
            var track = Assert.Single(importer.GetAvailableTracks(path));
            result = importer.ImportTracksWithMetadata(path, [track.Id]);
        }
        finally
        {
            File.Delete(path);
        }

        var imported = Assert.Single(result.Arrangements);
        Assert.Equal(2, imported.Capo);
        // The importer restored our ABSOLUTE numbers, not GP's relative ones.
        var importedLevel = Assert.Single(imported.Levels);
        Assert.Equal(new[] { 0, 5, 12 },
            importedLevel.Notes.OrderBy(n => n.Position).Select(n => n.Fret).ToArray());

        // ...and the second export writes the identical relative numbers.
        Assert.Equal(firstFrets, WrittenFrets(Gpif(imported, importedLevel, beats)));
    }

    [Fact]
    public void Export_SubCapoFret_IsWrittenAsOpen_NotAsANegativeFret()
    {
        // A fret between 1 and the capo is malformed (ChartExportValidator's
        // KeyNoteBelowCapo / inspector L01): the capo already stops the string
        // higher up, so the sound's offset is max(fret, capo) == capo — exactly
        // what the open string sounds. Folding it to 0 keeps the PITCH the chart
        // actually makes and stays a legal tab number; the naive fret - capo
        // would emit a negative fret, which GP reads as a dead note.
        var arrangement = Arrangement(5);
        var level = new ChartLevel();
        level.Notes.Add(Note(0.0, 0, 3)); // below the capo  -> 0
        level.Notes.Add(Note(1.0, 1, 5)); // exactly AT the capo -> 0
        level.Notes.Add(Note(2.0, 2, 6)); // first real fret above it -> 1
        level.Notes.Add(Note(3.0, 3, 1, graceFret: 2)); // a sub-capo grace folds too

        var gpif = Gpif(arrangement, level, Beats44(4));

        var frets = WrittenFrets(gpif);
        Assert.All(frets, f => Assert.True(f >= 0, $"negative fret {f} written to the gpif"));
        Assert.Equal(new[] { 0, 0, 1, 0, 0 }, frets);
    }

    [Theory]
    // capo 0 — the identity on every non-negative fret.
    [InlineData(0, 0, 0)]
    [InlineData(7, 0, 7)]
    // capo 2 — fret 0 is shared by both models; fretted frets shift down.
    [InlineData(0, 2, 0)]
    [InlineData(3, 2, 1)]
    [InlineData(24, 2, 22)]
    // sub-capo (malformed) frets fold to the open string that they sound as.
    [InlineData(1, 2, 0)]
    [InlineData(2, 2, 0)]
    [InlineData(4, 5, 0)]
    [InlineData(5, 5, 0)]
    // negative input (a dead-note sentinel) reads as open, as it always has.
    [InlineData(-1, 3, 0)]
    public void ToRelative_Cases(int chartFret, int capo, int expected) => Assert.Equal(expected, GpCapoFrets.ToRelative(chartFret, capo));

    [Fact]
    public void ToRelative_IsTheInverseOfToAbsolute_ForEveryWellFormedFret()
    {
        // The importer guarantees an absolute fret is either 0 or >= capo + 1,
        // and over exactly that domain the two conversions must compose to the
        // identity in both directions.
        for (var capo = 0; capo <= 7; capo++)
        {
            Assert.Equal(0, GpCapoFrets.ToRelative(0, capo));

            for (var gpFret = 1; gpFret <= GpCapoFrets.MaxAbsoluteFret - capo; gpFret++)
            {
                var absolute = GpCapoFrets.ToAbsolute(gpFret, capo);
                Assert.True(absolute == 0 || absolute >= capo + 1);
                Assert.Equal(gpFret, GpCapoFrets.ToRelative(absolute, capo));
            }
        }
    }
}
