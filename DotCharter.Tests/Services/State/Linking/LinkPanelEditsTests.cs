using DotCharter.Services.Editing.Linking;
using static DotCharter.Tests.Core.Linking.LinkFixture;

namespace DotCharter.Tests.Services.State.Linking;

/// <summary>
///     What the link UI drives: the propagation report behind the "Linked" toast, "Only here",
///     the link panel's group edits and "Link identical measures" — each one undo step.
/// </summary>
public sealed class LinkPanelEditsTests
{
    private static FractionalPosition Bent(int measure) => At(measure, 1);

    private static int Fret(Arrangement arrangement, int measure) => LinkHarness.NoteAt(arrangement, Bent(measure), 1).Fret;

    private static LinkPlacement Placement(Arrangement arrangement, int measure) =>
        new() { Arrangement = arrangement, Position = At(measure, 0), EndPosition = At(measure + 1, 0) };

    [Fact]
    public void A_propagated_edit_reports_its_source_and_every_copy_it_reached()
    {
        var h = LinkHarness.WithRhythm();
        var group = h.Link((h.Lead, 1), (h.Lead, 4), (h.Rhythm, 1), (h.Rhythm, 4));
        IReadOnlyList<LinkPropagationReport>? seen = null;
        h.History.LinksPropagated += reports => seen = reports;

        h.Edit(h.Lead, LinkHarness.NoteAt(h.Lead, Bent(4), 1), n => n.Fret = 8);

        var report = Assert.Single(seen!);
        Assert.Equal(group.Id, report.GroupId);
        Assert.Same(h.Lead, report.Source.Arrangement);
        Assert.Equal(At(4, 0), report.Source.Position);
        Assert.Equal(3, report.Targets.Count);
        Assert.Equal(2, report.Targets.Count(t => ReferenceEquals(t.Arrangement, h.Rhythm)));
    }

    [Fact]
    public void An_edit_outside_every_placement_reports_nothing()
    {
        var h = LinkHarness.WithRhythm();
        h.Link((h.Lead, 1), (h.Lead, 4));
        var raised = 0;
        h.History.LinksPropagated += _ => raised++;

        h.Edit(h.Lead, LinkHarness.NoteAt(h.Lead, At(3, 1.5), 4), n => n.Fret = 9);

        Assert.Equal(0, raised);
    }

    [Fact]
    public void Only_here_keeps_the_edit_restores_the_copies_and_unlinks_the_source_in_one_step()
    {
        var h = LinkHarness.WithRhythm();
        h.Link((h.Lead, 1), (h.Lead, 4), (h.Lead, 7), (h.Rhythm, 4));
        LinkPropagationReport? report = null;
        h.History.LinksPropagated += reports => report = reports[0];
        h.Edit(h.Lead, LinkHarness.NoteAt(h.Lead, Bent(4), 1), n => n.Fret = 8);
        var steps = h.History.GetUndoSteps().Count;

        Assert.True(h.History.OnlyHere(report!));

        Assert.Equal(steps + 1, h.History.GetUndoSteps().Count);
        Assert.Equal(8, Fret(h.Lead, 4));
        Assert.Equal(7, Fret(h.Lead, 1));
        Assert.Equal(7, Fret(h.Lead, 7));
        Assert.Equal(7, Fret(h.Rhythm, 4));
        var group = h.Project.LinkGroups.Single();
        Assert.Equal(3, group.Placements.Count);
        Assert.DoesNotContain(group.Placements, p => ReferenceEquals(p.Arrangement, h.Lead) && p.Position == At(4, 0));
        LinkHarness.AssertLinkedAndEqual(h, group, 3);

        // One undo: back to the propagated edit, all four linked.
        h.History.Undo();
        Assert.Equal(8, Fret(h.Lead, 1));
        Assert.Equal(8, Fret(h.Rhythm, 4));
        Assert.Equal(4, h.Project.LinkGroups.Single().Placements.Count);
        // Redo lands where the do did.
        h.History.Redo();
        Assert.Equal(7, Fret(h.Lead, 1));
        Assert.Equal(8, Fret(h.Lead, 4));
        // Two more undos: the edit is gone too.
        h.History.Undo();
        h.History.Undo();
        Assert.All(new[] { (h.Lead, 1), (h.Lead, 4), (h.Lead, 7), (h.Rhythm, 4) }, r => Assert.Equal(7, Fret(r.Item1, r.Item2)));
    }

    [Fact]
    public void Only_here_on_a_two_placement_group_dissolves_it()
    {
        var h = LinkHarness.WithRhythm();
        h.Link((h.Lead, 1), (h.Lead, 4));
        LinkPropagationReport? report = null;
        h.History.LinksPropagated += reports => report = reports[0];
        h.Edit(h.Lead, LinkHarness.NoteAt(h.Lead, Bent(1), 1), n => n.Fret = 8);

        Assert.True(h.History.OnlyHere(report!));

        Assert.Empty(h.Project.LinkGroups);
        Assert.Equal(8, Fret(h.Lead, 1));
        Assert.Equal(7, Fret(h.Lead, 4));
    }

    [Fact]
    public void Only_here_is_refused_once_another_step_came_after()
    {
        var h = LinkHarness.WithRhythm();
        h.Link((h.Lead, 1), (h.Lead, 4));
        LinkPropagationReport? report = null;
        h.History.LinksPropagated += reports => report = reports[0];
        h.Edit(h.Lead, LinkHarness.NoteAt(h.Lead, Bent(1), 1), n => n.Fret = 8);
        h.History.EndGesture();
        h.Edit(h.Lead, LinkHarness.NoteAt(h.Lead, At(3, 1.5), 4), n => n.Fret = 9);

        Assert.False(h.History.OnlyHere(report!));
        Assert.Equal(8, Fret(h.Lead, 4));
    }

    [Fact]
    public void Unlink_selected_takes_placements_out_and_undo_puts_them_back()
    {
        var h = LinkHarness.WithRhythm();
        var group = h.Link((h.Lead, 1), (h.Lead, 4), (h.Lead, 7));

        Assert.True(h.History.Do(LinkGroupEdits.Unlink(h.Project, group.Id, [Placement(h.Lead, 7)])!));
        Assert.Equal(2, h.Project.LinkGroups.Single().Placements.Count);

        h.Edit(h.Lead, LinkHarness.NoteAt(h.Lead, Bent(1), 1), n => n.Fret = 8);
        Assert.Equal(8, Fret(h.Lead, 4));
        Assert.Equal(7, Fret(h.Lead, 7));

        h.History.Undo();
        h.History.Undo();
        Assert.Equal(3, h.Project.LinkGroups.Single().Placements.Count);
    }

    [Fact]
    public void Unlinking_down_to_one_placement_dissolves_the_group()
    {
        var h = LinkHarness.WithRhythm();
        var group = h.Link((h.Lead, 1), (h.Lead, 4));

        h.History.Do(LinkGroupEdits.Unlink(h.Project, group.Id, [Placement(h.Lead, 4)])!);

        Assert.Empty(h.Project.LinkGroups);
    }

    [Fact]
    public void New_group_from_selected_gives_them_the_next_letter()
    {
        var h = LinkHarness.WithRhythm();
        var group = h.Link((h.Lead, 1), (h.Lead, 4), (h.Rhythm, 1), (h.Rhythm, 4));

        Assert.Null(LinkGroupEdits.NewGroupFrom(h.Project, group.Id, [Placement(h.Lead, 1)]));
        Assert.True(h.History.Do(LinkGroupEdits.NewGroupFrom(h.Project, group.Id, [Placement(h.Rhythm, 1), Placement(h.Rhythm, 4)])!));

        Assert.Equal(["A", "B"], h.Project.LinkGroups.Select(g => g.Letter));
        Assert.All(h.Project.LinkGroups[1].Placements, p => Assert.Same(h.Rhythm, p.Arrangement));
        Assert.All(h.Project.LinkGroups[0].Placements, p => Assert.Same(h.Lead, p.Arrangement));

        // The groups are independent now.
        h.Edit(h.Lead, LinkHarness.NoteAt(h.Lead, Bent(1), 1), n => n.Fret = 8);
        Assert.Equal(8, Fret(h.Lead, 4));
        Assert.Equal(7, Fret(h.Rhythm, 1));
    }

    [Fact]
    public void Split_by_arrangement_makes_one_group_per_arrangement_and_unlinks_singles()
    {
        var h = LinkHarness.WithRhythm();
        var group = h.Link((h.Lead, 1), (h.Lead, 4), (h.Lead, 7), (h.Rhythm, 1), (h.Rhythm, 4));

        Assert.True(h.History.Do(LinkGroupEdits.SplitByArrangement(h.Project, group.Id)!));
        Assert.Equal([3, 2], h.Project.LinkGroups.Select(g => g.Placements.Count));
        Assert.Equal(group.Id, h.Project.LinkGroups[0].Id);

        h.History.Undo();
        Assert.Equal(5, h.Project.LinkGroups.Single().Placements.Count);
        h.History.Do(LinkGroupEdits.Delete(h.Project, group.Id)!);

        var mixed = h.Link((h.Lead, 1), (h.Lead, 4), (h.Lead, 7), (h.Rhythm, 1));
        h.History.Do(LinkGroupEdits.SplitByArrangement(h.Project, mixed.Id)!);
        var lead = h.Project.LinkGroups.Single();
        Assert.Equal(3, lead.Placements.Count);
        Assert.All(lead.Placements, p => Assert.Same(h.Lead, p.Arrangement));
        Assert.Null(LinkGroupEdits.SplitByArrangement(h.Project, lead.Id));
    }

    [Fact]
    public void Rename_and_delete_are_undoable()
    {
        var h = LinkHarness.WithRhythm();
        var group = h.Link((h.Lead, 1), (h.Lead, 4));

        Assert.Null(LinkGroupEdits.Rename(h.Project, group.Id, "  "));
        h.History.Do(LinkGroupEdits.Rename(h.Project, group.Id, "Chorus")!);
        Assert.Equal("Chorus", h.Project.LinkGroups.Single().Name);
        h.History.Do(LinkGroupEdits.Delete(h.Project, group.Id)!);
        Assert.Empty(h.Project.LinkGroups);

        h.History.Undo();
        h.History.Undo();
        Assert.Equal("Riff", h.Project.LinkGroups.Single().Name);
    }

    [Fact]
    public void Link_identical_measures_finds_every_equal_run_in_every_compatible_arrangement()
    {
        var h = LinkHarness.WithRhythm();

        // A selection inside bar 4 widens to the whole bar.
        var plan = LinkIdenticalMeasures.Plan(h.Project, h.Lead, At(4, 1), At(4, 2), h.Rule);

        Assert.Equal(LinkCreateRefusal.None, plan.Refusal);
        Assert.Equal(6, plan.Runs.Count);
        Assert.Equal(At(4, 0), plan.Runs[0].Start);
        Assert.Equal(At(5, 0), plan.Runs[0].End);
        Assert.True(h.History.Do(plan.Action!));
        var group = h.Project.LinkGroups.Single();
        LinkHarness.AssertLinkedAndEqual(h, group, 6);

        // Already linked: nothing left to link.
        var again = LinkIdenticalMeasures.Plan(h.Project, h.Lead, At(4, 1), At(4, 2), h.Rule);
        Assert.NotEqual(LinkCreateRefusal.None, again.Refusal);
    }

    [Fact]
    public void Link_identical_measures_refuses_an_empty_selection()
    {
        var h = LinkHarness.WithRhythm();
        var empty = Enumerable.Range(0, 9).First(m => !PatternMeasures.Contains(m) &&
            !LinkHarness.Hardest(h.Lead).Notes.Any(n => n.Position >= At(m, 0) && n.Position < At(m + 1, 0)) &&
            !LinkHarness.Hardest(h.Lead).Chords.Any(c => c.Position >= At(m, 0) && c.Position < At(m + 1, 0)));

        var plan = LinkIdenticalMeasures.Plan(h.Project, h.Lead, At(empty, 0), At(empty, 2), h.Rule);

        Assert.Equal(LinkCreateRefusal.Empty, plan.Refusal);
        Assert.Null(plan.Action);
    }
}
