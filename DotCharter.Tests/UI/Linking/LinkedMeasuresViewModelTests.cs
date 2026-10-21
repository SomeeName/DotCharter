using DotCharter.Services.Editing.Linking;
using DotCharter.Tests.Services.State.Linking;
using static DotCharter.Tests.Core.Linking.LinkFixture;

namespace DotCharter.Tests.UI.Linking;

/// <summary>
///     What the linked-measures surfaces show and do: the toast after a propagated edit and its
///     Only here, the Inspector banner following the selection, Link identical measures, and the
///     link panel's three group edits.
/// </summary>
public sealed class LinkedMeasuresViewModelTests
{
    private static int Fret(Arrangement arrangement, int measure) => LinkHarness.NoteAt(arrangement, At(measure, 1), 1).Fret;

    // ── toast ───────────────────────────────────────────────────────────────

    [Fact]
    public void A_propagated_edit_shows_the_toast_and_flashes_the_copies()
    {
        var t = LinkedMeasuresHarness.WithRhythm();
        var h = t.Links;
        var group = h.Link((h.Lead, 1), (h.Lead, 4), (h.Rhythm, 4));

        h.Edit(h.Lead, LinkHarness.NoteAt(h.Lead, At(4, 1), 1), n => n.Fret = 8);

        Assert.True(t.Vm.IsToastVisible);
        Assert.True(t.Vm.CanOnlyHere);
        Assert.Equal(group.Letter, t.Vm.ToastLetter);
        Assert.Equal("Linked", t.Vm.ToastTitle);
        Assert.Equal("· changed 3 places (Lead ×2, Rhythm ×1)", t.Vm.ToastText);
        Assert.Equal(group.Id, t.Editor.LinkHighlight.FlashGroupId);
        Assert.Equal(2, t.Editor.LinkHighlight.FlashTargets.Count);
    }

    [Fact]
    public void Only_here_from_the_toast_keeps_the_edit_restores_the_copies_and_unlinks_this_placement()
    {
        var t = LinkedMeasuresHarness.WithRhythm();
        var h = t.Links;
        h.Link((h.Lead, 1), (h.Lead, 4), (h.Rhythm, 4));
        h.Edit(h.Lead, LinkHarness.NoteAt(h.Lead, At(4, 1), 1), n => n.Fret = 8);
        var steps = h.History.GetUndoSteps().Count;

        t.Vm.OnlyHereCommand.Execute(null);

        Assert.False(t.Vm.IsToastVisible);
        Assert.Equal(steps + 1, h.History.GetUndoSteps().Count);
        Assert.Equal(8, Fret(h.Lead, 4));
        Assert.Equal(7, Fret(h.Lead, 1));
        Assert.Equal(7, Fret(h.Rhythm, 4));
        Assert.Equal(2, h.Project.LinkGroups.Single().Placements.Count);
    }

    [Fact]
    public void A_tool_that_unlinks_on_divergence_shows_no_toast()
    {
        var t = LinkedMeasuresHarness.WithRhythm();
        var h = t.Links;
        h.Link((h.Lead, 1), (h.Lead, 4));

        h.History.Do(new ToolEdit(h.Mutate(h.Lead, LinkHarness.NoteAt(h.Lead, At(4, 1), 1), n => n.Fret = 8)));

        Assert.False(t.Vm.IsToastVisible);
        Assert.Null(t.Editor.LinkHighlight.FlashGroupId);
    }

    [Fact]
    public void A_generator_sharing_its_result_across_the_group_shows_no_toast()
    {
        var t = LinkedMeasuresHarness.WithRhythm();
        var h = t.Links;
        h.Link((h.Lead, 1), (h.Lead, 4));
        IReadOnlyList<LinkPropagationReport>? seen = null;
        h.History.LinksPropagated += reports => seen = reports;
        var generator = new CompositeAction([h.Mutate(h.Lead, LinkHarness.NoteAt(h.Lead, At(4, 1), 1), n => n.Fret = 8)])
        {
            LinkPolicyOverride = LinkPolicy.ShareFirstChanged
        };

        h.History.Do(generator);

        Assert.Equal(LinkPolicy.ShareFirstChanged, Assert.Single(seen!).Policy);
        Assert.Equal(8, Fret(h.Lead, 1));
        Assert.False(t.Vm.IsToastVisible);
    }

    // ── inspector banner ────────────────────────────────────────────────────

    [Fact]
    public void The_banner_follows_the_selection_in_and_out_of_a_placement()
    {
        var t = LinkedMeasuresHarness.WithRhythm();
        var h = t.Links;
        h.Link((h.Lead, 1), (h.Lead, 4), (h.Rhythm, 4));

        t.Select(t.NoteIn(4));
        Assert.True(t.Vm.IsBannerVisible);
        Assert.Equal("· edits apply to 3 places (Lead ×2, Rhythm ×1)", t.Vm.BannerText);
        Assert.True(t.Vm.OnlyThisOneCommand.CanExecute(null));

        var outside = Enumerable.Range(0, 9).First(m => m is not (1 or 4) &&
            LinkHarness.Hardest(h.Lead).Notes.Any(n => n.Position >= At(m, 0) && n.Position < At(m + 1, 0)));
        t.Select(t.NoteIn(outside));
        Assert.False(t.Vm.IsBannerVisible);
        Assert.False(t.Vm.OnlyThisOneCommand.CanExecute(null));
    }

    [Fact]
    public void Only_this_one_unlinks_the_placement_of_the_selection_in_one_step()
    {
        var t = LinkedMeasuresHarness.WithRhythm();
        var h = t.Links;
        h.Link((h.Lead, 1), (h.Lead, 4), (h.Rhythm, 4));
        t.Select(t.NoteIn(4));
        var steps = h.History.GetUndoSteps().Count;

        t.Vm.OnlyThisOneCommand.Execute(null);

        Assert.Equal(steps + 1, h.History.GetUndoSteps().Count);
        Assert.Equal(2, h.Project.LinkGroups.Single().Placements.Count);
        Assert.False(t.Vm.IsBannerVisible);
    }

    // ── link identical measures ─────────────────────────────────────────────

    [Fact]
    public void Link_identical_measures_links_every_equal_run_and_says_so()
    {
        var t = LinkedMeasuresHarness.WithRhythm();
        t.Select(t.NoteIn(4));

        t.Vm.LinkIdentical();

        var group = t.Links.Project.LinkGroups.Single();
        Assert.Equal(6, group.Placements.Count);
        Assert.True(t.Vm.IsToastVisible);
        Assert.False(t.Vm.CanOnlyHere);
        Assert.Equal("· linked 6 places (Lead ×3, Rhythm ×3)", t.Vm.ToastText);
    }

    [Fact]
    public void Link_identical_measures_shows_the_refusal_when_there_is_nothing_to_link()
    {
        var t = LinkedMeasuresHarness.WithRhythm();
        t.Select(t.NoteIn(4));
        t.Vm.LinkIdentical();
        var groups = t.Links.Project.LinkGroups.Count;
        var refusal = LinkIdenticalMeasures.Plan(t.Links.Project, t.Links.Lead, At(4, 0), At(4, 0), t.Links.Rule).Refusal;

        t.Vm.LinkIdentical();

        Assert.NotEqual(LinkCreateRefusal.None, refusal);
        Assert.Equal(groups, t.Links.Project.LinkGroups.Count);
        Assert.Equal("Link identical measures", t.Vm.ToastTitle);
        Assert.Equal(FakeLocalization.English()[$"LinkCreate_{refusal}"], t.Vm.ToastText);
        Assert.StartsWith("· ", t.Vm.ToastText, StringComparison.Ordinal);
        Assert.False(t.Vm.CanOnlyHere);
    }

    // ── panel ───────────────────────────────────────────────────────────────

    [Fact]
    public void The_panel_lists_placements_per_arrangement_and_checks_the_one_it_was_opened_on()
    {
        var t = LinkedMeasuresHarness.WithRhythm();
        var h = t.Links;
        var group = h.Link((h.Lead, 1), (h.Lead, 4), (h.Rhythm, 4));

        t.Vm.OpenPanel(group.Id, h.Lead, At(4, 2));
        var panel = t.Vm.Panel;

        Assert.Equal(["Lead", "Rhythm"], panel.Sections.Select(s => s.Header));
        Assert.Equal([false, true], panel.Sections[0].Rows.Select(r => r.IsChecked));
        Assert.True(panel.Sections[0].Rows[1].IsHere);
        Assert.Equal("1 of 3 selected", panel.SelectionSummary);
        Assert.Equal(group.Id, t.Editor.LinkHighlight.SelectedGroupId);
        Assert.True(panel.UnlinkSelectedCommand.CanExecute(null));
        Assert.False(panel.NewGroupFromSelectedCommand.CanExecute(null));
        Assert.True(panel.SplitByArrangementCommand.CanExecute(null));

        panel.Sections[1].Rows[0].IsChecked = true;
        Assert.True(panel.NewGroupFromSelectedCommand.CanExecute(null));
        foreach (var row in panel.Sections.SelectMany(s => s.Rows)) row.IsChecked = true;
        Assert.False(panel.NewGroupFromSelectedCommand.CanExecute(null));

        t.Vm.PanelClosed();
        Assert.Null(t.Editor.LinkHighlight.SelectedGroupId);
    }

    [Fact]
    public void Unlink_selected_is_one_step_and_the_panel_rereads_the_group()
    {
        var t = LinkedMeasuresHarness.WithRhythm();
        var h = t.Links;
        var group = h.Link((h.Lead, 1), (h.Lead, 4), (h.Rhythm, 4));
        t.Vm.OpenPanel(group.Id, h.Lead, At(4, 2));
        var steps = h.History.GetUndoSteps().Count;

        t.Vm.Panel.UnlinkSelectedCommand.Execute(null);

        Assert.Equal(steps + 1, h.History.GetUndoSteps().Count);
        Assert.Equal(2, h.Project.LinkGroups.Single(g => g.Id == group.Id).Placements.Count);
        Assert.Equal(2, t.Vm.Panel.Sections.Sum(s => s.Rows.Count));
    }

    [Fact]
    public void New_group_from_selected_and_split_by_arrangement_each_make_one_step()
    {
        var t = LinkedMeasuresHarness.WithRhythm();
        var h = t.Links;
        var group = h.Link((h.Lead, 1), (h.Lead, 4), (h.Lead, 7), (h.Rhythm, 4));
        t.Vm.OpenPanel(group.Id, h.Lead, At(4, 2));
        t.Vm.Panel.Sections[0].Rows[0].IsChecked = true;
        var steps = h.History.GetUndoSteps().Count;

        t.Vm.Panel.NewGroupFromSelectedCommand.Execute(null);

        Assert.Equal(steps + 1, h.History.GetUndoSteps().Count);
        Assert.Equal(2, h.Project.LinkGroups.Count);

        t.Vm.OpenPanel(group.Id, h.Lead, null);
        Assert.Equal(2, t.Vm.Panel.Sections.Count);
        t.Vm.Panel.SplitByArrangementCommand.Execute(null);

        Assert.Equal(steps + 2, h.History.GetUndoSteps().Count);
        Assert.All(h.Project.LinkGroups, g => Assert.Single(g.Placements.Select(p => p.Arrangement).Distinct()));
    }

    [Fact]
    public void The_panel_suggests_the_runs_that_play_the_same_again_and_link_adds_one_in_one_step()
    {
        var t = LinkedMeasuresHarness.WithRhythm();
        var h = t.Links;
        var group = h.Link((h.Lead, 1), (h.Lead, 4), (h.Rhythm, 4));

        t.Vm.OpenPanel(group.Id, h.Lead, At(4, 2));
        var panel = t.Vm.Panel;

        Assert.True(panel.HasSuggestions);
        Assert.Equal(["Lead", "Rhythm", "Rhythm"], panel.Suggestions.Select(s => s.Arrangement));
        Assert.Equal([At(7, 0), At(1, 0), At(7, 0)], panel.Suggestions.Select(s => s.Run.Start));
        var steps = h.History.GetUndoSteps().Count;

        panel.Suggestions[1].LinkCommand.Execute(null);

        Assert.Equal(steps + 1, h.History.GetUndoSteps().Count);
        Assert.Equal(4, h.Project.LinkGroups.Single().Placements.Count);
        Assert.Equal(4, panel.Sections.Sum(s => s.Rows.Count));
        Assert.Equal([At(7, 0), At(7, 0)], panel.Suggestions.Select(s => s.Run.Start));

        h.History.Undo();
        Assert.Equal(3, panel.Suggestions.Count);

        // Closed, the panel forgets the group and stops re-reading it on every edit.
        t.Vm.PanelClosed();
        Assert.Null(panel.GroupId);
        Assert.Empty(panel.Suggestions);
        Assert.False(panel.HasSuggestions);
    }

    /// <summary>A tool's edit: outside truth, never copied across a group.</summary>
    private sealed class ToolEdit(IChartAction inner) : IChartAction, ILinkPolicyAction
    {
        public LinkPolicy LinkPolicy => LinkPolicy.UnlinkOnDivergence;

        public void Execute() => inner.Execute();

        public void Undo() => inner.Undo();
    }
}
