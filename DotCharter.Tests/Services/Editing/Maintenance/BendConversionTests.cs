using DotCharter.Services.Editing.Collisions;

namespace DotCharter.Tests.Services.Editing.Maintenance;

public sealed class BendConversionTests
{
    [Theory]
    [InlineData("merge-undo")]
    [InlineData("power-undo")]
    [InlineData("split")]
    public void ADetachedConversionSelectionCannotAddDuplicateNotes(string previous)
    {
        var h = Setup();
        var level = h.State.ActiveLevel!;
        if (previous == "power-undo") h.Selection.SelectedNotes.RemoveAt(1);
        if (previous == "power-undo") h.Maintenance.ConvertSelectedNotesToPowerChord(false);
        else h.Maintenance.MergeSelectedNotesIntoChords();
        if (previous == "split") h.Maintenance.SplitSelectedChordsIntoNotes();
        else h.History.Undo();
        Assert.Equal(2, level.Notes.Count);
        var historyNode = h.History.CurrentNodeId;

        h.Maintenance.SplitSelectedChordsIntoNotes();

        Assert.Equal(2, level.Notes.Count);
        Assert.Empty(level.Chords);
        Assert.Equal(historyNode, h.History.CurrentNodeId);
    }

    private static ChartPowerToolsTests.Harness Setup()
    {
        var h = new ChartPowerToolsTests.Harness();
        var level = h.State.ActiveLevel!;
        level.Notes.Clear();
        level.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(2), StringIndex = 0, Fret = 5,
            Sustain = new FractionalPosition(2), SlideTo = 7, UnpitchedSlide = true,
            Harmonic = HarmonicType.Pinch,
            BendValues = { new BendValue { Position = new FractionalPosition(3), Step = 2 } }
        });
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(2), StringIndex = 1, Fret = 7 });
        foreach (var note in level.Notes) h.Selection.SelectedNotes.Add(note);
        return h;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConversionOwnsItsBends_AndUndoRestoresTheUntouchedSource(bool split)
    {
        var h = Setup();
        var level = h.State.ActiveLevel!;
        h.Maintenance.MergeSelectedNotesIntoChords();
        var chord = Assert.Single(level.Chords);
        if (split)
        {
            h.Selection.ClearAll();
            h.Selection.SelectedChords.Add(chord);
            h.Maintenance.SplitSelectedChordsIntoNotes();
        }

        var config = new AppConfig { MinNoteDistanceMode = MinNoteDistanceMode.Milliseconds, MinNoteDistanceMs = 0 };
        h.History.Do(new AddNotesAction(level.Notes,
            [new GuitarNote { Position = new FractionalPosition(2, new Fraction(1, 2)), StringIndex = 0, Fret = 9 }],
            () => ChartSpacingEnforcer.EnforceLevel(level, h.State.Beats, config)));
        var sound = split ? (IGuitarSound)level.Notes.Single(n => n.Fret == 5) : chord.ChordNotes[0];
        Assert.Equal(new FractionalPosition(2, new Fraction(1, 2)), Assert.Single(sound.BendValues).Position);
        h.History.Undo();
        h.History.Undo();
        var restored = split ? (IGuitarSound)chord.ChordNotes[0] : level.Notes.Single(n => n.StringIndex == 0);
        Assert.Equal(new FractionalPosition(3), Assert.Single(restored.BendValues).Position);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PowerChordKeepsTheRootsTechniques(bool big)
    {
        var h = Setup();
        h.Selection.SelectedNotes.RemoveAt(1);
        h.Maintenance.ConvertSelectedNotesToPowerChord(big);
        var chord = Assert.Single(h.State.ActiveLevel!.Chords);
        var root = chord.ChordNotes.Single(n => n.StringIndex == 0);
        Assert.Equal(2, Assert.Single(root.BendValues).Step);
        Assert.Equal(7, root.SlideTo);
        Assert.True(root.UnpitchedSlide);
        Assert.Equal(HarmonicType.Pinch, root.Harmonic);
        Assert.All(chord.ChordNotes.Where(n => n.StringIndex != 0), n => Assert.Empty(n.BendValues));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConversionSelectsLiveSounds_AndRepeatingItAddsNoDuplicate(bool power)
    {
        var h = Setup();
        if (power) h.Selection.SelectedNotes.RemoveAt(1);
        Convert();
        var chord = Assert.Single(h.State.ActiveLevel!.Chords);
        Assert.DoesNotContain(h.Selection.SelectedNotes, n => !h.State.ActiveLevel.Notes.Contains(n));
        Assert.Contains(chord, h.Selection.SelectedChords);
        Convert();
        Assert.Single(h.State.ActiveLevel.Chords);

        void Convert()
        {
            if (power) h.Maintenance.ConvertSelectedNotesToPowerChord(false);
            else h.Maintenance.MergeSelectedNotesIntoChords();
        }
    }
}
