using DotCharter.Core.Domain.Vocals;

namespace DotCharter.Tests.Services.Editing.Maintenance;

/// <summary>
///     Deleting an arrangement or a vocal track is the most destructive thing the
///     app can do from a single menu click, and it was NOT UNDOABLE: both commands
///     removed the item straight out of the collection with no
///     <c>IHistoryService</c> involvement, so Ctrl+Z could not bring it back and
///     the work was gone unless the user closed the project without saving.
///     Reported by the owner after losing a vocal track.
///     <para>
///         These assert the whole claim — the item goes, undo brings it back WITH
///         ITS CONTENT, and redo removes it again. "Undo restores an arrangement
///         of the right name" would satisfy a weaker assertion while the charting
///         inside it was still lost, which is the failure that actually matters.
///     </para>
/// </summary>
public sealed class ArrangementDeletionUndoTests
{
    /// <summary>The shared harness plus a second arrangement and a vocal track to delete.</summary>
    private static ChartPowerToolsTests.Harness Populated()
    {
        var h = new ChartPowerToolsTests.Harness();
        var project = h.State.ActiveProject!;

        var rhythm = new Arrangement { Name = "Rhythm", ArrangementType = ArrangementType.Rhythm };
        var level = new ChartLevel();
        for (var i = 0; i < 3; i++)
            level.Notes.Add(new GuitarNote { Position = new FractionalPosition(i), StringIndex = i, Fret = 5 });
        rhythm.Levels.Add(level);
        project.Arrangements.Add(rhythm);

        var vocals = new VocalTrack { Name = "Vocals" };
        var phrase = new LyricPhrase();
        phrase.Lyrics.Add(new LyricEvent { Text = "hel", Position = new FractionalPosition(0) });
        vocals.Phrases.Add(phrase);
        project.VocalTracks.Add(vocals);
        h.Editor.ActiveVocalTrack = vocals;

        return h;
    }

    [Fact]
    public void DeletingAnArrangementIsUndoableAndRestoresItsNotes()
    {
        var h = Populated();
        h.State.ActiveArrangement = h.State.ActiveProject!.Arrangements[1];

        h.Maintenance.DeleteActiveArrangement();

        Assert.Single(h.State.ActiveProject!.Arrangements);
        Assert.True(h.History.CanUndo, "deleting an arrangement must create an undo node");

        h.History.Undo();

        Assert.Equal(2, h.State.ActiveProject!.Arrangements.Count);
        var restored = h.State.ActiveProject.Arrangements.Single(a => a.Name == "Rhythm");
        // The CONTENT has to come back, not just a row with the right name.
        Assert.Equal(3, restored.Levels[0].Notes.Count);
    }

    [Fact]
    public void DeletingAVocalTrackIsUndoableAndRestoresItsLyrics()
    {
        var h = Populated();

        h.Maintenance.DeleteActiveVocalTrack();

        Assert.Empty(h.State.ActiveProject!.VocalTracks);
        Assert.True(h.History.CanUndo, "deleting a vocal track must create an undo node");

        h.History.Undo();

        var restored = Assert.Single(h.State.ActiveProject!.VocalTracks);
        Assert.Equal("Vocals", restored.Name);
        Assert.Equal("hel", Assert.Single(Assert.Single(restored.Phrases).Lyrics).Text);
    }

    [Fact]
    public void RedoDeletesItAgain()
    {
        var h = Populated();
        h.State.ActiveArrangement = h.State.ActiveProject!.Arrangements[1];

        h.Maintenance.DeleteActiveArrangement();
        h.History.Undo();
        Assert.Equal(2, h.State.ActiveProject!.Arrangements.Count);

        h.History.Redo();

        Assert.Single(h.State.ActiveProject!.Arrangements);
    }

    /// <summary>
    ///     A chart with no arrangement is not a state the editor can represent, so
    ///     the last one is refused — and refusing must not push an empty node onto
    ///     the history, or the next Ctrl+Z would appear to do nothing.
    /// </summary>
    [Fact]
    public void TheLastArrangementIsRefusedWithoutTouchingHistory()
    {
        var h = new ChartPowerToolsTests.Harness();

        h.Maintenance.DeleteActiveArrangement();

        Assert.Single(h.State.ActiveProject!.Arrangements);
        Assert.False(h.History.CanUndo);
    }

    /// <summary>
    ///     The active arrangement must land on a SURVIVOR. Leaving it pointing at
    ///     the deleted instance would let every later edit write into an object
    ///     that is no longer in the project — silently, and unsaved.
    /// </summary>
    [Fact]
    public void TheActiveArrangementFollowsTheDeletion()
    {
        var h = Populated();
        h.State.ActiveArrangement = h.State.ActiveProject!.Arrangements[1];

        h.Maintenance.DeleteActiveArrangement();

        Assert.NotNull(h.State.ActiveArrangement);
        Assert.Contains(h.State.ActiveArrangement!, h.State.ActiveProject!.Arrangements);
    }
}
