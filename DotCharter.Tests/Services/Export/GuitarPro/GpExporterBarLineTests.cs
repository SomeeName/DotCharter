using DotCharter.Services.Export.GuitarPro;
using DotCharter.Services.Infrastructure;
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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RescaledImportedBendsRemainExportable(bool fallback)
    {
        var start = new FractionalPosition(4, new Fraction(1, 65521));
        var oldSustain = fallback ? new FractionalPosition(1, new Fraction(1, 65519)) : new FractionalPosition(1);
        var offset = fallback ? new Fraction(1, 4) : new Fraction(1, 2);
        var note = new GuitarNote
        {
            Position = start, StringIndex = 1, Fret = 5, Sustain = new FractionalPosition(2),
            BendValues = { new BendValue(start, 0), new BendValue(start + offset, 2), new BendValue(start + 2, 0) }
        };
        var expected = offset.DoubleValue * 2 / oldSustain.DoubleValue;
        BendMath.RescaleToSustain(note.BendValues, start, oldSustain, note.Sustain);

        var back = RoundTrip(Lead(note, new GuitarNote { Position = At(8), StringIndex = 1, Fret = 7 }));

        var exported = back.First(n => n.Fret == 5);
        var peak = exported.BendValues.MaxBy(b => b.Step)!;
        Assert.Equal(2, peak.Step);
        Assert.InRange(Math.Abs((peak.Position - exported.Position).DoubleValue - expected), 0, 1.0 / 48);
    }

    [Fact]
    public void ATiedBendInsideOneBar_KeepsItsPeakAndRelease()
    {
        var note = new GuitarNote
        {
            Position = At(0), StringIndex = 1, Fret = 5, Sustain = new FractionalPosition(1, new Fraction(3, 4)),
            BendValues =
            {
                new BendValue { Position = At(0), Step = 0 },
                new BendValue { Position = At(1, 1), Step = 2 },
                new BendValue { Position = new FractionalPosition(1, new Fraction(3, 4)), Step = 0 }
            }
        };
        var back = RoundTrip(Lead(note,
            new GuitarNote { Position = new FractionalPosition(1, new Fraction(3, 4)), StringIndex = 1, Fret = 7 }));
        Assert.Equal(At(1, 1), back[0].BendValues.MaxBy(b => b.Step)!.Position);
        Assert.Equal(0, back[0].BendValues.Last().Step);
    }

    [Fact]
    public void TiedChunksMeetAtTheInterpolatedBoundaryStrength()
    {
        var sound = new GuitarNote
        {
            Position = At(2), Sustain = At(4),
            BendValues =
            {
                new BendValue { Position = At(2), Step = 0 },
                new BendValue { Position = At(5), Step = 3 },
                new BendValue { Position = At(6), Step = 0 }
            }
        };
        AlphaTabRuntime.RunExclusive(() =>
        {
            var first = new AlphaTab.Model.Note();
            var second = new AlphaTab.Model.Note();
            GpBendCurve.AddTo(sound, sound.Position, At(2), At(4), first);
            GpBendCurve.AddTo(sound, sound.Position, At(4), At(6), second);
            var firstPoints = first.BendPoints!;
            var secondPoints = second.BendPoints!;
            // At beat 4 the original ramp has reached two semitones (four quarter tones).
            Assert.Equal(4, firstPoints[^1].Value);
            Assert.Equal(4, secondPoints[0].Value);
            Assert.Equal(60, firstPoints[^1].Offset);
            Assert.Equal(0, secondPoints[0].Offset);
            Assert.Equal((30.0, 6.0), (secondPoints[1].Offset, secondPoints[1].Value));
            Assert.Equal(0, secondPoints[^1].Value);
            return 0;
        });
    }

    [Theory]
    [InlineData(2, 4, 5)]
    [InlineData(2, 8, 7)]
    [InlineData(0, 2, 1)]
    public void ATiedBendKeepsItsPeakAtTheAuthoredBeat(int start, int duration, int peak)
    {
        var note = new GuitarNote
        {
            Position = At(start), StringIndex = 1, Fret = 5,
            Sustain = At(duration), NotationDuration = new Fraction(duration, 1),
            BendValues =
            {
                new BendValue { Position = At(start), Step = 0 },
                new BendValue { Position = At(peak), Step = 2 },
                new BendValue { Position = At(start + duration), Step = 0 }
            }
        };
        var back = RoundTrip(Lead(note,
            new GuitarNote { Position = At(start + duration), StringIndex = 1, Fret = 7 }));
        var bends = back[0].BendValues;
        Assert.Equal(At(peak), bends.MaxBy(b => b.Step)!.Position);
        Assert.Equal(2, bends.Max(b => b.Step));
        Assert.Equal(0, bends.Last().Step);
        Assert.Contains(bends, b => b.Position == At(start + duration));
    }

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheFinalNote_CarriesThroughOtherwiseEmptyBars(bool authored)
    {
        var note = new GuitarNote
        {
            Position = At(2), StringIndex = 1, Fret = 5, Sustain = At(8),
            NotationDuration = authored ? new Fraction(8, 1) : null
        };

        var back = Assert.Single(RoundTrip(Lead(note)));

        Assert.Equal(At(2), back.Position);
        Assert.Equal(new Fraction(8, 1), back.NotationDuration);
        Assert.Equal(At(8), back.Sustain);
    }

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
