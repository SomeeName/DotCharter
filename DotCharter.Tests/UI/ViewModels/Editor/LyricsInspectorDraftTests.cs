using DotCharter.Tests.Presentation.Dialogs;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     The lyrics inspector's text box is a DRAFT until it commits. A history change of the same
///     syllable keeps a typed draft while the syllable's stored text is still the text the draft
///     started from; a changed stored text, or a syllable that left the chart, wins. Every reader
///     of the chart (save, export, the export check, close) applies a pending draft first through
///     the editor's <c>CommitPendingEdits</c>, as one history entry at most.
/// </summary>
public sealed class LyricsInspectorDraftTests
{
    [Fact]
    public void ADirtyDraft_SurvivesAHistoryChangeThatLeavesItsStoredTextAlone()
    {
        var harness = new ChartEditHarness();
        var rig = new LyricDraftRig(harness, "hello");

        harness.History.Do(new SetLyricNoteAction(rig.Syllable, 60, 62));
        harness.History.Do(new RenameLyricAction(rig.Phrase.Lyrics[1], "lo", "lu", false, false, true, true));

        Assert.Equal("hello", rig.Inspector.SelectedLyricText);
        Assert.Equal("la", rig.Syllable.Text);
        rig.Inspector.CommitSelectedLyricText();
        Assert.Equal("hello", rig.Syllable.Text);
        Assert.Equal("hello", rig.Inspector.SelectedLyricText);
    }

    [Fact]
    public void AChangedStoredText_WinsAndDropsTheDraft()
    {
        var harness = new ChartEditHarness();
        var rig = new LyricDraftRig(harness, "hello");

        harness.History.Do(new RenameLyricAction(rig.Syllable, "la", "le", false, false, false, false));
        var node = harness.History.CurrentNodeId;

        Assert.Equal("le", rig.Inspector.SelectedLyricText);
        harness.Editor.CommitPendingEdits();
        Assert.Equal("le", rig.Syllable.Text);
        Assert.Equal(node, harness.History.CurrentNodeId);
    }

    [Fact]
    public void AnUndoOfTheSyllable_WinsOverADraftTypedAfterTheCommit()
    {
        var harness = new ChartEditHarness();
        var rig = new LyricDraftRig(harness, "le");
        rig.Inspector.CommitSelectedLyricText();
        Assert.Equal("le", rig.Syllable.Text);

        rig.Inspector.SelectedLyricText = "lu";
        harness.History.Undo();

        Assert.Equal("la", rig.Syllable.Text);
        Assert.Equal("la", rig.Inspector.SelectedLyricText);
    }

    [Fact]
    public void ASyllableThatLeftTheChart_DropsTheDraft()
    {
        var harness = new ChartEditHarness();
        var rig = new LyricDraftRig(harness, "hello");

        harness.History.Do(new RemoveLyricAction(rig.Track.Phrases, rig.Phrase, rig.Syllable));
        var node = harness.History.CurrentNodeId;

        Assert.NotEqual("hello", rig.Inspector.SelectedLyricText);
        harness.Editor.CommitPendingEdits();
        Assert.Equal(node, harness.History.CurrentNodeId);
        Assert.Equal("la", rig.Syllable.Text);
    }

    [Fact]
    public void ADraftThatIsNotDirty_FollowsTheStoredText()
    {
        var harness = new ChartEditHarness();
        var rig = new LyricDraftRig(harness, "la");

        harness.History.Do(new RenameLyricAction(rig.Syllable, "la", "le", false, false, false, false));
        Assert.Equal("le", rig.Inspector.SelectedLyricText);
        harness.History.Undo();
        Assert.Equal("la", rig.Inspector.SelectedLyricText);
    }

    /// <summary>The seam a save, an export and a close call: one entry for a dirty draft, none for a
    /// second call, and the box shows the stored text afterwards (a trailing "-" became the word part).</summary>
    [Fact]
    public void TheEditorSeam_CommitsADirtyDraftAsOneEntry_AndNothingTwice()
    {
        var harness = new ChartEditHarness();
        var rig = new LyricDraftRig(harness, "le-");
        var before = harness.History.GetUndoSteps().Count;

        harness.Editor.CommitPendingEdits();
        var node = harness.History.CurrentNodeId;
        harness.Editor.CommitPendingEdits();

        Assert.Equal(before + 1, harness.History.GetUndoSteps().Count);
        Assert.Equal(node, harness.History.CurrentNodeId);
        Assert.Equal("le", rig.Syllable.Text);
        Assert.True(rig.Syllable.IsWordPart);
        Assert.Equal("le", rig.Inspector.SelectedLyricText);
    }

    [Fact]
    public void TheEditorSeam_MakesNoEntryForAnUnchangedDraft()
    {
        var harness = new ChartEditHarness();
        _ = new LyricDraftRig(harness, "la");
        var node = harness.History.CurrentNodeId;

        harness.Editor.CommitPendingEdits();

        Assert.Equal(node, harness.History.CurrentNodeId);
    }

    /// <summary>
    ///     An undo that moves the selection away from a typed draft while its history notification
    ///     runs: before the undo applies (the focus request) or after it (the change notification),
    ///     or the inspector leaving its Lyrics tab inside the notification. The undo's own result
    ///     stays, the draft (its stored text untouched) is committed once as a NEW action after the
    ///     undo — so the undone step is no longer the redo (the history keeps it as a sibling
    ///     branch, as for any new action after an undo) — and undo / redo walk the commit cleanly.
    /// </summary>
    [Theory]
    [InlineData("before-apply", "select")]
    [InlineData("after-apply", "select")]
    [InlineData("before-apply", "leave-tab")]
    [InlineData("after-apply", "leave-tab")]
    public void AnUndoThatMovesAwayFromADraft_KeepsItsResult_AndCommitsTheDraftOnceAfterIt(string when, string how)
    {
        var harness = new ChartEditHarness();
        var rig = new LyricDraftRig(harness, "lu");
        var other = rig.Phrase.Lyrics[1];
        harness.History.Do(new RenameLyricAction(other, "lo", "le", false, false, true, true));
        Assert.Equal("lu", rig.Inspector.SelectedLyricText);
        var fired = false;
        void MoveAway()
        {
            if (fired) return;
            fired = true;
            if (how == "select") harness.Editor.Selection.SelectedLyric = other;
            else rig.Inspector.CommitPendingEdit();
        }

        if (when == "before-apply") harness.History.FocusRequested += _ => MoveAway();
        else harness.History.HistoryChanged += MoveAway;
        harness.History.Undo();

        Assert.True(fired);
        Assert.Equal("lo", other.Text);
        Assert.Equal("lu", rig.Syllable.Text);
        Assert.Single(harness.History.GetUndoSteps());
        Assert.False(harness.History.CanRedo);
        harness.History.Undo();
        Assert.Equal("la", rig.Syllable.Text);
        Assert.Equal("lo", other.Text);
        harness.History.Redo();
        Assert.Equal("lu", rig.Syllable.Text);
        Assert.Equal("lo", other.Text);
    }

    /// <summary>
    ///     The app's own order: its focus handler (subscribed before any inspector exists) moves the
    ///     editor to the undo's arrangement and clears the selection before the inspector hears of the
    ///     undo. The draft still waits for the step to apply and is then committed once, as a new step.
    /// </summary>
    [Fact]
    public void AFocusHandlerSubscribedFirst_ClearingTheSelection_StillDefersTheDraftPastTheUndo()
    {
        var harness = new ChartEditHarness();
        var cleared = false;
        harness.History.FocusRequested += _ =>
        {
            if (cleared) return;
            cleared = true;
            harness.Editor.Selection.SelectedLyric = null;
        };
        var rig = new LyricDraftRig(harness, "lu");
        var other = rig.Phrase.Lyrics[1];
        harness.History.Do(new RenameLyricAction(other, "lo", "le", false, false, true, true));

        harness.History.Undo();

        Assert.True(cleared);
        Assert.Equal("lo", other.Text);
        Assert.Equal("lu", rig.Syllable.Text);
        Assert.Single(harness.History.GetUndoSteps());
        Assert.False(harness.History.CanRedo);
        harness.History.Undo();
        Assert.Equal("la", rig.Syllable.Text);
        Assert.Equal("lo", other.Text);
    }

    /// <summary>An undo whose apply throws leaves no step "in progress": a draft commits at once.</summary>
    [Fact]
    public void AStepThatThrows_DoesNotHoldBackALaterDraftCommit()
    {
        var harness = new ChartEditHarness();
        var rig = new LyricDraftRig(harness, "lu");
        harness.History.Do(new ThrowingUndoAction());
        Assert.Throws<InvalidOperationException>(harness.History.Undo);
        var node = harness.History.CurrentNodeId;

        harness.Editor.CommitPendingEdits();

        Assert.Equal("lu", rig.Syllable.Text);
        Assert.NotEqual(node, harness.History.CurrentNodeId);
    }

    /// <summary>
    ///     A jump from the History dialog to a node on another branch, two levels up, with a dirty draft
    ///     whose selection the first step's focus request clears: the jump lands on the chosen node and the
    ///     draft is committed once, after the jump, as a child of that node.
    /// </summary>
    [Fact]
    public void AJumpWithADirtyDraft_LandsOnTheChosenNode_AndCommitsTheDraftOnceUnderIt()
    {
        var harness = new ChartEditHarness();
        var rig = new LyricDraftRig(harness, "lu");
        var other = rig.Phrase.Lyrics[1];
        harness.History.Do(new RenameLyricAction(other, "lo", "le", false, false, true, true));
        harness.History.Do(new SetLyricNoteAction(other, 60, 62));
        var chosen = harness.History.CurrentNodeId;
        harness.History.Undo();
        harness.History.Undo();
        harness.History.Do(new RenameLyricAction(other, "lo", "li", false, false, true, true));
        harness.History.Do(new SetLyricNoteAction(other, 60, 64));
        Assert.Equal("lu", rig.Inspector.SelectedLyricText);
        var nodes = harness.History.GetTree().Count;
        var cleared = false;
        harness.History.FocusRequested += _ =>
        {
            if (cleared) return;
            cleared = true;
            harness.Editor.Selection.SelectedLyric = null;
        };

        Assert.True(harness.History.NavigateTo(chosen));

        Assert.True(cleared);
        Assert.Equal("le", other.Text);
        Assert.Equal(62, other.Note);
        Assert.Equal("lu", rig.Syllable.Text);
        var tree = harness.History.GetTree();
        Assert.Equal(nodes + 1, tree.Count);
        Assert.Equal(chosen, tree.Single(n => n.IsCurrent).ParentId);
    }

    /// <summary>
    ///     An undo that throws after its focus request deferred a draft: the failed step raises no change
    ///     notification, and the next, unrelated edit does not commit the draft on top of a half-applied
    ///     chart. The draft is pending in the box again, its syllable selected, for the user to commit or
    ///     discard.
    /// </summary>
    [Fact]
    public void AFailedStep_ReturnsItsDeferredDraftToTheBox_AndAnUnrelatedEditDoesNotCommitIt()
    {
        var harness = new ChartEditHarness();
        harness.History.FocusRequested += _ => harness.Editor.Selection.SelectedLyric = null;
        var rig = new LyricDraftRig(harness, "lu");
        var other = rig.Phrase.Lyrics[1];
        harness.History.Do(new ThrowingUndoAction());
        Assert.Throws<InvalidOperationException>(harness.History.Undo);
        var nodes = harness.History.GetTree().Count;

        harness.History.Do(new RenameLyricAction(other, "lo", "le", false, false, true, true));

        Assert.Equal(nodes + 1, harness.History.GetTree().Count);
        Assert.Equal("la", rig.Syllable.Text);
        Assert.Equal("le", other.Text);
        Assert.Same(rig.Syllable, harness.Editor.Selection.SelectedLyric);
        Assert.Equal("lu", rig.Inspector.SelectedLyricText);
        harness.Editor.CommitPendingEdits();
        Assert.Equal("lu", rig.Syllable.Text);
    }

    /// <summary>
    ///     A step that fails without a failed apply — the link rebuild throws after the action applied, or a
    ///     second focus listener throws after the first cleared the selection — still ends the deferral:
    ///     the next, unrelated edit commits nothing else, and the draft is pending in the box again.
    /// </summary>
    [Theory]
    [InlineData("link rebuild")]
    [InlineData("focus listener")]
    public void AStepThatFailsOutsideItsApply_ReturnsItsDeferredDraftToTheBox(string failure)
    {
        var harness = new ChartEditHarness();
        harness.History.FocusRequested += _ => harness.Editor.Selection.SelectedLyric = null;
        if (failure == "focus listener") harness.History.FocusRequested += _ => throw new InvalidOperationException("focus");
        var rig = new LyricDraftRig(harness, "lu");
        var other = rig.Phrase.Lyrics[1];
        harness.History.Do(new SetLyricNoteAction(other, 60, 62));
        if (failure == "link rebuild") harness.Project.LinkGroups.Add(null!);
        Assert.ThrowsAny<Exception>(harness.History.Undo);
        harness.Project.LinkGroups.Clear();
        Assert.False(harness.History.IsApplyingStep);
        var nodes = harness.History.GetTree().Count;

        harness.History.Do(new RenameLyricAction(other, "lo", "le", false, false, true, true));

        Assert.Equal(nodes + 1, harness.History.GetTree().Count);
        Assert.Equal("la", rig.Syllable.Text);
        Assert.Equal("le", other.Text);
        Assert.Same(rig.Syllable, harness.Editor.Selection.SelectedLyric);
        Assert.Equal("lu", rig.Inspector.SelectedLyricText);
    }

    private sealed class ThrowingUndoAction : IChartAction
    {
        public bool HasEffect => true;

        public void Execute()
        {
        }

        public void Undo() => throw new InvalidOperationException("undo failed");
    }

    /// <summary>The inspector's own LRC export reads the track: the draft goes in first.</summary>
    [Fact]
    public async Task AnLrcExport_AppliesThePendingDraftFirst()
    {
        var harness = new ChartEditHarness();
        var storage = new FakeFileStorageService { NextSaveFile = null };
        var rig = new LyricDraftRig(harness, "hey", storage);

        await rig.Inspector.ExportLrcCommand.ExecuteAsync(null);

        Assert.Equal("hey", rig.Syllable.Text);
        Assert.Single(storage.RequestedTitles);
    }
}
