using System.Globalization;
using System.Text.RegularExpressions;
using DotCharter.Services.Export.Sheet;

namespace DotCharter.Tests.Services.Export.Sheet;

/// <summary>
///     #286: an RS-XML import engraved a tempo marking on nearly every bar. Its beats sit
///     on whole milliseconds, so the tempo each beat implies wobbles by a few tenths of a
///     BPM, and the old rule marked any bar whose first beat moved more than 0.05.
/// </summary>
public sealed partial class SheetTempoMarkingTests
{
    // Decimals too: the old exporter printed "120.00", and this must count ITS markings.
    [GeneratedRegex(@"\\tempo \(([0-9.]+)\)")]
    private static partial Regex TempoMarking();

    /// <summary>A beat map as an RS-XML import leaves it: every beat on a whole
    /// millisecond, each beat's tempo implied by the gap to the next.</summary>
    private static List<BeatMarker> ImportedBeats(int bars, Func<int, double> bpmAtBeat)
    {
        var times = new List<int> { 0 };
        var exact = 0.0;
        for (var b = 0; b < bars * 4; b++)
        {
            exact += 60_000.0 / bpmAtBeat(b);
            times.Add((int)Math.Round(exact));
        }

        var beats = new List<BeatMarker>();
        for (var b = 0; b < bars * 4; b++)
        {
            beats.Add(new BeatMarker
            {
                Time = times[b],
                Bpm = 60_000.0 / (times[b + 1] - times[b]),
                BeatsInMeasure = 4,
                NoteDenominator = 4
            });
        }

        return beats;
    }

    private static List<double> Markings(List<BeatMarker> beats)
    {
        var project = new ChartProject();
        var arr = new Arrangement { Name = "Lead", StringCount = 6 };
        var level = new ChartLevel();
        arr.Levels.Add(level);
        project.Arrangements.Add(arr);
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(0), StringIndex = 0, Fret = 3 });

        var tex = AlphaTexExporter.Export(project, arr, level, beats).AlphaTex;
        return TempoMarking().Matches(tex).Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
    }

    [Fact]
    public void AMillisecondGrid_AtOneTempo_IsMarkedOnce()
    {
        var beats = ImportedBeats(bars: 24, _ => 117.3);
        Assert.True(beats.Select(b => Math.Round(b.Bpm, 2)).Distinct().Count() > 1,
            "the fixture must wobble the way an imported grid does");

        Assert.Equal([117.0], Markings(beats));
    }

    [Fact]
    public void ARealTempoChange_IsMarkedWhereItHappens()
    {
        var beats = ImportedBeats(bars: 16, b => b < 32 ? 120 : 140);

        Assert.Equal([120.0, 140.0], Markings(beats));
    }

    /// <summary>The step is measured from the last MARKING, so a slow drift of a tenth
    /// per bar is marked once it adds up rather than never.</summary>
    [Fact]
    public void ASlowDrift_IsMarkedOnceItAddsUp()
    {
        var beats = ImportedBeats(bars: 30, b => 100 + b / 4 * 0.1);

        var markings = Markings(beats);

        Assert.InRange(markings.Count, 3, 4);
        Assert.Equal(100.0, markings[0]);
        Assert.True(markings.Zip(markings.Skip(1)).All(p => p.Second - p.First == 1.0), string.Join(", ", markings));
    }

    [Theory]
    [InlineData(119.5, 120)]
    [InlineData(120.49, 120)]
    [InlineData(120.5, 121)]
    public void TheMarkingIsAWholeNumber_HalfRoundingUp(double bpm, int engraved) =>
        Assert.Equal(engraved, SheetTempoMarking.Engraved(bpm));
}
