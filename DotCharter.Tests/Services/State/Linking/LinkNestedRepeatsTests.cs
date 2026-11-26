using DotCharter.Services.Editing.Linking;
using DotCharter.Tests.Core.Linking;

namespace DotCharter.Tests.Services.State.Linking;

/// <summary>
///     Identical repeats inside a part are linked too. A long unison run of two parts no longer
///     hides the section it shares with a later bar range, short repeats are linked nested inside
///     the section groups, and an edit in any of those bars reaches every identical bar. An anchor
///     on the downbeat right after a placement, used first by the next notes, belongs to the placement.
/// </summary>
public sealed class LinkNestedRepeatsTests
{
    /// <summary>
    ///     Bar contents of part A (0 = rest). The section S = 10 11 10 13 14 15 plays at bars 5 and
    ///     14; bar 12 is the only bar where part B differs, so the two parts' longest unison runs
    ///     are bars 1-11 and 13-23, each holding one S.
    /// </summary>
    private static readonly int[] PartA = [0, 1, 2, 3, 4, 10, 11, 10, 13, 14, 15, 20, 21, 22, 10, 11, 10, 13, 14, 15, 30, 31, 32, 33];

    private static LinkHarness TwoParts()
    {
        var partB = (int[])PartA.Clone();
        partB[12] = 99;
        var beats = LinkFixture.Beats(PartA.Length, seed: null);
        var project = new ChartProject();
        foreach (var beat in beats) project.Beats.Add(beat);
        project.Arrangements.Add(Part("Lead A", PartA));
        project.Arrangements.Add(Part("Lead B", partB));
        return new LinkHarness(project);
    }

    private static Arrangement Part(string name, int[] bars)
    {
        var arrangement = new Arrangement { Name = name, StringCount = 6 };
        for (var i = 0; i < 6; i++) arrangement.TuningPitches.Add(new TuningOffset(0));
        var level = new ChartLevel();
        arrangement.Levels.Add(level);
        level.Anchors.Add(new FretAnchor(LinkFixture.At(0, 0), 1));
        for (var m = 0; m < bars.Length; m++)
        {
            if (bars[m] == 0) continue;
            level.Notes.Add(LinkFixture.Note(LinkFixture.At(m, 0), 2, bars[m] % 20, 0));
            level.Notes.Add(LinkFixture.Note(LinkFixture.At(m, 2), 3, bars[m] / 10, 0));
        }

        return arrangement;
    }

    private static HashSet<(string Part, int First)> RunsOf(LinkSuggestion suggestion) =>
        [.. suggestion.Runs.Select((r, i) => (r.Arrangement.Name, suggestion.FirstMeasures[i]))];

    [Fact]
    public void A_unison_run_is_cut_where_a_section_it_shares_with_later_bars_starts()
    {
        var h = TwoParts();

        var suggestions = LinkSuggestionFinder.Find(h.Project, h.Project.Arrangements, null, h.Rule);

        var section = Assert.Single(suggestions, s => s.MeasureCount == 6 && s.Runs.Count == 4);
        Assert.Equal([("Lead A", 5), ("Lead B", 5), ("Lead A", 14), ("Lead B", 14)], RunsOf(section));
        var nested = Assert.Single(suggestions, s => s.MeasureCount == 1 && RunsOf(s).Contains(("Lead A", 7)));
        Assert.Equal(
            [("Lead A", 5), ("Lead A", 7), ("Lead A", 14), ("Lead A", 16), ("Lead B", 5), ("Lead B", 7), ("Lead B", 14), ("Lead B", 16)],
            RunsOf(nested));
    }

    [Fact]
    public void An_edit_in_a_nested_bar_reaches_every_identical_bar_in_every_part_and_unlinks_nothing()
    {
        var h = TwoParts();
        var suggestions = LinkSuggestionFinder.Find(h.Project, h.Project.Arrangements, null, h.Rule);
        Assert.True(h.History.Do(LinkSuggestionFinder.LinkAll(h.Project, suggestions, h.Rule).Action!));
        var placements = h.Project.LinkGroups.Sum(g => g.Placements.Count);
        Assert.Contains(h.Project.LinkGroups, g => g.Placements.Count == 8 && g.Placements.All(p => p.EndPosition == p.Position + LinkFixture.Beats(4)));

        // A sustain step on the riff note of bar 16 in part A (the history's own sustain verb).
        var note = LinkHarness.NoteAt(h.Lead, LinkFixture.At(16, 0), 2);
        Assert.True(h.Edit(h.Lead, note, n => n.Sustain = LinkFixture.Beats(1), MergeVerbs.Sustain));

        foreach (var arrangement in h.Project.Arrangements)
        {
            foreach (var bar in new[] { 5, 7, 14, 16 })
                Assert.Equal(LinkFixture.Beats(1), LinkHarness.NoteAt(arrangement, LinkFixture.At(bar, 0), 2).Sustain);
            Assert.Equal(FractionalPosition.Zero, LinkHarness.NoteAt(arrangement, LinkFixture.At(6, 0), 2).Sustain);
        }

        Assert.Equal(placements, h.Project.LinkGroups.Sum(g => g.Placements.Count));
    }

    [Fact]
    public void An_anchor_on_the_downbeat_after_a_placement_that_the_next_notes_use_is_propagated_with_it()
    {
        var h = TwoParts();
        var level = LinkHarness.Hardest(h.Lead);
        level.Anchors.Add(new FretAnchor(LinkFixture.At(1, 0), 1));
        level.Anchors.Add(new FretAnchor(LinkFixture.At(2, 0), 2));
        level.Anchors.Add(new FretAnchor(LinkFixture.At(20, 0), 1));
        level.Anchors.Add(new FretAnchor(LinkFixture.At(21, 0), 2));
        // Bars 1 and 20 are made identical: one riff, each followed by a bar whose notes use the downbeat anchor.
        foreach (var bar in new[] { 1, 20 })
        {
            foreach (var stale in level.Notes.Where(n => n.Position >= LinkFixture.At(bar, 0) && n.Position < LinkFixture.At(bar + 1, 0)).ToList())
                level.Notes.Remove(stale);
            level.Notes.Add(LinkFixture.Note(LinkFixture.At(bar, 1), 1, 3, 0));
        }

        var group = h.Link((h.Lead, 1), (h.Lead, 20));
        var anchor = level.Anchors.Single(a => a.Position == LinkFixture.At(2, 0));
        var moved = anchor.Clone();
        moved.Fret = 7;

        Assert.True(h.History.Do(new MutateAnchorsAction(level.Anchors, [anchor], [anchor.Clone()], [moved])));

        Assert.Equal(7, level.Anchors.Single(a => a.Position == LinkFixture.At(21, 0)).Fret);
        Assert.Equal(2, group.Placements.Count);
    }

    /// <summary>
    ///     Bars 1-4 and 9-12 linked. Bar 5 stores fret 3 on its downbeat and fret 7 on beat 2, and its
    ///     first note is on beat 3; bar 13 holds unrelated music on its own downbeat anchor.
    /// </summary>
    private static (LinkHarness H, ChartLevel Level) EndAnchorOutsideEveryPlacement()
    {
        var project = new ChartProject();
        foreach (var beat in LinkFixture.Beats(16, seed: null)) project.Beats.Add(beat);
        var arrangement = new Arrangement { Name = "Lead", StringCount = 6 };
        for (var i = 0; i < 6; i++) arrangement.TuningPitches.Add(new TuningOffset(0));
        var level = new ChartLevel();
        arrangement.Levels.Add(level);
        project.Arrangements.Add(arrangement);
        foreach (var bar in new[] { 0, 1, 9 }) level.Anchors.Add(new FretAnchor(LinkFixture.At(bar, 0), 1));
        for (var m = 0; m < 16; m++)
        {
            var fret = m is >= 1 and <= 4 ? m : m is >= 9 and <= 12 ? m - 8 : 12;
            if (m != 5) level.Notes.Add(LinkFixture.Note(LinkFixture.At(m, 0), 2, fret, 0));
        }

        level.Anchors.Add(new FretAnchor(LinkFixture.At(5, 0), 3));
        level.Anchors.Add(new FretAnchor(LinkFixture.At(5, 1), 7));
        level.Notes.Add(LinkFixture.Note(LinkFixture.At(5, 2), 2, 8, 0));
        level.Anchors.Add(new FretAnchor(LinkFixture.At(13, 0), 9));
        var h = new LinkHarness(project);
        var requests = new[] { 1, 9 }.Select(m => new LinkRunRequest(h.Lead, LinkFixture.At(m, 0), LinkFixture.At(m + 4, 0))).ToList();
        Assert.Equal(LinkCreateRefusal.None, LinkGroupCreator.TryBuild(h.Project, requests, "Riff", h.Rule, out var link));
        Assert.True(h.History.Do(link!));
        return (h, LinkHarness.Hardest(h.Lead));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_edit_outside_every_placement_never_moves_another_copys_end_anchor(bool addDownbeatNote)
    {
        var (h, level) = EndAnchorOutsideEveryPlacement();

        // An ordinary edit in unlinked bar 5 that makes its stored downbeat anchor the one its notes use first.
        IChartAction edit = addDownbeatNote
            ? new AddNotesAction(level.Notes, [LinkFixture.Note(LinkFixture.At(5, 0), 2, 3, 0)])
            : new RemoveAnchorsAction(level.Anchors, [level.Anchors.Single(a => a.Position == LinkFixture.At(5, 1))]);
        Assert.True(h.History.Do(edit));

        Assert.Equal(9, level.Anchors.Single(a => a.Position == LinkFixture.At(13, 0)).Fret);
        Assert.Equal(3, level.Anchors.Single(a => a.Position == LinkFixture.At(5, 0)).Fret);
    }

    [Fact]
    public void Undo_after_a_propagation_through_several_groups_is_one_step_that_restores_everything()
    {
        var h = TwoParts();
        var suggestions = LinkSuggestionFinder.Find(h.Project, h.Project.Arrangements, null, h.Rule);
        Assert.True(h.History.Do(LinkSuggestionFinder.LinkAll(h.Project, suggestions, h.Rule).Action!));
        var before = h.Snapshot();
        var steps = h.History.GetUndoSteps().Count;

        var note = LinkHarness.NoteAt(h.Lead, LinkFixture.At(16, 0), 2);
        Assert.True(h.Edit(h.Lead, note, n => n.Sustain = LinkFixture.Beats(1), MergeVerbs.Sustain));
        Assert.Equal(steps + 1, h.History.GetUndoSteps().Count);
        Assert.Equal(LinkFixture.Beats(1), LinkHarness.NoteAt(h.Project.Arrangements[1], LinkFixture.At(5, 0), 2).Sustain);

        h.History.Undo();

        Assert.Equal(steps, h.History.GetUndoSteps().Count);
        Assert.Equal(before, h.Snapshot());
    }
}
