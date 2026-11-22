namespace DotCharter.Tests.Services.Editing.Maintenance;

/// <summary>
///     Insert Beat / Remove Beat at the playhead change how many beats ONE bar holds. Every
///     bar line stays on its beat: the bar that holds the pivot becomes one beat longer (4/4
///     reads 5/4) or shorter (3/4) and says so, and every other bar, with the linked-measure
///     groups after it, keeps its bar lines and signature. Insert Measures at the playhead is
///     the verb that adds WHOLE bars mid-song, in a time signature and tempo of its own.
/// </summary>
public sealed class TempoMapMeasureReflowTests
{
    /// <summary>The harness grid: 32 beats, 500 ms apart, 4/4, bar lines on every fourth beat.</summary>
    private static ChartPowerToolsTests.Harness At(int beat)
    {
        var h = new ChartPowerToolsTests.Harness();
        h.State.CurrentTimeMs = h.State.ActiveProject!.Beats[beat].Time;
        return h;
    }

    private static List<int> DownbeatIndexes(IList<BeatMarker> beats) =>
        Enumerable.Range(0, beats.Count).Where(i => beats[i].IsMeasure).ToList();

    private static List<int> Signatures(IList<BeatMarker> beats) =>
        beats.Where(b => b.IsMeasure).Select(b => b.BeatsInMeasure).ToList();

    private static List<FractionalPosition> NotePositions(ChartPowerToolsTests.Harness h) =>
        h.State.ActiveArrangement!.Levels[0].Notes.Select(n => n.Position).ToList();

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(4, 1)]
    [InlineData(9, 2)]
    public void InsertBeat_MakesOnlyThePivotsBarOneBeatLonger(int pivot, int bar)
    {
        var h = At(pivot);

        h.Maintenance.InsertBeatAtPlayhead();

        var beats = h.State.ActiveProject!.Beats;
        Assert.Equal(33, beats.Count);
        var expected = Enumerable.Repeat(4, 8).ToList();
        expected[bar] = 5;
        Assert.Equal(expected, Signatures(beats));
        // Every bar line after the pivot's bar stays on its old beat, one index later.
        Assert.Equal(Enumerable.Range(0, 8).Select(b => b * 4 + (b > bar ? 1 : 0)), DownbeatIndexes(beats));
        // Every beat of the longer bar carries its new signature.
        Assert.All(beats.Skip(bar * 4).Take(5), b => Assert.Equal(5, b.BeatsInMeasure));
    }

    [Fact]
    public void InsertBeat_TwiceInBarOne_MakesItSixFour()
    {
        // The owner's report: two inserts in M1. The bar now holds six beats and says so.
        var h = At(1);

        h.Maintenance.InsertBeatAtPlayhead();
        h.State.CurrentTimeMs = h.State.ActiveProject!.Beats[1].Time;
        h.Maintenance.InsertBeatAtPlayhead();

        var beats = h.State.ActiveProject!.Beats;
        Assert.Equal(new[] { 0, 6, 10, 14, 18, 22, 26, 30 }, DownbeatIndexes(beats));
        Assert.Equal(6, beats[0].BeatsInMeasure);
        Assert.Equal("M2.1", ChartPositionLabel.Measure(beats, beats[6].Time));
    }

    [Fact]
    public void InsertBeat_InTheLastBar_GrowsItsOpenLabel()
    {
        var h = At(30);

        h.Maintenance.InsertBeatAtPlayhead();

        var beats = h.State.ActiveProject!.Beats;
        Assert.Equal(new[] { 4, 4, 4, 4, 4, 4, 4, 5 }, Signatures(beats));
    }

    [Fact]
    public void InsertBeat_NotesKeepTheirBeatPositions()
    {
        var h = At(2);

        h.Maintenance.InsertBeatAtPlayhead();

        // Notes sat on beats 0..5; the ones at or after the pivot move one beat on, with the grid.
        Assert.Equal(new[] { 0, 1, 3, 4, 5, 6 }.Select(b => new FractionalPosition(b)), NotePositions(h));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(3, 0)]
    [InlineData(4, 1)]
    [InlineData(7, 1)]
    [InlineData(9, 2)]
    public void RemoveBeat_MakesOnlyThePivotsBarOneBeatShorter(int pivot, int bar)
    {
        var h = At(pivot);

        h.Maintenance.RemoveBeatAtPlayhead();

        var beats = h.State.ActiveProject!.Beats;
        Assert.Equal(31, beats.Count);
        var expected = Enumerable.Repeat(4, 8).ToList();
        expected[bar] = 3;
        Assert.Equal(expected, Signatures(beats));
        Assert.Equal(Enumerable.Range(0, 8).Select(b => b * 4 - (b > bar ? 1 : 0)), DownbeatIndexes(beats));
    }

    /// <summary>
    ///     The only beat of a one-beat bar: removing it removes that bar. The beats around it
    ///     belong to the bars they belonged to, so those keep their lengths and signatures.
    /// </summary>
    [Fact]
    public void RemoveBeat_TheOnlyBeatOfAOneBeatBar_RemovesThatBar()
    {
        var h = new ChartPowerToolsTests.Harness();
        var grid = h.State.ActiveProject!.Beats;
        // Bars 0, 4, 8, 12 of 4/4, a one-beat bar at 16, then 4/4 bars at 17, 21, 25 and an open bar at 29.
        for (var i = 16; i < grid.Count; i++)
            grid[i].IsMeasure = i == 16 || (i - 17) % 4 == 0;
        grid[16].BeatsInMeasure = 1;
        h.State.CurrentTimeMs = grid[16].Time;

        h.Maintenance.RemoveBeatAtPlayhead();

        var beats = h.State.ActiveProject!.Beats;
        Assert.Equal(new[] { 0, 4, 8, 12, 16, 20, 24, 28 }, DownbeatIndexes(beats));
        Assert.Equal(new[] { 4, 4, 4, 4, 4, 4, 4, 4 }, Signatures(beats));
    }

    [Fact]
    public void RemoveBeat_NotesKeepTheirBeatPositions()
    {
        var h = At(2);

        h.Maintenance.RemoveBeatAtPlayhead();

        // The note ON the removed beat goes with it; later notes slide back one beat with the grid.
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }.Select(b => new FractionalPosition(b)), NotePositions(h));
    }

    /// <summary>
    ///     An RS XML import with no time-signature events labels every bar 4/4, whatever its real
    ///     length. Insert Beat touches only the pivot's bar, so a three-beat bar later on keeps
    ///     its bar lines and its label.
    /// </summary>
    [Fact]
    public void InsertBeat_LeavesALaterMislabelledBarAlone()
    {
        var h = new ChartPowerToolsTests.Harness();
        var grid = h.State.ActiveProject!.Beats;
        for (var i = 16; i < grid.Count; i++)
            grid[i].IsMeasure = i == 16 || (i - 19) % 4 == 0;
        h.State.CurrentTimeMs = grid[1].Time;

        h.Maintenance.InsertBeatAtPlayhead();

        var beats = h.State.ActiveProject!.Beats;
        Assert.Equal(new[] { 0, 5, 9, 13, 17, 20, 24, 28, 32 }, DownbeatIndexes(beats));
        Assert.Equal(new[] { 5, 4, 4, 4, 4, 4, 4, 4, 4 }, Signatures(beats));
    }

    [Fact]
    public void InsertMeasures_InTheMiddle_AddsWholeBarsBeforeThePlayheadsBar()
    {
        var h = At(9);
        var level = h.State.ActiveArrangement!.Levels[0];
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(10), Fret = 5 });
        Assert.False(h.History.CanUndo);

        h.Maintenance.InsertMeasuresAtPlayhead(2);

        var beats = h.State.ActiveProject!.Beats;
        Assert.Equal(40, beats.Count);
        Assert.Equal(Enumerable.Range(0, 10).Select(b => b * 4), DownbeatIndexes(beats));
        // The new bars start where bar 3 started (4000 ms) and keep its 500 ms beat.
        Assert.Equal(4000, beats[8].Time, 3);
        Assert.Equal(7500, beats[15].Time, 3);
        Assert.Equal(8000, beats[16].Time, 3);
        Assert.All(beats.Skip(8).Take(8), b => Assert.Equal((4, 4), (b.BeatsInMeasure, b.NoteDenominator)));
        // Before the bar: untouched; from the bar on: eight beats later.
        Assert.Equal(new FractionalPosition(5), level.Notes[5].Position);
        Assert.Equal(new FractionalPosition(18), level.Notes[6].Position);

        h.History.Undo();

        Assert.False(h.History.CanUndo);
        Assert.Equal(32, h.State.ActiveProject!.Beats.Count);
        Assert.Equal(new FractionalPosition(10), h.State.ActiveArrangement!.Levels[0].Notes[6].Position);
    }

    [Fact]
    public void InsertMeasures_UsesThePlayheadBarsTempoAndTimeSignature()
    {
        var h = new ChartPowerToolsTests.Harness();
        var grid = h.State.ActiveProject!.Beats;
        // From beat 16 on: 3/4 at 150 BPM (400 ms beats).
        for (var i = 16; i < grid.Count; i++)
        {
            grid[i].BeatsInMeasure = 3;
            grid[i].Bpm = 150;
            grid[i].IsMeasure = (i - 16) % 3 == 0;
            grid[i].Time = 8000 + (i - 16) * 400;
        }
        h.State.CurrentTimeMs = grid[17].Time;

        Assert.Equal(new MeasureInsertTempo(3, 4, 150), h.Maintenance.PlayheadBarTempo());
        h.Maintenance.InsertMeasuresAtPlayhead(1);

        var beats = h.State.ActiveProject!.Beats;
        Assert.Equal(35, beats.Count);
        Assert.True(beats[16].IsMeasure);
        Assert.False(beats[17].IsMeasure);
        Assert.True(beats[19].IsMeasure);
        Assert.Equal(new[] { 8000.0, 8400, 8800, 9200 }, beats.Skip(16).Take(4).Select(b => b.Time));
        Assert.All(beats.Skip(16).Take(3), b => Assert.Equal((3, 150.0), (b.BeatsInMeasure, b.Bpm)));
    }

    /// <summary>
    ///     The owner's request: the dialog picks the inserted bars' time signature and tempo.
    ///     Two bars of 3/4 in a 4/4 song are six beats labelled 3/4 at the chosen tempo, and the
    ///     bar after them still reads 4/4 at its own tempo, all in one undo step.
    /// </summary>
    [Fact]
    public void InsertMeasures_InAChosenSignatureAndTempo_AddsExactlyThoseBeats()
    {
        var h = At(9);

        h.Maintenance.InsertMeasuresAtPlayhead(2, new MeasureInsertTempo(3, 4, 90));

        var beats = h.State.ActiveProject!.Beats;
        Assert.Equal(38, beats.Count);
        Assert.Equal(new[] { 0, 4, 8, 11, 14, 18, 22, 26, 30, 34 }, DownbeatIndexes(beats));
        Assert.All(beats.Skip(8).Take(6), b => Assert.Equal((3, 4, 90.0), (b.BeatsInMeasure, b.NoteDenominator, b.Bpm)));
        // 90 BPM: 666.67 ms a quarter-note beat; the old bar 3 starts six of them later.
        Assert.Equal(4000 + 6 * 60000.0 / 90, beats[14].Time, 6);
        Assert.Equal(4000 + 60000.0 / 90, beats[9].Time, 6);
        Assert.Equal((4, 4, 120.0), (beats[14].BeatsInMeasure, beats[14].NoteDenominator, beats[14].Bpm));
        Assert.Equal(500, beats[15].Time - beats[14].Time, 6);

        h.History.Undo();

        Assert.False(h.History.CanUndo);
        Assert.Equal(32, h.State.ActiveProject!.Beats.Count);
    }

    [Fact]
    public void InsertMeasures_InSixEight_UsesEighthNoteBeats()
    {
        var h = At(9);

        h.Maintenance.InsertMeasuresAtPlayhead(1, new MeasureInsertTempo(6, 8, 120));

        var beats = h.State.ActiveProject!.Beats;
        Assert.Equal(38, beats.Count);
        // 120 quarter notes a minute: an eighth-note beat is 250 ms.
        Assert.Equal(new[] { 4000.0, 4250, 4500, 4750, 5000, 5250, 5500 }, beats.Skip(8).Take(7).Select(b => b.Time));
        Assert.All(beats.Skip(8).Take(6), b => Assert.Equal((6, 8), (b.BeatsInMeasure, b.NoteDenominator)));
    }

    [Fact]
    public void InsertMeasures_RespectsTheBeatMapLock()
    {
        var h = At(9);
        h.State.ActiveProject!.IsBeatMapLocked = true;

        h.Maintenance.InsertMeasuresAtPlayhead(2);

        Assert.Equal(32, h.State.ActiveProject!.Beats.Count);
    }
}
