using DotCharter.Services.Editing;

namespace DotCharter.Tests.Services.Editing.Highway;

/// <summary>
///     Stage 1 of the editing rework: every per-string keyboard verb routes
///     through <c>ProjectState.StringEditMask</c>, so "edit one string of a
///     chord" is two keystrokes instead of a trip to the inspector's per-string
///     checkboxes (editing-rework audit 03 §5, §6.4).
///     <para>
///         The mask is empty by default, so all of this is dormant until the
///         user presses <c>Ctrl+1..9</c> — every other test in the suite pins
///         the unmasked behaviour.
///     </para>
/// </summary>
public sealed class StringMaskedEditingTests
{
    [Fact]
    public void AMaskedFretChangeTouchesOnlyTheMaskedStringOfAChord()
    {
        var h = new ChordKeyboardMutationTests.Harness();
        var chord = h.AddChord(2, templateId: -1, (0, 3), (1, 5), (2, 5));
        h.Selection.SelectedChords.Add(chord);
        h.State.StringEditMask = StringEditMask.FromStrings(1);

        h.Service.MoveSelectedFretUp();

        Assert.Equal(new[] { 3, 6, 5 }, chord.ChordNotes.Select(n => n.Fret));
    }

    [Fact]
    public void AMaskedFretChangeStillRepointsTheChordTemplate()
    {
        var h = new ChordKeyboardMutationTests.Harness();
        var shared = h.AddTemplate(0, (0, 3), (1, 5));
        var chord = h.AddChord(2, 0, (0, 3), (1, 5));
        h.Selection.SelectedChords.Add(chord);
        h.State.StringEditMask = StringEditMask.FromStrings(1);

        h.Service.SetSelectedFret(7);

        Assert.Equal(new[] { 3, 7 }, chord.ChordNotes.Select(n => n.Fret));
        var resolved = h.Arrangement.ChordTemplates.Single(t => t.Id == chord.TemplateId);
        Assert.Equal(ChordTemplateResolver.FretsOf(chord), resolved.Frets);
        Assert.Equal(5, shared.Frets[1]);
    }

    [Fact]
    public void AMaskedTechniqueToggleTouchesOnlyTheMaskedStrings()
    {
        var h = new ChordKeyboardMutationTests.Harness();
        var chord = h.AddChord(2, templateId: -1, (0, 3), (1, 5), (2, 5));
        h.Selection.SelectedChords.Add(chord);
        h.State.StringEditMask = StringEditMask.FromStrings(0, 2);

        h.Service.ToggleSelectedHarmonic();

        Assert.Equal(HarmonicType.Normal, chord.ChordNotes[0].Harmonic);
        Assert.Equal(HarmonicType.None, chord.ChordNotes[1].Harmonic);
        Assert.Equal(HarmonicType.Normal, chord.ChordNotes[2].Harmonic);
    }

    [Fact]
    public void TheMaskFiltersSTANDALONENotesByTheirOwnString()
    {
        // A note lives on exactly one string, so the mask decides whether it is
        // edited at all — EOF's per-lane bitmask semantics.
        var h = new ChordKeyboardMutationTests.Harness();
        var onMask = h.AddNote(1, 2, 5);
        var offMask = h.AddNote(1, 4, 5);
        h.Selection.SelectedNotes.Add(onMask);
        h.Selection.SelectedNotes.Add(offMask);
        h.State.StringEditMask = StringEditMask.FromStrings(2);

        h.Service.MoveSelectedFretUp();

        Assert.Equal(6, onMask.Fret);
        Assert.Equal(5, offMask.Fret);
    }

    [Fact]
    public void AChordPlayingNoMaskedStringIsNotAffectedAtAll()
    {
        var h = new ChordKeyboardMutationTests.Harness();
        var chord = h.AddChord(2, templateId: -1, (0, 3), (1, 5));
        h.Selection.SelectedChords.Add(chord);
        h.State.StringEditMask = StringEditMask.FromStrings(4);

        h.Service.ToggleSelectedHopo();

        Assert.All(chord.ChordNotes, n => Assert.Equal(HopoType.None, n.Hopo));
        Assert.False(h.History.CanUndo);
    }

    [Fact]
    public void AWholeChordVerbIsGatedByTheMaskButStillWritesTheChordLevelFlag()
    {
        var h = new ChordKeyboardMutationTests.Harness();
        var covered = h.AddChord(2, templateId: -1, (0, 3), (1, 5));
        var missed = h.AddChord(6, templateId: -1, (4, 3), (5, 5));
        h.Selection.SelectedChords.Add(covered);
        h.Selection.SelectedChords.Add(missed);
        h.State.StringEditMask = StringEditMask.FromStrings(1);

        h.Service.ToggleSelectedIgnore();

        Assert.True(covered.Ignore);
        Assert.False(missed.Ignore);
    }

    [Fact]
    public void AnEmptyMaskIsAStrictNoOp()
    {
        var h = new ChordKeyboardMutationTests.Harness();
        var chord = h.AddChord(2, templateId: -1, (0, 3), (1, 5), (2, 5));
        h.Selection.SelectedChords.Add(chord);
        Assert.True(h.State.StringEditMask.IsEmpty);

        h.Service.MoveSelectedFretUp();

        Assert.Equal(new[] { 4, 6, 6 }, chord.ChordNotes.Select(n => n.Fret));
    }

    [Fact]
    public void TogglingTheSameStringTwiceRestoresAllStrings()
    {
        var h = new ChordKeyboardMutationTests.Harness();
        h.State.ToggleStringEditMask(3);
        Assert.False(h.State.StringEditMask.IsEmpty);

        h.State.ToggleStringEditMask(3);

        Assert.True(h.State.StringEditMask.IsEmpty);
    }

    [Fact]
    public void ClearingTheMaskReturnsToAllStrings()
    {
        var h = new ChordKeyboardMutationTests.Harness();
        h.State.ToggleStringEditMask(1);
        h.State.ToggleStringEditMask(3);

        h.State.ClearStringEditMask();

        Assert.True(h.State.StringEditMask.IsEmpty);
    }
}
