namespace DotCharter.Tests.Services.Tempo;

/// <summary>
///     The suggested offset (#403): the shift that puts the most note onsets on a
///     1/4- or 1/3-beat line. The planted chart has three tempos and triplets, so a
///     quarter-beat shift — which lines straight sixteenths up just as well — loses.
/// </summary>
public sealed class BeatGridOffsetEstimatorTests
{
    [Theory]
    [InlineData(-121.0)]
    [InlineData(37.0)]
    [InlineData(-203.5)]
    public void RecoversAPlantedOffset(double gridErrorMs)
    {
        var project = BeatGridFixtures.Chart(gridErrorMs);

        var estimate = BeatGridOffsetEstimator.Estimate(project);

        Assert.NotNull(estimate);
        Assert.Equal(-gridErrorMs, estimate.OffsetMs, 0.5);
        Assert.True(estimate.MedianOffNowMs > 10, $"median off now {estimate.MedianOffNowMs:0.0} ms on a grid {gridErrorMs} ms off");
        Assert.True(estimate.MedianOffAfterMs < 0.5, $"median off after {estimate.MedianOffAfterMs:0.0} ms");
    }

    /// <summary>
    ///     One tempo, no triplets: a quarter-beat shift lines every note up just as
    ///     well as the true one, so the median alone cannot choose. The shift that
    ///     puts the beats ON the beat wins — not the smallest move, which here is
    ///     4 ms the wrong way and reads every downbeat as a sixteenth.
    /// </summary>
    [Fact]
    public void StraightSixteenths_PreferTheShiftThatPutsTheBeatsOnTheBeat()
    {
        var truth = BeatGridFixtures.Grid(2000, (60, 120));
        var shipped = truth.Select(b => new BeatMarker { Time = b.Time - 121, Bpm = 120, IsMeasure = b.IsMeasure }).ToList();
        var onsets = new List<double>();
        for (var beat = 1; beat < 56; beat++)
        {
            onsets.Add(new FractionalPosition(beat).GetPositionMs(truth));
            onsets.Add(new FractionalPosition(beat, new Fraction(1, 2)).GetPositionMs(truth));
            if (beat % 2 == 0) onsets.Add(new FractionalPosition(beat, new Fraction(1, 4)).GetPositionMs(truth));
        }

        var estimate = BeatGridOffsetEstimator.Estimate(onsets, shipped);

        Assert.NotNull(estimate);
        Assert.Equal(121, estimate.OffsetMs, 0.5);
    }

    [Fact]
    public void AnAlignedChart_NeedsNoShift()
    {
        var estimate = BeatGridOffsetEstimator.Estimate(BeatGridFixtures.Chart(gridErrorMs: 0));

        Assert.NotNull(estimate);
        Assert.Equal(0, estimate.OffsetMs, 0.5);
        Assert.True(estimate.MedianOffNowMs < 0.5);
    }

    /// <summary>The suggestion, applied, aligns the chart it was measured on — the two halves agree.</summary>
    [Fact]
    public void TheSuggestionApplied_LeavesNothingToSuggest()
    {
        var project = BeatGridFixtures.Chart(gridErrorMs: -121);
        var estimate = BeatGridOffsetEstimator.Estimate(project)!;

        BeatGridShift.Apply(project, estimate.OffsetMs);

        Assert.Equal(0, BeatGridOffsetEstimator.Estimate(project)!.OffsetMs, 0.5);
    }

    [Fact]
    public void IsDeterministic()
    {
        var project = BeatGridFixtures.Chart(gridErrorMs: -121);

        Assert.Equal(BeatGridOffsetEstimator.Estimate(project), BeatGridOffsetEstimator.Estimate(project));
    }

    [Fact]
    public void TooFewNotes_SaysNothing()
    {
        var project = BeatGridFixtures.Chart(gridErrorMs: -121);
        var level = project.Arrangements[0].Levels[0];
        while (level.Notes.Count >= BeatGridOffsetEstimator.MinOnsets) level.Notes.RemoveAt(level.Notes.Count - 1);

        Assert.Null(BeatGridOffsetEstimator.Estimate(project));
    }

    /// <summary>The same onset in two levels or two arrangements is one sound, not two votes.</summary>
    [Fact]
    public void Onsets_AreDistinctAcrossLevelsAndArrangements()
    {
        var project = BeatGridFixtures.Chart(gridErrorMs: 0);
        var copy = new ChartLevel();
        foreach (var note in project.Arrangements[0].Levels[0].Notes)
            copy.Notes.Add(new GuitarNote { Position = note.Position, Fret = 1 });
        var second = new Arrangement { Name = "Rhythm" };
        second.Levels.Add(copy);
        project.Arrangements.Add(second);

        var onsets = BeatGridOffsetEstimator.Onsets(project, project.Beats);

        Assert.Equal(BeatGridFixtures.NotePositions().Count, onsets.Count);
    }
}
