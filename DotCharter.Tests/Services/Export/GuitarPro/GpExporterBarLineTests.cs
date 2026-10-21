using DotCharter.Services.Export.GuitarPro;
using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Export.GuitarPro;

/// <summary>
///     What crosses a bar line survives export → re-import. The tab-corpus round trip
///     showed two exporter losses at the bar line: a sound longer than the rest of its bar
///     was cut there (the re-import then read the gap as a staccato rest and ACCENTED the
///     note), and a hammer-on whose destination opens the next bar lost its origin mark.
/// </summary>
public sealed class GpExporterBarLineTests
{
    private static List<BeatMarker> Beats44(int count) => [.. Enumerable.Range(0, count).Select(i => new BeatMarker
    {
        Time = i * 500.0, Bpm = 120, BeatsInMeasure = 4, NoteDenominator = 4, IsMeasure = i % 4 == 0
    })];

    private static Arrangement Lead(params GuitarNote[] notes)
    {
        var arrangement = new Arrangement { Name = "Lead", StringCount = 6 };
        for (var i = 0; i < 6; i++) arrangement.TuningPitches.Add(new TuningOffset(0));
        var level = new ChartLevel();
        foreach (var note in notes) level.Notes.Add(note);
        arrangement.Levels.Add(level);
        return arrangement;
    }

    private static List<GuitarNote> RoundTrip(Arrangement arrangement)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-barline-{Guid.NewGuid():N}.gp");
        try
        {
            File.WriteAllBytes(path, GpExporter.ExportToBytes(arrangement, arrangement.Levels[0], Beats44(12)));
            var importer = new GpImporter();
            var back = importer.ImportTracks(path, [.. importer.GetAvailableTracks(path).Select(t => t.Id)]);
            return [.. Assert.Single(back).Levels[0].Notes.OrderBy(n => n.Position)];
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static FractionalPosition At(int beat, int eighths = 0) => new(beat, new Fraction(eighths, 2));

    /// <summary>An authored quarter on the last off-beat of bar 1 (an eighth tied into bar 2).</summary>
    [Fact]
    public void ANoteTiedAcrossTheBarLine_KeepsItsLength_AndGainsNoAccent()
    {
        var back = RoundTrip(Lead(
            new GuitarNote { Position = At(3, 1), StringIndex = 1, Fret = 5, NotationDuration = new Fraction(1, 1) },
            new GuitarNote { Position = At(4, 1), StringIndex = 1, Fret = 7, NotationDuration = new Fraction(1, 2) }));

        Assert.Equal(2, back.Count);
        Assert.Equal((At(3, 1), new Fraction(1, 1), false), (back[0].Position, back[0].NotationDuration!.Value, back[0].Accent));
        Assert.Equal(At(4, 1), back[1].Position);
    }

    /// <summary>A whole note's worth of sustain from beat 2 runs through bar 2 into bar 3.</summary>
    [Fact]
    public void ASustainLongerThanTheNextBar_IsTiedThroughIt()
    {
        var back = RoundTrip(Lead(
            new GuitarNote { Position = At(2), StringIndex = 1, Fret = 5, NotationDuration = new Fraction(8, 1) },
            new GuitarNote { Position = At(10), StringIndex = 1, Fret = 7, NotationDuration = new Fraction(1, 1) }));

        Assert.Equal(2, back.Count);
        Assert.Equal(new Fraction(8, 1), back[0].NotationDuration);
        Assert.Equal(At(10), back[1].Position);
    }

    /// <summary>The hammer-on lands on the first note of bar 2; its origin is bar 1's last.</summary>
    [Fact]
    public void AHammerOnAcrossTheBarLine_SurvivesTheRoundTrip()
    {
        var back = RoundTrip(Lead(
            new GuitarNote { Position = At(3), StringIndex = 1, Fret = 5, NotationDuration = new Fraction(1, 1) },
            new GuitarNote { Position = At(4), StringIndex = 1, Fret = 7, Hopo = HopoType.HammerOn, NotationDuration = new Fraction(1, 1) }));

        Assert.Equal(HopoType.HammerOn, back[1].Hopo);
    }

    /// <summary>Guitar Pro marks the hammer-on ORIGIN, which is the previous sound on the
    /// destination's string — not the previous event, here a note on another string.</summary>
    [Fact]
    public void AHammerOnAfterANoteOnAnotherString_SurvivesTheRoundTrip()
    {
        var back = RoundTrip(Lead(
            new GuitarNote { Position = At(0), StringIndex = 1, Fret = 5, NotationDuration = new Fraction(1, 1) },
            new GuitarNote { Position = At(1), StringIndex = 2, Fret = 3, NotationDuration = new Fraction(1, 1) },
            new GuitarNote { Position = At(2), StringIndex = 1, Fret = 7, Hopo = HopoType.HammerOn, NotationDuration = new Fraction(1, 1) }));

        Assert.Equal(HopoType.HammerOn, Assert.Single(back, n => n.Fret == 7).Hopo);
    }
}
