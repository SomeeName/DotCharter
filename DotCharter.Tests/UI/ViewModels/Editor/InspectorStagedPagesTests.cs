using DotCharter.Tests.UI.ViewModels.Panels;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Editor.Inspector;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     #751 P4: the Note pages bound to the staged engine. A page control stages and writes nothing; the footer's
///     Apply writes at once for one sound and asks first (Confirm / Cancel) for more; leaving a page with a draft
///     asks Stay / Discard / Apply; a Selection Move over several sounds asks too.
/// </summary>
public sealed class InspectorStagedPagesTests
{
    /// <summary>The pages over the ladder fixture, on a chord's Technique section unless told otherwise; the Apply step
    /// on unless <paramref name="applyStep" /> says otherwise.</summary>
    internal static InspectorStagedPages Pages(ChordLadderFixture f, SoundSectionsViewModel? sections = null,
        bool applyStep = true)
    {
        sections ??= ChordTechniqueSections();
        var queue = new SoundQueueViewModel(() => f.Arrangement, () => f.Arrangement.Levels[0], () => [], () => [],
            f.Selection, _ => { }, FakeLocalization.English(), _ => (ChordNamingOptions.Default, DoubleStopNameOptions.Default));
        return new InspectorStagedPages(InspectorStagedEditsTests.Engine(f, applyStep: applyStep), f.Editor, f.History, f.State,
            FakeLocalization.English(), sections, queue, () => 4, f.Panel);
    }

    internal static SoundSectionsViewModel ChordTechniqueSections()
    {
        var sections = new SoundSectionsViewModel { Kind = SoundSelectionKind.Chord, HasChords = true };
        sections.ChooseCommand.Execute(SoundSection.Technique);
        return sections;
    }

    [Fact]
    public void ATechniqueToggle_WritesNothing_UntilApply_ThenOneUndoNode()
    {
        var f = new ChordLadderFixture();
        using var pages = Pages(f);
        f.Choose(ChordApplyRung.ThisChord);
        var fingerprints = f.Fingerprints();

        pages.ChordTechnique.SelectStringCommand.Execute(1);
        pages.ChordTechnique.Vibrato = true;

        Assert.Empty(f.ChangedSince(fingerprints));
        Assert.True(pages.ChordTechnique.Vibrato);
        Assert.Equal(1, pages.Footer.Count);

        pages.Footer.ApplyCommand.Execute(null);

        Assert.Equal(StagedAsk.None, pages.Footer.Asking);
        Assert.Single(f.ChangedSince(fingerprints));
        Assert.Single(f.History.GetUndoSteps());
        Assert.Equal(0, pages.Footer.Count);
    }

    [Fact]
    public void ApplyOverSeveralSounds_AsksFirst_CancelKeepsTheDraft_ConfirmWritesEveryTarget()
    {
        var f = new ChordLadderFixture();
        using var pages = Pages(f);
        f.Choose(ChordApplyRung.Selected);
        var fingerprints = f.Fingerprints();
        pages.ChordTechnique.SelectStringCommand.Execute(1);
        pages.ChordTechnique.Vibrato = true;

        pages.Footer.ApplyCommand.Execute(null);
        Assert.True(pages.Footer.IsReviewing);
        Assert.False(string.IsNullOrEmpty(pages.Footer.AskText));
        Assert.Empty(f.ChangedSince(fingerprints));

        pages.Footer.CancelCommand.Execute(null);
        Assert.True(pages.Footer.IsIdle);
        Assert.Equal(2, pages.Footer.Count);
        Assert.Empty(f.ChangedSince(fingerprints));

        pages.Footer.ApplyCommand.Execute(null);
        pages.Footer.ConfirmCommand.Execute(null);

        var changed = f.ChangedSince(fingerprints);
        Assert.Equal(2, changed.Count);
        Assert.All(changed, c => Assert.True(c.ChordNotes.Single(n => n.StringIndex == 1).Vibrato));
        Assert.Single(f.History.GetUndoSteps());
    }

    /// <summary>#751 P4 review: the chord "Apply to" rung moves what Apply reaches, so the footer's count, its button
    /// and an open review sentence follow a rung change made after the stage.</summary>
    [Fact]
    public void ARungChangeAfterTheStage_RereadsTheFootersPlan_AndAnOpenReview()
    {
        var f = new ChordLadderFixture();
        using var pages = Pages(f);
        f.Choose(ChordApplyRung.ThisChord);
        pages.ChordTechnique.SelectStringCommand.Execute(1);
        pages.ChordTechnique.Vibrato = true;
        Assert.Equal(1, pages.Footer.Count);

        f.Choose(ChordApplyRung.Selected);

        Assert.Equal(2, pages.Footer.Count);
        Assert.True(pages.Footer.CanApply);
        pages.Footer.ApplyCommand.Execute(null);
        Assert.True(pages.Footer.IsReviewing);
        var two = pages.Footer.AskText;

        f.Choose(ChordApplyRung.ThisChord);

        Assert.Equal(1, pages.Footer.Count);
        Assert.NotEqual(two, pages.Footer.AskText);
        Assert.Equal(pages.Engine.Review(StagedPage.Technique).Text, pages.Footer.AskText);
    }

    [Fact]
    public void LeavingAPageWithADraft_AsksStayDiscardOrApply()
    {
        var f = new ChordLadderFixture();
        var sections = ChordTechniqueSections();
        using var pages = Pages(f, sections);
        f.Choose(ChordApplyRung.ThisChord);
        var fingerprints = f.Fingerprints();
        pages.ChordTechnique.SelectStringCommand.Execute(1);
        pages.ChordTechnique.Vibrato = true;

        // Stay: the page and its draft stay.
        pages.Footer.ChooseSectionCommand.Execute(SoundSection.Shape);
        Assert.True(pages.Footer.IsLeaving);
        Assert.Equal(SoundSection.Technique, sections.Section);
        pages.Footer.CancelCommand.Execute(null);
        Assert.Equal(SoundSection.Technique, sections.Section);
        Assert.Equal(1, pages.Footer.Count);

        // Discard: leaves, writes nothing.
        pages.Footer.ChooseSectionCommand.Execute(SoundSection.Shape);
        pages.Footer.DiscardCommand.Execute(null);
        Assert.Equal(SoundSection.Shape, sections.Section);
        Assert.Empty(f.ChangedSince(fingerprints));
        Assert.True(pages.Engine.CanLeave(StagedPage.Technique));

        // Apply: writes, then leaves.
        sections.ChooseCommand.Execute(SoundSection.Technique);
        pages.ChordTechnique.Vibrato = true;
        pages.Footer.ChooseSectionCommand.Execute(SoundSection.Shape);
        pages.Footer.ApplyAndLeaveCommand.Execute(null);
        Assert.Equal(SoundSection.Shape, sections.Section);
        Assert.Single(f.ChangedSince(fingerprints));
    }

    [Fact]
    public void LeavingAPageWithoutADraft_GoesAtOnce()
    {
        var f = new ChordLadderFixture();
        var sections = ChordTechniqueSections();
        using var pages = Pages(f, sections);

        pages.Footer.ChooseSectionCommand.Execute(SoundSection.Shape);

        Assert.True(pages.Footer.IsIdle);
        Assert.Equal(SoundSection.Shape, sections.Section);
    }

    [Fact]
    public void AWholeChordToggle_StagesOnTheChord()
    {
        var f = new ChordLadderFixture();
        using var pages = Pages(f);
        f.Choose(ChordApplyRung.ThisChord);
        var chord = f.Selection.SelectedChord!;

        pages.ChordTechnique.Accent = true;
        Assert.False(chord.Accent);
        pages.Footer.ApplyCommand.Execute(null);

        Assert.True(chord.Accent);
    }

    /// <summary>#751 P4 review: the timeline takes clicks while the footer asks; a selection change withdraws the
    /// question and the action it held, so Confirm never writes onto a selection it was not asked about.</summary>
    [Fact]
    public void ASelectionChangeWhileTheFooterAsks_WithdrawsTheQuestion_AndItsAction()
    {
        var f = new ChordLadderFixture();
        var sections = ChordTechniqueSections();
        using var pages = Pages(f, sections);
        f.Choose(ChordApplyRung.Selected);
        var fingerprints = f.Fingerprints();
        pages.ChordTechnique.SelectStringCommand.Execute(1);
        pages.ChordTechnique.Vibrato = true;

        pages.Footer.ApplyCommand.Execute(null);
        Assert.True(pages.Footer.IsReviewing);
        f.Selection.SelectedChords.Remove(f.Second);
        Assert.True(pages.Footer.IsIdle);
        pages.Footer.ConfirmCommand.Execute(null);
        Assert.Empty(f.ChangedSince(fingerprints));

        // The leave guard's held action goes too: Discard after a timeline click leaves nothing to run.
        pages.Footer.ChooseSectionCommand.Execute(SoundSection.Shape);
        Assert.True(pages.Footer.IsLeaving);
        f.Selection.SelectedChords.Add(f.Second);
        Assert.True(pages.Footer.IsIdle);
        pages.Footer.DiscardCommand.Execute(null);
        Assert.Equal(SoundSection.Technique, sections.Section);
    }

    /// <summary>A history change (a keyboard edit, an undo) withdraws a Move question the same way.</summary>
    [Fact]
    public void AHistoryChangeWhileTheFooterAsks_WithdrawsTheMove()
    {
        var f = new ChordLadderFixture();
        var sections = new SoundSectionsViewModel { Kind = SoundSelectionKind.MultiSame, HasChords = true };
        using var pages = Pages(f, sections);

        pages.Selection.MoveFretCommand.Execute("1");
        Assert.True(pages.Footer.IsReviewing);
        var clone = f.First.Clone();
        clone.Accent = true;
        f.History.Do(new MutateChordsAction(f.Arrangement.Levels[0].Chords, [f.First], [f.First.Clone()], [clone]));
        var fingerprints = f.Fingerprints();

        Assert.True(pages.Footer.IsIdle);
        pages.Footer.ConfirmCommand.Execute(null);
        Assert.Empty(f.ChangedSince(fingerprints));
    }

    /// <summary>#751 P4 review: a timeline click to another sound keeps the Technique draft on the first one and
    /// says so, with a jump back; the draft is still there to Apply.</summary>
    [Fact]
    public void ATimelineClickToAnotherSound_KeepsTheDraft_NamesIt_AndJumpsBack()
    {
        var f = new ChordLadderFixture();
        using var pages = Pages(f);
        Select(f, f.First);
        f.Choose(ChordApplyRung.ThisChord);
        pages.ChordTechnique.Accent = true;

        Select(f, f.Second);
        Assert.Equal(1, pages.Footer.ElsewhereCount);
        Assert.Equal("1 draft on another sound", pages.Footer.ElsewhereText);
        Assert.False(f.First.Accent);

        pages.Footer.JumpToDraftCommand.Execute(null);
        Assert.Same(f.First, f.Selection.SelectedChord);
        Assert.Equal(0, pages.Footer.ElsewhereCount);
        Assert.Equal(1, pages.Footer.Count);
        pages.Footer.ApplyCommand.Execute(null);
        Assert.True(f.First.Accent);
    }

    /// <summary>#751 P4 review: a level switch keeps the draft (it no longer drops every draft silently); the
    /// jump goes back to its level and sound.</summary>
    [Fact]
    public void ALevelSwitch_KeepsTheDraft_AndTheJumpGoesBackToItsLevel()
    {
        var f = new ChordLadderFixture();
        using var pages = Pages(f);
        Select(f, f.First);
        f.Choose(ChordApplyRung.ThisChord);
        pages.ChordTechnique.Accent = true;

        f.State.ActiveLevel = f.Arrangement.Levels[1];
        f.Selection.ClearSelections();
        Assert.True(f.Editor.HasPendingDrafts);
        Assert.Equal(1, pages.Footer.ElsewhereCount);

        pages.Footer.JumpToDraftCommand.Execute(null);
        Assert.Same(f.Arrangement.Levels[0], f.State.ActiveLevel);
        Assert.Same(f.First, f.Selection.SelectedChord);
        Assert.True(pages.ChordTechnique.Accent);
        Assert.Equal(1, pages.Footer.Count);
    }

    /// <summary>#751 P4 review: the Placement sustain spinner stops at the end of the song's audio again.</summary>
    [Fact]
    public void ThePlacementSustainSpinner_StopsAtTheEndOfTheAudio()
    {
        var f = new ChordLadderFixture();
        f.Selection.ClearSelections();
        var note = InspectorStagedEditsTests.AddNote(f, 20, 5);
        // 16 s of audio is beat 32: twelve beats from beat 20, less one grid step (4 a beat).
        note.Sustain = new FractionalPosition(new Fraction(47, 4));
        f.Selection.SelectedNotes.Add(note);
        f.Selection.SelectedNote = note;
        var sections = new SoundSectionsViewModel { Kind = SoundSelectionKind.Note };
        sections.ChooseCommand.Execute(SoundSection.Placement);
        using var pages = Pages(f, sections);

        pages.Placement.StepSustain(1);
        Assert.Equal(new FractionalPosition(12), pages.Engine.Placement!.Sustain);
        pages.Placement.StepSustain(1);
        Assert.Equal(new FractionalPosition(12), pages.Engine.Placement!.Sustain);
        pages.Placement.StepSustain(-1);
        Assert.Null(pages.Engine.Placement);
    }

    /// <summary>#751 P4 review: a stage re-reads the page on screen and the footer, never the hidden pages.</summary>
    [Fact]
    public void AStage_RereadsThePageOnScreen_NotTheHiddenOnes()
    {
        var f = new ChordLadderFixture();
        using var pages = Pages(f);
        f.Choose(ChordApplyRung.ThisChord);
        var hidden = 0;
        pages.Placement.PropertyChanged += (_, _) => hidden++;
        pages.NoteTechnique.PropertyChanged += (_, _) => hidden++;
        pages.Selection.PropertyChanged += (_, _) => hidden++;
        var shown = 0;
        pages.ChordTechnique.PropertyChanged += (_, _) => shown++;

        pages.ChordTechnique.Accent = true;

        Assert.Equal(0, hidden);
        Assert.True(shown > 0);
    }

    private static void Select(ChordLadderFixture f, Chord chord)
    {
        f.Selection.ClearSelections();
        f.Selection.SelectedChords.Add(chord);
        f.Selection.SelectedChord = chord;
    }

    [Fact]
    public void ASelectionMoveOverSeveralSounds_AsksFirst()
    {
        var f = new ChordLadderFixture();
        var sections = new SoundSectionsViewModel { Kind = SoundSelectionKind.MultiSame, HasChords = true };
        using var pages = Pages(f, sections);
        var fingerprints = f.Fingerprints();

        pages.Selection.MoveFretCommand.Execute("1");

        Assert.True(pages.Footer.IsReviewing);
        Assert.Empty(f.ChangedSince(fingerprints));
        pages.Footer.ConfirmCommand.Execute(null);
        Assert.Equal(2, f.ChangedSince(fingerprints).Count);
        Assert.Single(f.History.GetUndoSteps());
    }
}
