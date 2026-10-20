namespace DotCharter.Tests.Services.Editing.Tempo;

/// <summary>
///     Coverage for re-timing a chart's beat grid onto external sync anchors.
///     The key guarantee: notes are grid-relative, so re-timing the beats moves
///     every note automatically — and the action undoes cleanly.
/// </summary>
public sealed class BeatResyncServiceTests
{
    private static ChartProject ProjectWithBeats(int count, double msPerBeat)
    {
        var project = new ChartProject();
        for (var i = 0; i < count; i++)
        {
            project.Beats.Add(new BeatMarker
            {
                Time = i * msPerBeat, Bpm = 120, BeatsInMeasure = 4, NoteDenominator = 4, IsMeasure = i % 4 == 0
            });
        }

        return project;
    }

    private static List<BeatMarker> Beats(int count, double msPerBeat)
        => Enumerable.Range(0, count)
            .Select(i => new BeatMarker
            {
                Time = i * msPerBeat, Bpm = 240, BeatsInMeasure = 4, NoteDenominator = 4, IsMeasure = i % 4 == 0
            })
            .ToList();

    [Fact]
    public void Resync_RetimesBeats_AndNotesFollow_AndUndoRestores()
    {
        var project = ProjectWithBeats(8, 500); // beat 4 at 2000 ms
        var note = new GuitarNote { Position = new FractionalPosition(4, new Fraction(0, 1)) };
        Assert.Equal(2000.0, note.GetTimeMs(project.Beats), 1);

        var synced = Beats(8, 250); // beat 4 at 1000 ms
        var result = new BeatResyncService().BuildResyncAction(project, synced);

        Assert.Equal(BeatResyncStatus.Ok, result.Status);
        Assert.NotNull(result.Action);

        result.Action!.Execute();
        Assert.Equal(1000.0, project.Beats[4].Time, 1);
        Assert.Equal(1000.0, note.GetTimeMs(project.Beats), 1); // note followed the beat

        result.Action.Undo();
        Assert.Equal(2000.0, project.Beats[4].Time, 1);
        Assert.Equal(2000.0, note.GetTimeMs(project.Beats), 1); // fully restored
    }

    [Fact]
    public void Resync_LongerSyncThanChart_RetimesTheChartsBeats()
    {
        var project = ProjectWithBeats(8, 500);
        var synced = Beats(12, 250); // sync is longer than the chart — allowed
        var result = new BeatResyncService().BuildResyncAction(project, synced);

        Assert.Equal(BeatResyncStatus.Ok, result.Status);
        result.Action!.Execute();
        Assert.Equal(250.0, project.Beats[1].Time, 1);
        Assert.Equal(8, project.Beats.Count); // count unchanged — only re-timed
    }

    [Fact]
    public void Resync_ShorterSyncThanChart_RefusesToAvoidBrokenGrid()
    {
        var project = ProjectWithBeats(8, 500);
        var result = new BeatResyncService().BuildResyncAction(project, Beats(4, 250));

        Assert.Equal(BeatResyncStatus.CountMismatch, result.Status);
        Assert.Null(result.Action);
        Assert.Equal(8, result.ProjectBeatCount);
        Assert.Equal(4, result.SyncedBeatCount);
    }

    [Fact]
    public void Resync_LockedBeatMap_Refuses()
    {
        var project = ProjectWithBeats(8, 500);
        project.IsBeatMapLocked = true;
        var result = new BeatResyncService().BuildResyncAction(project, Beats(8, 250));

        Assert.Equal(BeatResyncStatus.Locked, result.Status);
        Assert.Null(result.Action);
    }

    [Fact]
    public void Resync_TooFewSyncPoints_ReturnsNoSyncData()
    {
        var project = ProjectWithBeats(8, 500);
        var result = new BeatResyncService().BuildResyncAction(project, Beats(1, 250));
        Assert.Equal(BeatResyncStatus.NoSyncData, result.Status);
    }

    [Fact]
    public void Resync_EmptyChart_ReturnsNoExistingBeats()
    {
        var result = new BeatResyncService().BuildResyncAction(new ChartProject(), Beats(8, 250));
        Assert.Equal(BeatResyncStatus.NoExistingBeats, result.Status);
    }
}
