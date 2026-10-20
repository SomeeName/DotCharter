using DotCharter.Services.Export.GuitarPro;
using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Export.GuitarPro;

// Mutates the AMBIENT culture, which is process-wide, so it shares the
// serialized collection with the other culture tests. See
// Infrastructure/CultureCollectionLintTests.
[Collection("LocalizationSingleton")]
public sealed class GpExporterRoundTripTests
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

    private static Arrangement StandardArrangement()
    {
        var arr = new Arrangement { Name = "Lead Test", StringCount = 6 };
        arr.TuningPitches.Clear();
        for (var i = 0; i < 6; i++) arr.TuningPitches.Add(new TuningOffset(0));
        return arr;
    }

    private static GuitarNote Note(double positionBeats, int stringIndex, int fret, double sustainBeats,
        HopoType hopo = HopoType.None)
    {
        return new GuitarNote
        {
            Position = Pos(positionBeats),
            StringIndex = stringIndex,
            Fret = fret,
            Sustain = Pos(sustainBeats),
            Hopo = hopo
        };
    }

    private static FractionalPosition Pos(double beats)
    {
        var beatId = (int)Math.Floor(beats);
        return new FractionalPosition(beatId, new Fraction((int)Math.Round((beats - beatId) * 12), 12));
    }

    [Fact]
    public void Export_IsCultureInvariant()
    {
        // The gpif is a wire format: a de-DE thread culture (comma decimal
        // separator) must produce byte-identical output to the invariant
        // culture. Before the fix, alphaTab serialized floats through the
        // thread culture — every German-locale user exported broken bends,
        // and the culture-switching localization tests made the round-trip
        // tests fail nondeterministically under parallel suite load.
        var arr = StandardArrangement();
        var level = new ChartLevel();
        var bendNote = Note(0, 2, 7, 2);
        bendNote.BendValues.Add(new BendValue(Pos(0.5), 0.5));
        bendNote.BendValues.Add(new BendValue(Pos(1.5), 1.0));
        level.Notes.Add(bendNote);
        var beats = Beats44(8);

        var previous = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            System.Threading.Thread.CurrentThread.CurrentCulture =
                System.Globalization.CultureInfo.InvariantCulture;
            var invariant = GpExporter.ExportToBytes(arr, level, beats, "Culture", "Test");

            System.Threading.Thread.CurrentThread.CurrentCulture =
                new System.Globalization.CultureInfo("de-DE");
            var german = GpExporter.ExportToBytes(arr, level, beats, "Culture", "Test");

            Assert.Equal(invariant, german);
        }
        finally
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    private static GpImportResult ExportAndReimport(Arrangement arrangement, ChartLevel level,
        List<BeatMarker> beats)
    {
        var bytes = GpExporter.ExportToBytes(arrangement, level, beats, "Round Trip", "DotCharter");
        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-gpexport-{Guid.NewGuid():N}.gp");
        try
        {
            File.WriteAllBytes(path, bytes);
            var importer = new GpImporter();
            var tracks = importer.GetAvailableTracks(path);
            var track = Assert.Single(tracks);
            return importer.ImportTracksWithMetadata(path, [track.Id]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RoundTrip_NotesChordsAndRhythm_SurviveExportAndImport()
    {
        var arrangement = StandardArrangement();
        var level = new ChartLevel();
        level.Notes.Add(Note(0.0, 0, 3, 1.0)); // quarter
        level.Notes.Add(Note(1.0, 1, 5, 1.0)); // quarter
        level.Notes.Add(Note(2.0, 2, 7, 0.5)); // eighth, hammer origin
        level.Notes.Add(Note(2.5, 2, 9, 0.5, HopoType.HammerOn)); // eighth, destination
        var chord = new Chord { Position = Pos(4.0) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 0, Sustain = Pos(2.0) });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 2, Sustain = Pos(2.0) });
        level.Chords.Add(chord);

        var result = ExportAndReimport(arrangement, level, Beats44(8));

        // Metadata: two 4/4 bars at 120 BPM.
        Assert.Equal(2, result.Metadata.Bars.Count);
        Assert.All(result.Metadata.Bars, b =>
        {
            Assert.Equal(4, b.BeatsInMeasure);
            Assert.Equal(4, b.NoteDenominator);
        });
        var tempo = Assert.Single(result.Metadata.TempoPoints);
        Assert.Equal(120.0, tempo.Bpm, 1);

        var imported = Assert.Single(result.Arrangements);
        Assert.Equal(6, imported.StringCount);
        Assert.All(imported.TuningPitches, t => Assert.Equal(0, t.Step));
        var importedLevel = Assert.Single(imported.Levels);

        // Singles: string, fret and position survive exactly.
        Assert.Equal(4, importedLevel.Notes.Count);
        var notes = importedLevel.Notes.OrderBy(n => n.Position).ToList();

        Assert.Equal((0, 3), (notes[0].StringIndex, notes[0].Fret));
        Assert.Equal(Pos(0.0), notes[0].Position);
        Assert.Equal(Pos(1.0), notes[0].Sustain); // quarter-note rhythm kept

        Assert.Equal((1, 5), (notes[1].StringIndex, notes[1].Fret));
        Assert.Equal(Pos(1.0), notes[1].Position);

        Assert.Equal((2, 7), (notes[2].StringIndex, notes[2].Fret));
        Assert.Equal(Pos(2.0), notes[2].Position);

        Assert.Equal((2, 9), (notes[3].StringIndex, notes[3].Fret));
        Assert.Equal(Pos(2.5), notes[3].Position);
        Assert.Equal(HopoType.HammerOn, notes[3].Hopo); // 7 -> 9 keeps hammer-on

        // The eighth pair loses only its (already display-trimmed) sustain.
        Assert.True(notes[2].Sustain.DoubleValue is 0.0 or 0.5);
        Assert.True(notes[3].Sustain.DoubleValue is 0.0 or 0.5);

        // Chord: frets/strings survive. Import's auto-handshape pass now trims a
        // plain chord to a short strike (sustain zeroed) and lays a handshape
        // over the half-note region it used to cover.
        var importedChord = Assert.Single(importedLevel.Chords);
        Assert.Equal(Pos(4.0), importedChord.Position);
        Assert.Equal(2, importedChord.ChordNotes.Count);
        Assert.Contains(importedChord.ChordNotes, n => n is { StringIndex: 0, Fret: 0 });
        Assert.Contains(importedChord.ChordNotes, n => n is { StringIndex: 1, Fret: 2 });
        Assert.All(importedChord.ChordNotes, n => Assert.Equal(FractionalPosition.Zero, n.Sustain));

        var handShape = Assert.Single(importedLevel.HandShapes);
        Assert.Equal(Pos(4.0), handShape.Position);
        Assert.Equal(Pos(6.0), handShape.EndPosition);
        Assert.True(handShape.TemplateId >= 0);
    }

    [Fact]
    public void RoundTrip_BendValues_SurviveWithinTolerance()
    {
        var arrangement = StandardArrangement();
        var level = new ChartLevel();
        var bent = Note(0.0, 3, 7, 1.0);
        bent.BendValues.Add(new BendValue(Pos(0.0), 0.0));
        bent.BendValues.Add(new BendValue(Pos(0.5), 2.0)); // whole-tone bend at mid-note
        bent.BendValues.Add(new BendValue(Pos(1.0), 2.0));
        level.Notes.Add(bent);

        var result = ExportAndReimport(arrangement, level, Beats44(4));

        var importedLevel = Assert.Single(Assert.Single(result.Arrangements).Levels);
        var note = Assert.Single(importedLevel.Notes);
        Assert.Equal(3, note.StringIndex);
        Assert.Equal(7, note.Fret);
        Assert.True(note.BendValues.Count >= 2, "bend points lost in round trip");
        Assert.Equal(2.0, note.BendValues.Max(b => b.Step), 1);
        // A bent note must keep its sustain so the bend has room.
        Assert.True(note.Sustain.DoubleValue > 0);
    }

    [Fact]
    public void RoundTrip_DropTuningAndCapo_Survive()
    {
        var arrangement = StandardArrangement();
        arrangement.TuningPitches[0] = new TuningOffset(-2); // drop D
        arrangement.Capo = 2;
        var level = new ChartLevel();
        level.Notes.Add(Note(0.0, 0, 0, 1.0));

        var result = ExportAndReimport(arrangement, level, Beats44(4));

        var imported = Assert.Single(result.Arrangements);
        Assert.Equal(-2, imported.TuningPitches[0].Step);
        Assert.All(imported.TuningPitches.Skip(1), t => Assert.Equal(0, t.Step));
        Assert.Equal(2, imported.Capo);
    }

    [Fact]
    public void Export_IsDeterministic()
    {
        var arrangement = StandardArrangement();
        var level = new ChartLevel();
        level.Notes.Add(Note(0.0, 0, 3, 1.0));
        level.Notes.Add(Note(1.5, 2, 7, 0.5));
        var beats = Beats44(4);

        var first = GpExporter.ExportToBytes(arrangement, level, beats, "T", "A");
        var second = GpExporter.ExportToBytes(arrangement, level, beats, "T", "A");

        // The .gp container embeds zip timestamps, so determinism is asserted
        // on the score document itself.
        Assert.Equal(ReadScoreGpif(first), ReadScoreGpif(second));
    }

    private static string ReadScoreGpif(byte[] gpFile)
    {
        using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(gpFile));
        var entry = archive.Entries.Single(e =>
            e.FullName.EndsWith("score.gpif", StringComparison.OrdinalIgnoreCase));
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }
}
