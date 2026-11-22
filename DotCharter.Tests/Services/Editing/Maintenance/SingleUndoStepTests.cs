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
    public void AFailedMutation_RestoresSelectionAndFocusBeforeItRan()
    {
        var h = new ChartPowerToolsTests.Harness();
        var project = h.State.ActiveProject!;
        var arrangement = h.State.ActiveArrangement!;
        var level = h.State.ActiveLevel!;
        var note = level.Notes[0];
        var other = new Arrangement();
        other.Levels.Add(new ChartLevel());
        project.Arrangements.Add(other);
        h.Selection.SelectedNotes.Add(note);
        h.Selection.HoveredNote = note;
        h.Selection.PreviewNotes.Add(note);
        h.Selection.PreviewArrangement = arrangement;

        Assert.Throws<InvalidOperationException>(() => h.Maintenance.RunAsSingleUndoStep(() =>
        {
            h.State.ActiveArrangement = other;
            h.State.ActiveLevel = other.Levels[0];
            h.Selection.ClearAll();
            level.Notes.Clear();
            throw new InvalidOperationException("partial import");
        }));

        Assert.Same(arrangement, h.State.ActiveArrangement);
        Assert.Same(level, h.State.ActiveLevel);
        Assert.Same(note, Assert.Single(h.Selection.SelectedNotes));
        Assert.Same(note, h.Selection.SelectedNote);
        Assert.Same(note, h.Selection.HoveredNote);
        Assert.Same(note, Assert.Single(h.Selection.PreviewNotes));
        Assert.Same(arrangement, h.Selection.PreviewArrangement);
    }

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
    public void RedoingAGrowingSnapshot_ReattachesTheNoteCapturedByALaterEdit()
    {
        var h = new ChartPowerToolsTests.Harness();
        var level = h.State.ActiveLevel!;
        var added = new GuitarNote { Position = new FractionalPosition(20), StringIndex = 0, Fret = 7 };
        h.Maintenance.RunAsSingleUndoStep(() => level.Notes.Add(added));
        h.History.Do(new MutateNotesAction(level.Notes, [added], n => n.Fret = 19));

        h.History.Undo();
        h.History.Undo();
        Assert.DoesNotContain(added, level.Notes);
        h.History.Redo();
        h.History.Redo();

        Assert.Same(added, level.Notes[^1]);
        Assert.Equal(19, level.Notes[^1].Fret);
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

    [Fact]
    public void AMutationThatChangesTheChartThenThrows_RestoresTheChartAndExistingHistory()
    {
        var h = new ChartPowerToolsTests.Harness();
        var project = h.State.ActiveProject!;
        var arrangement = project.Arrangements[0];
        var level = arrangement.Levels[0];
        var note = level.Notes[0];
        var originalFret = note.Fret;
        var originalBeatCount = project.Beats.Count;
        h.History.Do(new MutateNotesAction(level.Notes, [note], n => n.Fret = 19));
        var node = h.History.CurrentNodeId;
        var failure = new InvalidOperationException("import failed after mutation");

        var thrown = Assert.Throws<InvalidOperationException>(() => h.Maintenance.RunAsSingleUndoStep(() =>
        {
            project.Beats.Clear();
            project.Arrangements.Clear();
            project.IsBeatMapLocked = true;
            project.AudioFileName = "partial-import.wav";
            throw failure;
        }));

        Assert.Same(failure, thrown);
        Assert.Same(project, h.State.ActiveProject);
        Assert.Same(arrangement, Assert.Single(project.Arrangements));
        Assert.Same(level, arrangement.Levels[0]);
        Assert.Same(note, level.Notes[0]);
        Assert.Equal(19, note.Fret);
        Assert.Equal(originalBeatCount, project.Beats.Count);
        Assert.False(project.IsBeatMapLocked);
        Assert.NotEqual("partial-import.wav", project.AudioFileName);
        Assert.Equal(node, h.History.CurrentNodeId);
        Assert.False(h.History.CanRedo);

        h.History.Undo();
        Assert.Equal(originalFret, note.Fret);
    }

    [Fact]
    public void SnapshotUndoAndRedo_PreserveALaterLockAndAudioAttachment()
    {
        var h = new ChartPowerToolsTests.Harness();
        var project = h.State.ActiveProject!;
        h.Maintenance.RunAsSingleUndoStep(() => project.Arrangements[0].Name = "Edited");

        h.Maintenance.ToggleBeatMapLock();
        project.AudioFileName = "attached-later.wav";
        h.History.Undo();

        Assert.True(project.IsBeatMapLocked);
        Assert.Equal("attached-later.wav", project.AudioFileName);
        h.History.Redo();
        Assert.True(project.IsBeatMapLocked);
        Assert.Equal("attached-later.wav", project.AudioFileName);

        // A step which changes these fields itself still owns their before/after values.
        h.Maintenance.RunAsSingleUndoStep(() =>
        {
            project.IsBeatMapLocked = false;
            project.AudioFileName = "imported.wav";
        });
        h.History.Undo();

        Assert.True(project.IsBeatMapLocked);
        Assert.Equal("attached-later.wav", project.AudioFileName);
        h.History.Redo();
        Assert.False(project.IsBeatMapLocked);
        Assert.Equal("imported.wav", project.AudioFileName);
    }
}
