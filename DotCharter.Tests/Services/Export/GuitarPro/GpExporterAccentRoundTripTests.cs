using DotCharter.Services.Export.GuitarPro;
using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Export.GuitarPro;

/// <summary>
///     Accents survive export → re-import unchanged (#101). The tab-corpus round trip showed
///     two exporter sources of accents the chart never had: a single note written at its
///     authored value followed by a short rest, which the importer's SHORT-REST (staccato)
///     rule accents, and a chord accent written onto every chord note.
/// </summary>
public sealed class GpExporterAccentRoundTripTests
{
    private static List<BeatMarker> Beats44(int count) => [.. Enumerable.Range(0, count).Select(i => new BeatMarker
    {
        Time = i * 500.0, Bpm = 120, BeatsInMeasure = 4, NoteDenominator = 4, IsMeasure = i % 4 == 0
    })];

    private static Arrangement Lead(IEnumerable<GuitarNote> notes, IEnumerable<Chord>? chords = null)
    {
        var arrangement = new Arrangement { Name = "Lead", StringCount = 6 };
        for (var i = 0; i < 6; i++) arrangement.TuningPitches.Add(new TuningOffset(0));
        var level = new ChartLevel();
        foreach (var note in notes) level.Notes.Add(note);
        foreach (var chord in chords ?? []) level.Chords.Add(chord);
        arrangement.Levels.Add(level);
        return arrangement;
    }

    private static ChartLevel RoundTrip(Arrangement arrangement)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-accent-{Guid.NewGuid():N}.gp");
        try
        {
            File.WriteAllBytes(path, GpExporter.ExportToBytes(arrangement, arrangement.Levels[0], Beats44(12)));
            var importer = new GpImporter();
            var back = importer.ImportTracks(path, [.. importer.GetAvailableTracks(path).Select(t => t.Id)]);
            return Assert.Single(back).Levels[0];
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static FractionalPosition At(int beat, int sixteenths = 0) => new(beat, new Fraction(sixteenths, 4));

    /// <summary>A note with an authored value and no sustain, as a Guitar Pro import leaves it.</summary>
    private static GuitarNote Note(FractionalPosition at, int stringIndex, int fret, Fraction value, bool accent = false) =>
        new() { Position = at, StringIndex = stringIndex, Fret = fret, NotationDuration = value, Accent = accent };

    private static List<(FractionalPosition, bool)> Accents(ChartLevel level) =>
        [.. level.Notes.OrderBy(n => n.Position).Select(n => (n.Position, n.Accent))];

    /// <summary>An eighth whose next note comes a sixteenth after its value ends — the shape
    /// a second voice leaves after the voices are merged. Written as eighth + sixteenth rest,
    /// the re-import read that rest as staccato and accented the note.</summary>
    [Fact]
    public void AShortNoteFollowedByAGap_ComesBackWithoutAnAccent()
    {
        var original = Lead([
            Note(At(0), 1, 5, new Fraction(1, 2)),
            Note(At(0, 3), 1, 7, new Fraction(1, 4)),
            Note(At(1), 2, 5, new Fraction(1, 4)),
            Note(At(1, 2), 2, 7, new Fraction(1, 1))
        ]);

        var back = RoundTrip(original);

        Assert.Equal(Accents(original.Levels[0]), Accents(back));
    }

    /// <summary>The bar's last note, a rest after it up to the bar line and the next note on
    /// the next downbeat: the rest that articulates it is judged across the bar line.</summary>
    [Fact]
    public void ABarsLastShortNoteBeforeTheNextDownbeat_ComesBackWithoutAnAccent()
    {
        var original = Lead([
            Note(At(0), 1, 5, new Fraction(13, 4)),
            Note(At(3, 1), 1, 7, new Fraction(1, 2)),
            Note(At(4), 2, 5, new Fraction(1, 1))
        ]);

        var back = RoundTrip(original);

        Assert.Equal(Accents(original.Levels[0]), Accents(back));
    }

    /// <summary>A real staccato — an ACCENTED note before its short rest — stays accented, and a
    /// note whose rest is longer than its own value (a phrase ending, not staccato) keeps the
    /// value it was written with.</summary>
    [Fact]
    public void AnAccentedShortNote_StaysAccented_AndAPhraseEndingKeepsItsWrittenValue()
    {
        var original = Lead([
            Note(At(0), 1, 5, new Fraction(1, 2), accent: true),
            Note(At(0, 3), 1, 7, new Fraction(1, 4)),
            Note(At(1), 2, 5, new Fraction(1, 4)),
            Note(At(2), 2, 7, new Fraction(1, 1))
        ]);

        var back = RoundTrip(original);

        Assert.Equal(Accents(original.Levels[0]), Accents(back));
        Assert.Equal(new Fraction(1, 4), back.Notes.Single(n => n.Position.Equals(At(1))).NotationDuration);
    }

    /// <summary>A chord whose accent sits on ONE of its notes (how a Guitar Pro import stores it):
    /// the export wrote the accent on every chord note, and the re-import read it per note.</summary>
    [Fact]
    public void AChordAccentOnOneNote_ComesBackOnThatNoteOnly()
    {
        var chord = new Chord { Position = At(0), Accent = true, NotationDuration = new Fraction(1, 1) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 3, Accent = true });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = 5 });

        var back = RoundTrip(Lead([Note(At(1), 1, 5, new Fraction(1, 1))], [chord]));

        var again = Assert.Single(back.Chords);
        Assert.True(again.Accent);
        Assert.Equal([(0, true), (1, false), (2, false)],
            again.ChordNotes.OrderBy(n => n.StringIndex).Select(n => (n.StringIndex, n.Accent)));
    }

    /// <summary>A chord accented as a whole, with no note of its own accented (a chart, not a
    /// tab, authored it), still comes back an accented chord.</summary>
    [Fact]
    public void AChordAccentedAsAWhole_ComesBackAccented()
    {
        var chord = new Chord { Position = At(0), Accent = true, NotationDuration = new Fraction(1, 1) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 3 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5 });

        var back = RoundTrip(Lead([Note(At(1), 1, 5, new Fraction(1, 1))], [chord]));

        Assert.True(Assert.Single(back.Chords).Accent);
    }
}
