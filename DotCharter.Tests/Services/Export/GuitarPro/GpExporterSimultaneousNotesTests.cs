using System.Text.RegularExpressions;
using DotCharter.Services.Export.GuitarPro;

namespace DotCharter.Tests.Services.Export.GuitarPro;

/// <summary>
///     A Guitar Pro BEAT is a chord of simultaneous notes, and the exporter used
///     to write only ONE of the chart events that share a position: the slot loop
///     kept the first and <c>continue</c>d past the rest.
///     <para>
///         That silently dropped strings from every shape the chart spells as
///         LOOSE NOTES rather than as a <c>Chord</c> — a double stop or an octave
///         drawn straight onto the highway, and every landing
///         <c>LinkLandingBreakup</c> produces (in the hand-corrected EOF corpus,
///         492 of 493 such landings carry 2-4 simultaneous notes). The written
///         <c>.gp</c> is asserted here, not a re-import: the chart the exporter
///         built is the only place the loss is visible.
///     </para>
/// </summary>
public sealed class GpExporterSimultaneousNotesTests
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

    private static Arrangement Arrangement()
    {
        var arrangement = new Arrangement { Name = "Lead Test", StringCount = 6 };
        arrangement.TuningPitches.Clear();
        for (var i = 0; i < 6; i++) arrangement.TuningPitches.Add(new TuningOffset(0));
        return arrangement;
    }

    private static GuitarNote Note(int beat, int stringIndex, int fret) => new()
    {
        Position = new FractionalPosition(beat),
        StringIndex = stringIndex,
        Fret = fret,
        Sustain = new FractionalPosition(1)
    };

    private static string Gpif(Arrangement arrangement, ChartLevel level, List<BeatMarker> beats)
    {
        var bytes = GpExporter.ExportToBytes(arrangement, level, beats, "Simultaneous", "DotCharter");
        using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(bytes));
        var entry = archive.Entries.Single(e =>
            e.FullName.EndsWith("score.gpif", StringComparison.OrdinalIgnoreCase));
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    /// <summary>(string, fret) for every note the gpif carries, in document order.</summary>
    private static List<(int String, int Fret)> WrittenNotes(string gpif)
    {
        var notes = gpif[gpif.IndexOf("<Notes>", StringComparison.Ordinal)..];
        var strings = Regex.Matches(notes, @"<Property name=""String""><String>(-?\d+)</String></Property>")
            .Select(m => int.Parse(m.Groups[1].Value)).ToList();
        var frets = Regex.Matches(notes, @"<Property name=""Fret""><Fret>(-?\d+)</Fret></Property>")
            .Select(m => int.Parse(m.Groups[1].Value)).ToList();
        Assert.Equal(strings.Count, frets.Count);
        return strings.Zip(frets).ToList();
    }

    [Fact]
    public void ThreeLooseNotesAtOnePosition_AllReachTheExportedGp()
    {
        var level = new ChartLevel();
        level.Notes.Add(Note(0, 2, 5));
        level.Notes.Add(Note(0, 3, 7));
        level.Notes.Add(Note(0, 4, 5));

        var written = WrittenNotes(Gpif(Arrangement(), level, Beats44(4)));

        Assert.Equal(3, written.Count);
        Assert.Contains((2, 5), written);
        Assert.Contains((3, 7), written);
        Assert.Contains((4, 5), written);
    }

    /// <summary>A chord and a loose note at the same position also share the
    /// beat — and a STRING claimed twice must still produce exactly one note,
    /// because alphaTab's <c>Score.Finish</c> assumes one per string.</summary>
    [Fact]
    public void AChordAndALooseNoteAtOnePosition_ShareOneBeatWithNoDuplicateString()
    {
        var level = new ChartLevel();
        var chord = new Chord { Position = new FractionalPosition(0) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 3, Sustain = new FractionalPosition(1) });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5, Sustain = new FractionalPosition(1) });
        level.Chords.Add(chord);
        level.Notes.Add(Note(0, 4, 12)); // a different string — must survive
        level.Notes.Add(Note(0, 1, 9));  // string 1 again — must NOT duplicate

        var written = WrittenNotes(Gpif(Arrangement(), level, Beats44(4)));

        Assert.Equal(3, written.Count);
        Assert.Single(written, n => n.String == 1);
        Assert.Contains((0, 3), written);
        Assert.Contains((4, 12), written);
    }

    /// <summary>The one-event case must be untouched — the primary event still
    /// decides the rhythm, so nothing about a single-note chart changes.</summary>
    [Fact]
    public void ASingleNotePerPosition_ExportsExactlyAsBefore()
    {
        var level = new ChartLevel();
        level.Notes.Add(Note(0, 0, 3));
        level.Notes.Add(Note(1, 1, 5));
        level.Notes.Add(Note(2, 2, 7));

        var written = WrittenNotes(Gpif(Arrangement(), level, Beats44(4)));

        Assert.Equal([(0, 3), (1, 5), (2, 7)], written);
    }
}
