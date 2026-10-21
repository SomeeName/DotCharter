using DotCharter.Core.Linking;
using DotCharter.Services.Import.GuitarPro.Report;

namespace DotCharter.Tests.Services.Import.GuitarPro.Report;

/// <summary>The tab-corpus round trip names WHY a measure changed, one class per difference.</summary>
public sealed class GpRoundTripDiffTests
{
    private static readonly List<BeatMarker> Beats = [.. Enumerable.Range(0, 8).Select(i => new BeatMarker
    {
        Time = i * 500.0, Bpm = 120, BeatsInMeasure = 4, NoteDenominator = 4, IsMeasure = i % 4 == 0
    })];

    private static MeasureContentIndex Index(params GuitarNote[] notes)
    {
        var arrangement = new Arrangement { Name = "Lead", StringCount = 6 };
        for (var i = 0; i < 6; i++) arrangement.TuningPitches.Add(new TuningOffset(0));
        var level = new ChartLevel();
        foreach (var note in notes) level.Notes.Add(note);
        arrangement.Levels.Add(level);
        return new MeasureContentIndex(arrangement, level, Beats, MeasureContentOptions.SoundsOnly);
    }

    private static GuitarNote Note(int beat, int fret, HopoType hopo = HopoType.None, HarmonicType harmonic = HarmonicType.None) =>
        new() { Position = new FractionalPosition(beat, Fraction.Zero), StringIndex = 1, Fret = fret, Hopo = hopo, Harmonic = harmonic };

    [Fact]
    public void EachUnequalMeasure_IsExplainedByItsDifferences()
    {
        var before = Index(Note(0, 5, HopoType.HammerOn), Note(4, 3, harmonic: HarmonicType.Normal), Note(5, 7));
        var after = Index(Note(0, 5), Note(4, 3), Note(5, 8));

        var classes = GpRoundTripDiff.Classify(before, after);

        Assert.Equal(1, classes["hopo:from-nothing"]);
        Assert.Equal(1, classes["only:hopo:from-nothing"]);
        Assert.Equal(1, classes["harmonic"]);
        Assert.Equal(1, classes["fret"]);
        Assert.False(classes.ContainsKey("only:harmonic"));
    }

    [Fact]
    public void EqualMeasures_HaveNoClass() =>
        Assert.Empty(GpRoundTripDiff.Classify(Index(Note(0, 5), Note(4, 3)), Index(Note(0, 5), Note(4, 3))));
}
