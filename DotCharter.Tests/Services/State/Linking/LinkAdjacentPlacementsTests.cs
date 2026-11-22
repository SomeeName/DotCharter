using DotCharter.Core.Linking;
using DotCharter.Services.Editing.Collisions;
using static DotCharter.Tests.Core.Linking.LinkFixture;

namespace DotCharter.Tests.Services.State.Linking;

/// <summary>
///     Back-to-back placements of one group: the copy onto the bar BEFORE the edited source pins
///     the hand the source starts with at the source's start. That stored anchor must reach every
///     copy's start in the same step, or the copies stop storing the same anchors.
/// </summary>
public sealed class LinkAdjacentPlacementsTests
{
    private static readonly int[] Placements = [1, 2, 5];

    private static ChartProject Chart()
    {
        var arrangement = new Arrangement { Name = "Lead", StringCount = 6 };
        for (var i = 0; i < 6; i++) arrangement.TuningPitches.Add(new TuningOffset(0));
        var level = new ChartLevel();
        arrangement.Levels.Add(level);

        AddAnchored(level, At(0, 0), 3);
        foreach (var m in Placements)
        {
            level.Notes.Add(Note(At(m, 0), 0, 3, 0.5));
            AddAnchored(level, At(m, 1), 7);
            AddAnchored(level, At(m, 3), 3);
        }

        AddAnchored(level, At(3, 0), 9);
        AddAnchored(level, At(4, 0), 3);

        var project = new ChartProject();
        foreach (var beat in Beats(8, null)) project.Beats.Add(beat);
        project.Arrangements.Add(arrangement);
        return project;
    }

    private static void AddAnchored(ChartLevel level, FractionalPosition at, int fret)
    {
        level.Anchors.Add(new FretAnchor(at, fret));
        level.Notes.Add(Note(at, fret == 7 ? 1 : 0, fret, 0.5));
    }

    [Fact]
    public void An_anchor_edit_in_the_second_of_two_adjacent_copies_keeps_every_copy_linked()
    {
        var h = new LinkHarness(Chart());
        var group = h.Link([.. Placements.Select(m => (h.Lead, m))]);
        var level = LinkHarness.Hardest(h.Lead);
        // A load's clean-up drops the start anchors the link pinned where they restate the hand in effect.
        ChartSpacingEnforcer.EnforceProject(h.Project, h.Config);
        Assert.DoesNotContain(level.Anchors, a => a.Position == At(2, 0));
        var anchor = level.Anchors.Single(a => a.Position == At(2, 3));
        var after = anchor.Clone();
        after.Fret = 4;

        Assert.True(h.History.Do(new MutateAnchorsAction(level.Anchors, [anchor], [anchor.Clone()], [after])));

        // The copies store the same anchors: the start anchor pinned at bar 2 is stored at every copy's start.
        Assert.All(Placements, m => Assert.Equal(3, Assert.Single(level.Anchors, a => a.Position == At(m, 0)).Fret));
        Assert.Single(Placements.Select(m => h.Integrity(h.Lead, m)).Distinct());
        LinkHarness.AssertLinkedAndEqual(h, group, 3);
        Assert.All(Placements, m => Assert.Equal(4, level.Anchors.Single(a => a.Position == At(m, 3)).Fret));
        var hash = h.Keys(group)[0]!.Hash;
        Assert.Empty(LinkGroupVerifier.Verify(h.Project, new Dictionary<LinkGroup, string> { [group] = hash }));
        LinkHarness.AssertLinkedAndEqual(h, group, 3);
    }

    [Fact]
    public void Undo_restores_every_anchor_of_the_adjacent_copies()
    {
        var h = new LinkHarness(Chart());
        h.Link([.. Placements.Select(m => (h.Lead, m))]);
        var level = LinkHarness.Hardest(h.Lead);
        ChartSpacingEnforcer.EnforceProject(h.Project, h.Config);
        var before = level.Anchors.Select(a => (a.Position, a.Fret)).OrderBy(a => a.Position).ToList();
        var anchor = level.Anchors.Single(a => a.Position == At(2, 3));
        var after = anchor.Clone();
        after.Fret = 4;

        Assert.True(h.History.Do(new MutateAnchorsAction(level.Anchors, [anchor], [anchor.Clone()], [after])));
        h.History.Undo();

        Assert.Equal(before, level.Anchors.Select(a => (a.Position, a.Fret)).OrderBy(a => a.Position).ToList());
    }
}
