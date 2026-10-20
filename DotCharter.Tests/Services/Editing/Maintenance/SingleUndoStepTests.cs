namespace DotCharter.Tests.Services.Editing.Maintenance;

/// <summary>
///     <c>RunAsSingleUndoStep</c> is the seam the bulk importers commit through.
///     <para>
///         It exists because Guitar Pro / Songsterr "import into the current
///         project" REPLACE the beat map — and since every chart position is a
///         <c>FractionalPosition</c> resolved THROUGH that map, replacing it moves
///         every already-charted note, chord, anchor, hand shape, section and
///         lyric in wall-clock time. None of those dialogs held an
///         <c>IHistoryService</c>, so the damage was unrecoverable: no Ctrl+Z,
///         and — because the dirty flag is derived from the undo history — not
///         even a title-bar <c>*</c> or a close prompt to warn the user.
///     </para>
///     <para>
///         Snapshotting AROUND the mutation rather than restructuring the
///         importers to write into a clone is deliberate: the before/after pair
///         captures whatever they touched, whichever services they went through.
///     </para>
/// </summary>
public sealed class SingleUndoStepTests
{
    [Fact]
    public void AReplacedBeatMapIsRestoredByUndo()
    {
        var h = new ChartPowerToolsTests.Harness();
        var project = h.State.ActiveProject!;
        var originalBeats = project.Beats.Count;
        Assert.True(originalBeats > 0, "the fixture must start with a beat map to replace");

        var ok = h.Maintenance.RunAsSingleUndoStep(() =>
        {
            // Exactly what GpImportApplier.ApplyTempoAndKey does.
            project.Beats.Clear();
            for (var i = 0; i < 4; i++)
                project.Beats.Add(new BeatMarker { Time = i * 1000, Bpm = 60, BeatsInMeasure = 3 });
        });

        Assert.True(ok);
        Assert.Equal(4, h.State.ActiveProject!.Beats.Count);
        Assert.True(h.History.CanUndo, "a bulk import must leave exactly one undo node");

        h.History.Undo();

        Assert.Equal(originalBeats, h.State.ActiveProject!.Beats.Count);
        Assert.Equal(120, h.State.ActiveProject!.Beats[0].Bpm);
    }

    [Fact]
    public void TheWholeMutationIsOneNodeHoweverManyThingsItTouched()
    {
        var h = new ChartPowerToolsTests.Harness();
        var project = h.State.ActiveProject!;

        h.Maintenance.RunAsSingleUndoStep(() =>
        {
            project.Beats.Clear();
            project.Beats.Add(new BeatMarker { Time = 0, Bpm = 90, BeatsInMeasure = 4 });
            project.Arrangements.Add(new Arrangement { Name = "Imported" });
            project.Arrangements[0].Levels[0].Notes.Clear();
        });

        // ONE undo takes all three back, not three.
        h.History.Undo();

        Assert.Single(h.State.ActiveProject!.Arrangements);
        Assert.NotEmpty(h.State.ActiveProject!.Arrangements[0].Levels[0].Notes);
        Assert.False(h.History.CanUndo);
    }

    /// <summary>
    ///     A half-done import is not a state anyone should be able to redo onto,
    ///     so a throwing mutation leaves no node — and the exception reaches the
    ///     caller, which owns its own failure message.
    /// </summary>
    [Fact]
    public void AThrowingMutationLeavesNoUndoNode()
    {
        var h = new ChartPowerToolsTests.Harness();

        Assert.Throws<InvalidOperationException>(() =>
            h.Maintenance.RunAsSingleUndoStep(() => throw new InvalidOperationException("import blew up")));

        Assert.False(h.History.CanUndo);
    }

    [Fact]
    public void WithNoProjectItRefusesRatherThanThrowing()
    {
        var h = new ChartPowerToolsTests.Harness();
        h.State.ActiveProject = null;
        var ran = false;

        Assert.False(h.Maintenance.RunAsSingleUndoStep(() => ran = true));
        Assert.False(ran);
    }
}
