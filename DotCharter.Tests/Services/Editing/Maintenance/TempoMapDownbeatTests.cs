namespace DotCharter.Tests.Services.Editing.Maintenance;

/// <summary>
///     The FIRST beat of a grid is a bar line — every importer writes it that
///     way and <see cref="ChartPositionLabel.Measure" /> counts bars by walking
///     <see cref="BeatMarker.IsMeasure" /> from index 0. Insert Beat and Remove
///     Beat at the playhead's nearest beat are the two verbs that can change
///     WHICH marker sits at index 0, and both dropped the flag there: the
///     inserted beat is always created with <c>IsMeasure = false</c>, and the
///     removal simply deletes index 0 and promotes a beat that was mid-bar.
///     <para>
///         The whole song then renumbers — bar 1 grows the dropped beat and
///         every later bar number is off by one — and nothing fails, because a
///         grid with no bar line at index 0 is still a legal ascending list.
///     </para>
/// </summary>
public sealed class TempoMapDownbeatTests
{
    /// <summary>The harness grid is 32 beats at 120 BPM, 4/4, bar lines every
    /// fourth beat — so index 0 is a downbeat and index 1 is not.</summary>
    private static ChartPowerToolsTests.Harness AtFirstBeat()
    {
        var h = new ChartPowerToolsTests.Harness();
        h.State.CurrentTimeMs = h.State.ActiveProject!.Beats[0].Time;
        return h;
    }

    [Fact]
    public void InsertBeatAtPlayhead_OnTheFirstBeat_KeepsTheGridStartingOnABarLine()
    {
        var h = AtFirstBeat();
        var barLinesBefore = h.State.ActiveProject!.Beats.Count(b => b.IsMeasure);

        h.Maintenance.InsertBeatAtPlayhead();

        var beats = h.State.ActiveProject!.Beats;
        Assert.True(beats[0].IsMeasure,
            "The beat inserted at index 0 became the start of the chart and must carry the bar line.");
        Assert.Equal(barLinesBefore, beats.Count(b => b.IsMeasure));
    }

    [Fact]
    public void RemoveBeatAtPlayhead_OnTheFirstBeat_KeepsTheGridStartingOnABarLine()
    {
        var h = AtFirstBeat();

        h.Maintenance.RemoveBeatAtPlayhead();

        var beats = h.State.ActiveProject!.Beats;
        Assert.True(beats[0].IsMeasure,
            "Removing the first beat promotes a mid-bar beat to index 0; it must inherit the bar line.");
    }

    /// <summary>
    ///     Insert then Remove at the same pivot is the identity on the beat
    ///     grid. It is the cheapest check that the two verbs agree about which
    ///     index they operate on and about which flags travel with the beat.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    public void InsertThenRemoveAtTheSameBeat_RestoresTheGridExactly(int beatIndex)
    {
        var h = new ChartPowerToolsTests.Harness();
        h.State.CurrentTimeMs = h.State.ActiveProject!.Beats[beatIndex].Time;
        var before = h.State.ActiveProject!.Beats
            .Select(b => (b.Time, b.Bpm, b.IsMeasure, b.IsAnchor, b.BeatsInMeasure, b.NoteDenominator))
            .ToList();

        h.Maintenance.InsertBeatAtPlayhead();
        h.State.CurrentTimeMs = h.State.ActiveProject!.Beats[beatIndex].Time;
        h.Maintenance.RemoveBeatAtPlayhead();

        var after = h.State.ActiveProject!.Beats
            .Select(b => (b.Time, b.Bpm, b.IsMeasure, b.IsAnchor, b.BeatsInMeasure, b.NoteDenominator))
            .ToList();
        Assert.Equal(before, after);
    }
}
