using DotCharter.Services.Parsing.Eof;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Parsing.Eof;

/// <summary>
///     #795: which EOF note carries a hand shape's ghost gems. EOF's RS export reads a handshape's
///     chord from the FIRST note in the phrase with two or more gems, ghosts counted (rs.c:2302-2316),
///     so a bar that opens before its first sound, or on a tapped harmonic followed by its chord,
///     still has a spelling. A ghost on a string its only possible carrier plays has none (one fret
///     per string per EOF note; same-timestamp notes merge, song.c:6905) and is reported, not moved.
/// </summary>
public sealed class EofGhostCarrierTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dc-eof-ghost-" + Guid.NewGuid().ToString("N"));

    public EofGhostCarrierTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => BestEffortDelete.Directory(_dir);

    [Fact]
    public void ABarThatOpensBeforeItsFirstSound_WritesTheGhostOnThatSound_AndReadsItBack()
    {
        var arrangement = Guitar();
        var grip = AddGrip(arrangement);
        AddChord(arrangement, beat: 2);
        arrangement.Levels[0].HandShapes.Add(new HandShape
        {
            Position = new FractionalPosition(1), EndPosition = new FractionalPosition(4), TemplateId = grip.Id
        });

        var (back, report) = RoundTrip(arrangement);

        AssertGripCameBack(back);
        Assert.DoesNotContain(report.Notes, n => n.Loss == EofLoss.HandShapeGhostGem);
    }

    [Fact]
    public void ATappedHarmonicOpeningTheBar_LeavesTheGhostToTheChordThatFollows()
    {
        var arrangement = Guitar();
        var grip = AddGrip(arrangement);
        var level = arrangement.Levels[0];
        // The tap at 17 on the D string, over the held 5 on the same string.
        level.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(2), StringIndex = 2, Fret = 17, Harmonic = HarmonicType.Normal,
            Hopo = HopoType.Tap
        });
        AddChord(arrangement, beat: 3);
        level.HandShapes.Add(new HandShape
        {
            Position = new FractionalPosition(2), EndPosition = new FractionalPosition(4), TemplateId = grip.Id
        });

        var (back, report) = RoundTrip(arrangement);

        AssertGripCameBack(back);
        var tap = Assert.Single(back.Levels[0].Notes);
        Assert.Equal((2, 17, HopoType.Tap), (tap.StringIndex, tap.Fret, tap.Hopo));
        Assert.DoesNotContain(report.Notes, n => n.Loss == EofLoss.HandShapeGhostGem);
    }

    [Fact]
    public void ATappedHarmonicAlone_AndABarOverNoSound_HaveNoEofSpelling_AndAreReported()
    {
        var arrangement = Guitar();
        var level = arrangement.Levels[0];
        var held = new ChordTemplate { Id = 0, Frets = { [2] = 5 }, Fingers = { [2] = 1 }, GhostMask = ChordTemplate.GhostBit(2) };
        arrangement.ChordTemplates.Add(held);
        level.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(2), StringIndex = 2, Fret = 17, Harmonic = HarmonicType.Normal,
            Hopo = HopoType.Tap
        });
        level.HandShapes.Add(new HandShape
        {
            Position = new FractionalPosition(2), EndPosition = new FractionalPosition(3), TemplateId = held.Id
        });
        level.HandShapes.Add(new HandShape
        {
            Position = new FractionalPosition(8), EndPosition = new FractionalPosition(9), TemplateId = held.Id
        });

        var report = Export(arrangement).Report;

        var losses = report.Notes.Where(n => n.Loss == EofLoss.HandShapeGhostGem).ToList();
        Assert.Equal(2, losses.Count);
        Assert.All(losses, n => Assert.Equal(EofConversionKind.Dropped, n.Kind));
        Assert.All(losses, n => Assert.Equal("2", n.Detail));
        Assert.Equal([1000.0, 4000.0], losses.Select(n => n.TimeMs!.Value));
    }

    private static void AssertGripCameBack(Arrangement back)
    {
        var shape = Assert.Single(back.Levels[0].HandShapes);
        var grip = back.ChordTemplates.Single(t => t.Id == shape.TemplateId);
        Assert.Equal(new Dictionary<int, int> { [0] = 3, [1] = 5, [2] = 5 }, grip.Frets);
        Assert.Equal(ChordTemplate.GhostBit(2), grip.GhostMask);
        Assert.Equal(4, grip.Fingers[2]);
        Assert.Equal([0, 1], Assert.Single(back.Levels[0].Chords).ChordNotes.Select(n => n.StringIndex).Order());
    }

    private (Arrangement Back, EofConversionReport Report) RoundTrip(Arrangement arrangement)
    {
        var result = Export(arrangement);
        var path = Path.Combine(_dir, "p.eof");
        File.WriteAllBytes(path, result.Bytes);
        var importer = new EofImporter();
        var back = importer.ImportTracks(path, importer.GetAvailableTracks(path).Select(t => t.Id).ToList()).Single();
        return (back, result.Report);
    }

    private static EofExportResult Export(Arrangement arrangement)
    {
        var project = new ChartProject { AudioFileName = "song.wav" };
        foreach (var i in Enumerable.Range(0, 16))
            project.Beats.Add(new BeatMarker { Time = i * 500.0, Bpm = 120, IsMeasure = i % 4 == 0, BeatsInMeasure = 4, NoteDenominator = 4 });
        project.Arrangements.Add(arrangement);
        return EofExporter.Export(project);
    }

    private static Arrangement Guitar()
    {
        var arrangement = new Arrangement
        {
            ArrangementType = ArrangementType.Lead, Name = "Lead", StringCount = 6,
            Tuning = ["E", "A", "D", "G", "B", "E"]
        };
        for (var s = 0; s < 6; s++) arrangement.TuningPitches.Add(new TuningOffset(0));
        arrangement.Levels.Add(new ChartLevel { DifficultyIndex = 0 });
        return arrangement;
    }

    /// <summary>G5 (3-5) played on template 1; the bar's grip, template 0, holds the octave 5 as a ghost.</summary>
    private static ChordTemplate AddGrip(Arrangement arrangement)
    {
        var grip = new ChordTemplate
        {
            Id = 0, Name = "G5", Frets = { [0] = 3, [1] = 5, [2] = 5 }, Fingers = { [0] = 1, [1] = 3, [2] = 4 },
            GhostMask = ChordTemplate.GhostBit(2)
        };
        arrangement.ChordTemplates.Add(grip);
        arrangement.ChordTemplates.Add(new ChordTemplate
        {
            Id = 1, Name = "G5", Frets = { [0] = 3, [1] = 5 }, Fingers = { [0] = 1, [1] = 3 }
        });
        return grip;
    }

    private static void AddChord(Arrangement arrangement, int beat)
    {
        var chord = new Chord { Position = new FractionalPosition(beat), TemplateId = 1 };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 3 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5 });
        arrangement.Levels[0].Chords.Add(chord);
    }
}
