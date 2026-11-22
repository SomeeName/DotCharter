using DotCharter.Tests.UI.ViewModels.Panels;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Editor.Inspector;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     #805: Settings ▸ Editor's "Inspector edits wait for Apply", OFF by default. Off, each page edit is written at
///     once through the engine's own Apply (same targets, one undo step per edit) and no draft, Apply button or
///     "draft on another sound" chip is left; on, the edit stays a draft as before (#751). Switching it off with a
///     draft waiting writes the draft instead of dropping it.
/// </summary>
public sealed class InspectorImmediateApplyTests
{
    [Fact]
    public void TheApplyStep_IsOffByDefault() => Assert.False(new ProjectState().InspectorApplyStep);

    [Fact]
    public void Off_LinkNext_IsInTheChartAtOnce_AndUndoesInOneStep()
    {
        var f = new ChordLadderFixture();
        var note = NoteOnTechnique(f);
        using var pages = InspectorStagedPagesTests.Pages(f, NoteTechniqueSections(), applyStep: false);

        pages.NoteTechnique.LinkNext = true;

        Assert.True(note.LinkNext);
        Assert.Single(f.History.GetUndoSteps());
        Assert.False(pages.Engine.HasAnyDraft);
        Assert.True(pages.NoteTechnique.LinkNext);
        Assert.False(pages.Footer.ShowsApplyButton);
        Assert.True(pages.Footer.ShowsApply);
        Assert.Equal(pages.Localization["Inspector_Staged_ScopeNote"] + " · 1", pages.Footer.ScopeText);

        f.History.Undo();

        Assert.False(note.LinkNext);
        Assert.False(pages.NoteTechnique.LinkNext);
    }

    [Fact]
    public void On_LinkNext_StaysADraft_UntilApply()
    {
        var f = new ChordLadderFixture();
        var note = NoteOnTechnique(f);
        using var pages = InspectorStagedPagesTests.Pages(f, NoteTechniqueSections(), applyStep: true);

        pages.NoteTechnique.LinkNext = true;

        Assert.False(note.LinkNext);
        Assert.Empty(f.History.GetUndoSteps());
        Assert.True(pages.Engine.HasAnyDraft);
        Assert.True(pages.Footer.ShowsApplyButton);

        pages.Footer.ApplyCommand.Execute(null);

        Assert.True(note.LinkNext);
        Assert.Single(f.History.GetUndoSteps());
    }

    [Fact]
    public void Off_AChordTechnique_WritesEveryTargetOfTheRung_AtOnce_WithoutAsking_OneUndoStepPerEdit()
    {
        var f = new ChordLadderFixture();
        using var pages = InspectorStagedPagesTests.Pages(f, InspectorStagedPagesTests.ChordTechniqueSections(), applyStep: false);
        f.Choose(ChordApplyRung.Selected);
        var fingerprints = f.Fingerprints();
        pages.ChordTechnique.SelectStringCommand.Execute(1);

        pages.ChordTechnique.Vibrato = true;

        var changed = f.ChangedSince(fingerprints);
        Assert.Equal(2, changed.Count);
        Assert.All(changed, c => Assert.True(c.ChordNotes.Single(n => n.StringIndex == 1).Vibrato));
        Assert.Equal(StagedAsk.None, pages.Footer.Asking);
        Assert.Single(f.History.GetUndoSteps());

        pages.ChordTechnique.Accent = true;

        Assert.Equal(2, f.History.GetUndoSteps().Count);
        Assert.False(pages.Engine.HasAnyDraft);
        Assert.False(pages.Footer.ShowsElsewhere);
    }

    [Fact]
    public void Off_AnEditThatLandsNowhere_LeavesNoDraft_SoLeavingNeverAsks()
    {
        var f = new ChordLadderFixture();
        NoteOnTechnique(f);
        var sections = NoteTechniqueSections();
        using var pages = InspectorStagedPagesTests.Pages(f, sections, applyStep: false);

        // A value the note already holds still stages (the scope may reach notes that do not), and lands nowhere.
        pages.Engine.StageTechnique(new StagedKey(TechniqueField.LinkNext), 0);
        Assert.True(pages.Engine.HasAnyDraft);

        Assert.False(pages.Engine.ApplyNow(StagedPage.Technique));

        Assert.Empty(f.History.GetUndoSteps());
        Assert.False(pages.Engine.HasAnyDraft);
        pages.Footer.ChooseSectionCommand.Execute(SoundSection.Placement);
        Assert.Equal(StagedAsk.None, pages.Footer.Asking);
        Assert.Equal(SoundSection.Placement, sections.Section);
    }

    [Fact]
    public void SwitchingTheApplyStepOff_WritesTheWaitingDraft_InOneUndoStep()
    {
        var f = new ChordLadderFixture();
        var note = NoteOnTechnique(f);
        using var pages = InspectorStagedPagesTests.Pages(f, NoteTechniqueSections(), applyStep: true);
        pages.NoteTechnique.LinkNext = true;
        Assert.False(note.LinkNext);

        f.State.InspectorApplyStep = false;

        Assert.True(note.LinkNext);
        Assert.Single(f.History.GetUndoSteps());
        Assert.False(pages.Engine.HasAnyDraft);
        Assert.False(pages.Footer.ShowsApplyButton);
    }

    private static GuitarNote NoteOnTechnique(ChordLadderFixture f)
    {
        f.Selection.ClearSelections();
        var note = InspectorStagedEditsTests.AddNote(f, 20, 5);
        f.Selection.SelectedNotes.Add(note);
        f.Selection.SelectedNote = note;
        return note;
    }

    private static SoundSectionsViewModel NoteTechniqueSections()
    {
        var sections = new SoundSectionsViewModel { Kind = SoundSelectionKind.Note };
        sections.ChooseCommand.Execute(SoundSection.Technique);
        return sections;
    }
}
