namespace DotCharter.Tests.Services.Editing.Maintenance;

/// <summary>
///     Insert beat / Insert measures at the very first beat used the gap between the
///     first two beats as the new beats' length. On a hand-mapped grid that gap is not
///     the bar's tempo, so four inserts built a first bar at the wrong BPM (the owner saw
///     81 become 102.59). At the first beat both verbs take the beat length Add measures
///     at start uses: the first beat's tempo and time signature.
/// </summary>
public sealed class TempoMapInsertAtFirstBeatTests
{
    /// <summary>The harness grid (32 beats at 120 BPM, 4/4) with a short first gap: beat 1 at 400 ms.</summary>
    private static ChartPowerToolsTests.Harness WithShortFirstGap(double offsetMs = 0)
    {
        var h = new ChartPowerToolsTests.Harness();
        var grid = h.State.ActiveProject!.Beats;
        grid[1].Time = 400;
        foreach (var beat in grid) beat.Time += offsetMs;
        h.State.CurrentTimeMs = 0;
        return h;
    }

    private static List<double> Times(ChartPowerToolsTests.Harness h) =>
        h.State.ActiveProject!.Beats.Select(b => b.Time).ToList();

    /// <summary>Each bar's length in ms, from the bar lines of <paramref name="beats" />.</summary>
    private static List<double> BarSpans(IList<BeatMarker> beats)
    {
        var lines = beats.Where(b => b.IsMeasure).Select(b => b.Time).ToList();
        return lines.Zip(lines.Skip(1), (a, b) => b - a).ToList();
    }

    [Fact]
    public void InsertBeat_FourTimesAtTheFirstBeat_AddsABarAtTheFirstBeatsTempo()
    {
        var h = WithShortFirstGap();
        var spansBefore = BarSpans(h.State.ActiveProject!.Beats);

        for (var i = 0; i < 4; i++)
        {
            h.State.CurrentTimeMs = h.State.ActiveProject!.Beats[0].Time;
            h.Maintenance.InsertBeatAtPlayhead();
            // The owner undid and redid in between.
            h.History.Undo();
            h.History.Redo();
        }

        var beats = h.State.ActiveProject!.Beats;
        Assert.Equal(36, beats.Count);
        Assert.Equal(new[] { 0.0, 500, 1000, 1500, 2000 }, beats.Take(5).Select(b => b.Time));
        // The four new beats join bar 1 (now 8/4) at 120 BPM; every other bar keeps its length and tempo.
        Assert.Equal(new List<double> { 2000 + spansBefore[0] }.Concat(spansBefore.Skip(1)), BarSpans(beats));
        Assert.Equal(8, beats[0].BeatsInMeasure);
        Assert.All(beats, b => Assert.Equal(120, b.Bpm));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3000)]
    public void InsertMeasures_AtOrBeforeTheFirstBeat_UsesTheFirstBeatsTempo(double offsetMs)
    {
        var h = WithShortFirstGap(offsetMs);
        var spansBefore = BarSpans(h.State.ActiveProject!.Beats);

        h.Maintenance.InsertMeasuresAtPlayhead(1);

        var beats = h.State.ActiveProject!.Beats;
        Assert.Equal(36, beats.Count);
        Assert.Equal(new[] { 0.0, 500, 1000, 1500, 2000 }.Select(t => t + offsetMs), beats.Take(5).Select(b => b.Time));
        Assert.Equal(new List<double> { 2000 }.Concat(spansBefore), BarSpans(beats));
        Assert.All(beats, b => Assert.Equal(120, b.Bpm));
    }

    [Fact]
    public void InsertBeatAndMeasuresAtTheFirstBeat_UndoAndRedoRestoreTheExactBeatTimes()
    {
        var h = WithShortFirstGap();
        var original = Times(h);

        h.Maintenance.InsertBeatAtPlayhead();
        var afterBeat = Times(h);
        h.State.CurrentTimeMs = 0;
        h.Maintenance.InsertMeasuresAtPlayhead(2);
        var afterMeasures = Times(h);

        h.History.Undo();
        Assert.Equal(afterBeat, Times(h));
        h.History.Undo();
        Assert.Equal(original, Times(h));
        h.History.Redo();
        Assert.Equal(afterBeat, Times(h));
        h.History.Redo();
        Assert.Equal(afterMeasures, Times(h));
    }
}
