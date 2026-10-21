using DotCharter.Services.Editing.Linking;
using static DotCharter.Tests.Core.Linking.LinkFixture;

namespace DotCharter.Tests.Services.State.Linking;

/// <summary>
///     Re-link suggestions: the unlinked runs that play the same as a group again, and the link
///     panel's "Link", which adds one of them to the group as ONE undo step.
/// </summary>
public sealed class LinkRelinkSuggestionsTests
{
    private static LinkRunRequest Run(Arrangement arrangement, int measure) => new(arrangement, At(measure, 0), At(measure + 1, 0));

    private static List<(Arrangement, FractionalPosition)> Starts(IEnumerable<LinkRunRequest> runs) =>
        [.. runs.Select(r => (r.Arrangement, r.Start))];

    [Fact]
    public void Finds_every_equal_unlinked_run_in_every_compatible_arrangement()
    {
        var h = LinkHarness.WithRhythm();
        var group = h.Link((h.Lead, 1), (h.Lead, 4), (h.Rhythm, 1), (h.Rhythm, 4));

        var found = LinkRelinkSuggestions.Find(h.Project, group);

        Assert.Equal([(h.Lead, At(7, 0)), (h.Rhythm, At(7, 0))], Starts(found));
        Assert.All(found, r => Assert.Equal(At(8, 0), r.End));
    }

    [Fact]
    public void Ignores_runs_that_differ_sit_in_an_incompatible_arrangement_or_overlap_a_placement()
    {
        var h = LinkHarness.WithRhythm();
        var capo = ChartProjectCloner.CloneArrangement(h.Lead);
        capo.Name = "Capo 2";
        capo.Capo = 2;
        h.Project.Arrangements.Add(capo);
        var group = h.Link((h.Lead, 1), (h.Lead, 4));
        // Rhythm's bars 1 and 4 belong to another group; its bar 7 plays something else.
        h.Link((h.Rhythm, 1), (h.Rhythm, 4));
        LinkHarness.NoteAt(h.Rhythm, At(7, 1), 1).Fret = 9;
        h.Rhythm.MarkEdited();

        var found = LinkRelinkSuggestions.Find(h.Project, group);

        Assert.Equal([(h.Lead, At(7, 0))], Starts(found));
        Assert.False(LinkRelinkSuggestions.Qualifies(h.Project, group, Run(capo, 7)));
        Assert.False(LinkRelinkSuggestions.Qualifies(h.Project, group, Run(h.Rhythm, 7)));
        Assert.False(LinkRelinkSuggestions.Qualifies(h.Project, group, Run(h.Rhythm, 4)));
        Assert.False(LinkRelinkSuggestions.Qualifies(h.Project, group, new LinkRunRequest(h.Lead, At(7, 0), At(9, 0))));
        Assert.True(LinkRelinkSuggestions.Qualifies(h.Project, group, Run(h.Lead, 7)));
    }

    [Fact]
    public void A_placement_unlinked_with_only_here_is_suggested_again_once_its_edit_is_taken_back()
    {
        var h = LinkHarness.WithRhythm();
        h.Link((h.Lead, 1), (h.Lead, 4), (h.Lead, 7));
        LinkPropagationReport? report = null;
        h.History.LinksPropagated += reports => report = reports[0];
        h.Edit(h.Lead, LinkHarness.NoteAt(h.Lead, At(7, 1), 1), n => n.Fret = 8);
        Assert.True(h.History.OnlyHere(report!));
        var group = h.Project.LinkGroups.Single();
        Assert.DoesNotContain(LinkRelinkSuggestions.Find(h.Project, group), r => ReferenceEquals(r.Arrangement, h.Lead));

        // Put the note back by hand: bar 7 plays like the group again.
        h.Edit(h.Lead, LinkHarness.NoteAt(h.Lead, At(7, 1), 1), n => n.Fret = 7);

        Assert.Contains((h.Lead, At(7, 0)), Starts(LinkRelinkSuggestions.Find(h.Project, h.Project.LinkGroups.Single())));
    }

    [Fact]
    public void Link_adds_the_run_in_one_undo_step_and_undo_takes_it_out()
    {
        var h = LinkHarness.WithRhythm();
        var group = h.Link((h.Lead, 1), (h.Lead, 4));
        var steps = h.History.GetUndoSteps().Count;

        Assert.True(h.History.Do(LinkGroupEdits.AddPlacement(h.Project, group.Id, Run(h.Rhythm, 7))!));

        Assert.Equal(steps + 1, h.History.GetUndoSteps().Count);
        var linked = h.Project.LinkGroups.Single();
        Assert.Equal(group.Id, linked.Id);
        LinkHarness.AssertLinkedAndEqual(h, linked, 3);
        Assert.Contains(linked.Placements, p => ReferenceEquals(p.Arrangement, h.Rhythm) && p.Position == At(7, 0));

        // The new copy follows the group now.
        h.Edit(h.Lead, LinkHarness.NoteAt(h.Lead, At(1, 1), 1), n => n.Fret = 8);
        Assert.Equal(8, LinkHarness.NoteAt(h.Rhythm, At(7, 1), 1).Fret);

        h.History.Undo();
        h.History.Undo();
        Assert.Equal(2, h.Project.LinkGroups.Single().Placements.Count);
        Assert.DoesNotContain(h.Project.LinkGroups.Single().Placements, p => ReferenceEquals(p.Arrangement, h.Rhythm));
        Assert.Equal(7, LinkHarness.NoteAt(h.Rhythm, At(7, 1), 1).Fret);
    }

    [Fact]
    public void Link_is_refused_when_the_run_no_longer_plays_the_same()
    {
        var h = LinkHarness.WithRhythm();
        var group = h.Link((h.Lead, 1), (h.Lead, 4));
        var suggested = LinkRelinkSuggestions.Find(h.Project, group).Single(r => ReferenceEquals(r.Arrangement, h.Rhythm) && r.Start == At(7, 0));
        LinkHarness.NoteAt(h.Rhythm, At(7, 1), 1).Fret = 9;
        h.Rhythm.MarkEdited();

        Assert.Null(LinkGroupEdits.AddPlacement(h.Project, group.Id, suggested));
        Assert.Null(LinkGroupEdits.AddPlacement(h.Project, "no such group", Run(h.Lead, 7)));
    }
}
