using System.IO.Compression;
using DotCharter.Services.Export.GuitarPro;
using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Export.GuitarPro;

/// <summary>
///     Techniques that survive export → re-import: a hammer-on with nothing before it on
///     its string (Guitar Pro's left-hand tapping), right-hand tap, slap and pop.
/// </summary>
public sealed class GpExporterTechniqueRoundTripTests
{
    private static List<BeatMarker> Beats44(int count) => [.. Enumerable.Range(0, count).Select(i => new BeatMarker
    {
        Time = i * 500.0, Bpm = 120, BeatsInMeasure = 4, NoteDenominator = 4, IsMeasure = i % 4 == 0
    })];

    private static Arrangement Part(int strings, IEnumerable<GuitarNote> notes, IEnumerable<Chord>? chords = null)
    {
        var arrangement = new Arrangement { Name = "Part", StringCount = strings };
        for (var i = 0; i < strings; i++) arrangement.TuningPitches.Add(new TuningOffset(0));
        var level = new ChartLevel();
        foreach (var note in notes) level.Notes.Add(note);
        foreach (var chord in chords ?? []) level.Chords.Add(chord);
        arrangement.Levels.Add(level);
        return arrangement;
    }

    private static (ChartLevel Level, string Gpif) RoundTrip(Arrangement arrangement)
    {
        var bytes = GpExporter.ExportToBytes(arrangement, arrangement.Levels[0], Beats44(8));
        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-gptech-{Guid.NewGuid():N}.gp");
        try
        {
            File.WriteAllBytes(path, bytes);
            var importer = new GpImporter();
            var back = importer.ImportTracks(path, [.. importer.GetAvailableTracks(path).Select(t => t.Id)]);
            return (Assert.Single(back).Levels[0], ReadGpif(bytes));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string ReadGpif(byte[] gp)
    {
        using var archive = new ZipArchive(new MemoryStream(gp));
        using var reader = new StreamReader(archive.Entries.Single(e => e.FullName.EndsWith("score.gpif", StringComparison.Ordinal)).Open());
        return reader.ReadToEnd();
    }

    private static GuitarNote Note(int beat, int stringIndex, int fret, HopoType hopo = HopoType.None,
        BassPickingType picking = BassPickingType.None)
    {
        return new GuitarNote
        {
            Position = new FractionalPosition(beat, Fraction.Zero),
            StringIndex = stringIndex,
            Fret = fret,
            NotationDuration = new Fraction(1, 1),
            Hopo = hopo,
            BassPicking = picking
        };
    }

    // ---- 1. hammer-on from nothing = left-hand tapping ----------------------------

    /// <summary>The first sound on its string is a hammer-on: there is no origin to mark, so
    /// it goes out as a left-hand tap and comes back a hammer-on.</summary>
    [Fact]
    public void AHammerOnWithNothingBeforeItOnItsString_IsALeftHandTap_AndComesBackAHammerOn()
    {
        var (level, gpif) = RoundTrip(Part(6, [
            Note(0, 1, 5),
            Note(1, 2, 7, HopoType.HammerOn),
            Note(2, 2, 9, HopoType.HammerOn)
        ]));

        Assert.Contains("\"LeftHandTapped\"", gpif, StringComparison.Ordinal);
        var back = level.Notes.OrderBy(n => n.Position).ToList();
        Assert.Equal([HopoType.None, HopoType.HammerOn, HopoType.HammerOn], back.Select(n => n.Hopo));
    }

    /// <summary>The control: a hammer-on from a note on its string is an ordinary legato
    /// origin mark, never a tap.</summary>
    [Fact]
    public void AHammerOnFromANoteOnItsString_IsNotALeftHandTap()
    {
        var (level, gpif) = RoundTrip(Part(6, [Note(0, 1, 5), Note(1, 1, 7, HopoType.HammerOn)]));

        Assert.DoesNotContain("LeftHandTapped", gpif, StringComparison.Ordinal);
        Assert.Equal(HopoType.HammerOn, Assert.Single(level.Notes, n => n.Fret == 7).Hopo);
    }

    // ---- 2. slap, pop, right-hand tap ---------------------------------------------

    [Fact]
    public void SlapPopAndTap_OnSingleNotes_SurviveTheRoundTrip()
    {
        var (level, gpif) = RoundTrip(Part(4, [
            Note(0, 0, 3, picking: BassPickingType.Slap),
            Note(1, 2, 5, picking: BassPickingType.Pop),
            Note(2, 3, 12, HopoType.Tap),
            Note(3, 1, 3)
        ]));

        Assert.Contains("\"Slapped\"", gpif, StringComparison.Ordinal);
        Assert.Contains("\"Popped\"", gpif, StringComparison.Ordinal);
        var back = level.Notes.OrderBy(n => n.Position).ToList();
        Assert.Equal(
            [(BassPickingType.Slap, HopoType.None), (BassPickingType.Pop, HopoType.None),
                (BassPickingType.None, HopoType.Tap), (BassPickingType.None, HopoType.None)],
            back.Select(n => (n.BassPicking, n.Hopo)));
    }

    [Fact]
    public void ASlappedChord_ComesBackSlappedOnEveryString()
    {
        var chord = new Chord { Position = new FractionalPosition(0, Fraction.Zero), NotationDuration = new Fraction(1, 1) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 3, BassPicking = BassPickingType.Slap });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5, BassPicking = BassPickingType.Slap });

        var (level, _) = RoundTrip(Part(4, [Note(1, 2, 5)], [chord]));

        Assert.All(Assert.Single(level.Chords).ChordNotes, cn => Assert.Equal(BassPickingType.Slap, cn.BassPicking));
        Assert.Equal(BassPickingType.None, Assert.Single(level.Notes).BassPicking);
    }

    // ---- 3. chord harmonics -------------------------------------------------------

    [Theory]
    [InlineData(HarmonicType.Normal, 12)]
    [InlineData(HarmonicType.Normal, 7)]
    [InlineData(HarmonicType.Pinch, 5)]
    public void AChordOfHarmonics_KeepsThem(HarmonicType harmonic, int fret)
    {
        var chord = new Chord { Position = new FractionalPosition(0, Fraction.Zero), NotationDuration = new Fraction(1, 1) };
        for (var s = 2; s < 5; s++)
            chord.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = fret, Harmonic = harmonic });

        var (level, _) = RoundTrip(Part(6, [Note(1, 1, 3)], [chord]));

        var back = Assert.Single(level.Chords);
        Assert.Equal([fret, fret, fret], back.ChordNotes.OrderBy(cn => cn.StringIndex).Select(cn => cn.Fret));
        Assert.All(back.ChordNotes, cn => Assert.Equal(harmonic, cn.Harmonic));
    }

    [Theory]
    [InlineData(HarmonicType.Normal)]
    [InlineData(HarmonicType.Pinch)]
    public void ASingleHarmonic_KeepsIt(HarmonicType harmonic)
    {
        var note = Note(0, 2, 12);
        note.Harmonic = harmonic;

        var (level, _) = RoundTrip(Part(6, [note, Note(1, 1, 3)]));

        Assert.Equal(harmonic, Assert.Single(level.Notes, n => n.Fret == 12).Harmonic);
    }
}
