namespace DotCharter.Tests.Services.Editing.Maintenance;

/// <summary>
///     "Muted strums → muted chord" (#566) through <c>ChartMaintenanceService</c>: the scope (the
///     selection, else the phrase under the playhead), the status line and the one undo node. The
///     conversion rules themselves are pinned by <c>MutedStrumChordsTests</c>.
/// </summary>
public sealed class MutedStrumsToChordCommandTests
{
    private readonly ChartPowerToolsTests.Harness _h = new();
    private readonly ChordTemplate _six = new() { Id = 0, Name = "D5", Frets = new() { [1] = 6, [2] = 6 } };

    public MutedStrumsToChordCommandTests()
    {
        Arrangement.ChordTemplates.Add(_six);
        Level.Notes.Clear();
        // Two phrases (beats 0-7 and 8-), each a chord with a one-chord bar and two muted strums.
        Arrangement.PhraseIterations.Add(new PhraseIteration { Position = new FractionalPosition(0) });
        Arrangement.PhraseIterations.Add(new PhraseIteration { Position = new FractionalPosition(8) });
        foreach (var start in new[] { 0, 8 })
        {
            var chord = new Chord { Position = new FractionalPosition(start), TemplateId = _six.Id };
            chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 6 });
            chord.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = 6 });
            Level.Chords.Add(chord);
            Level.HandShapes.Add(new HandShape
            {
                Position = new FractionalPosition(start), EndPosition = new FractionalPosition(start, new Fraction(1, 2)), TemplateId = _six.Id
            });
            for (var beat = start + 1; beat <= start + 2; beat++)
                Level.Notes.Add(new GuitarNote { Position = new FractionalPosition(beat), StringIndex = 2, Fret = 6, Mute = MuteType.Full });
        }
    }

    private Arrangement Arrangement => _h.State.ActiveArrangement!;
    private ChartLevel Level => _h.State.ActiveLevel!;

    private bool IsChordAt(int beat) => Level.Chords.Any(c => c.Position == new FractionalPosition(beat));

    [Fact]
    public void NothingSelected_ConvertsThePhraseUnderThePlayheadOnly_InOneUndoNode()
    {
        _h.State.CurrentTimeMs = 500; // beat 1, inside the first phrase

        _h.Maintenance.ConvertMutedStrumsToChords();

        Assert.True(IsChordAt(1));
        Assert.True(IsChordAt(2));
        Assert.False(IsChordAt(9));
        Assert.False(IsChordAt(10));
        Assert.Equal(2, Level.Notes.Count);
        Assert.Equal("2 muted strums are now muted copies of their chord.", _h.State.MainDisplayMessage);
        Assert.Single(_h.History.GetUndoSteps());
    }

    [Fact]
    public void TheSelectionWins_AndItsStrumsTakeTheChordBeforeThem_EvenOutsideIt()
    {
        _h.State.CurrentTimeMs = 500;
        var strum = Level.Notes.Single(n => n.Position == new FractionalPosition(9));
        _h.Selection.SelectedNotes.Add(strum);

        _h.Maintenance.ConvertMutedStrumsToChords();

        Assert.True(IsChordAt(9));
        Assert.False(IsChordAt(1));
        Assert.Equal("1 muted strum is now a muted copy of its chord.", _h.State.MainDisplayMessage);
        Assert.Empty(_h.Selection.SelectedNotes);
    }

    [Fact]
    public void NothingToDo_SaysSo_AndCreatesNoUndoNode()
    {
        foreach (var note in Level.Notes) note.Mute = MuteType.None;
        _h.State.CurrentTimeMs = 500;

        _h.Maintenance.ConvertMutedStrumsToChords();

        Assert.False(_h.History.CanUndo);
        Assert.Equal(4, Level.Notes.Count);
        Assert.StartsWith("No muted strum after a chord", _h.State.MainDisplayMessage);
    }
}
