using DotCharter.Core.Linking;
using DotCharter.Services.Editing.Linking;
using DotCharter.Tests.Core.Linking;

namespace DotCharter.Tests.Services.State.Linking;

/// <summary>
///     What an import proposes to link (<see cref="LinkSuggestionFinder" />), and Link All as one undo step.
/// </summary>
public sealed class LinkSuggestionTests
{
    /// <summary>Measures 1, 4 and 7 play written bar 1; every other measure its own bar.</summary>
    private static readonly int[] PatternRepeats = [0, 1, 2, 3, 1, 5, 6, 1, 8];

    private static List<(Arrangement Arrangement, int First)> RunsOf(LinkSuggestion suggestion) =>
        [.. suggestion.Runs.Select((r, i) => (r.Arrangement, suggestion.FirstMeasures[i]))];

    private static LinkHarness StandaloneRepeats()
    {
        var project = LinkFixture.Project(seed: null);
        project.Arrangements[0].Levels =
        [
            new ChartLevel
            {
                Notes = [.. new[] { 1, 4, 7 }.Select(m => new GuitarNote { Position = LinkFixture.At(m, 0), Fret = 5 })]
            }
        ];
        return new LinkHarness(project);
    }

    [Fact]
    public void The_scores_repeats_record_their_source_support()
    {
        var h = StandaloneRepeats();

        var suggestions = LinkSuggestionFinder.Find(h.Project, [h.Lead], PatternRepeats, h.Rule);

        var repeat = Assert.Single(suggestions, s => s.FromRepeats);
        Assert.True(repeat.FromRepeats);
        Assert.Equal(1, repeat.MeasureCount);
        Assert.Equal(
            [(h.Lead, 1), (h.Lead, 4), (h.Lead, 7)],
            RunsOf(repeat));
        Assert.DoesNotContain(suggestions.Skip(1), s => s.Runs.Any(r => r.Start == repeat.Runs[0].Start));
    }

    [Fact]
    public void A_run_found_by_both_content_and_source_builds_its_strict_key_once()
    {
        var h = StandaloneRepeats();
        var measures = MeasureGrid.Build(h.Project.Beats);
        var detector = new RankedLinkSuggestions(h.Project, measures, [h.Lead], PatternRepeats, [], h.Rule,
            LinkDetectionScope.All);

        Assert.Equal(3, Assert.Single(detector.Find()).Runs.Count);
        Assert.Equal(3, detector.RunKeyBuilds);
    }

    [Fact]
    public void The_search_budget_stops_candidate_work_and_reports_why()
    {
        var h = StandaloneRepeats();
        var messages = new List<string>();
        var work = new LinkDetectionWork(CancellationToken.None, MeasureGrid.Build(h.Project.Beats).Count);

        var suggestions = LinkSuggestionFinder.Find(h.Project, [h.Lead], PatternRepeats, h.Rule,
            LinkDetectionScope.All, work, messages.Add);

        Assert.Empty(suggestions);
        Assert.Contains("search limit", Assert.Single(messages));
    }

    [Fact]
    public void Oversized_compatibility_classes_are_reported()
    {
        var h = StandaloneRepeats();
        for (var i = 0; i < 400; i++) h.Project.Arrangements.Add(ChartProjectCloner.CloneArrangement(h.Lead));
        var messages = new List<string>();

        var suggestions = LinkSuggestionFinder.Find(h.Project, h.Project.Arrangements, PatternRepeats, h.Rule,
            CancellationToken.None, messages.Add);

        Assert.Empty(suggestions);
        Assert.Contains("MaxPairs", Assert.Single(messages));
    }

    [Fact]
    public void Without_a_repeat_structure_the_content_comparison_finds_what_the_parts_share()
    {
        var h = LinkHarness.WithRhythm();

        var suggestions = LinkSuggestionFinder.Find(h.Project, [h.Lead, h.Rhythm], null, h.Rule);

        // Rhythm is a copy of Lead, so the longest shared run spans the pattern measures in both.
        // The pattern bars are also linked nested inside that run, one bar long.
        var found = Assert.Single(suggestions, s => s.MeasureCount > 1 && RunsOf(s).Any(r =>
            ReferenceEquals(r.Arrangement, h.Lead) && r.First <= 4 && 4 < r.First + s.MeasureCount));
        Assert.False(found.FromRepeats);
        Assert.Contains(found.Runs, r => ReferenceEquals(r.Arrangement, h.Rhythm));
        Assert.True(found.MeasureCount > 1);
    }

    [Fact]
    public void A_single_bar_source_repeat_does_not_preempt_longer_shared_content()
    {
        var h = LinkHarness.WithRhythm();

        var suggestions = LinkSuggestionFinder.Find(h.Project, [h.Lead, h.Rhythm], PatternRepeats, h.Rule);

        var first = suggestions.First(s => !s.RequiresReview);
        Assert.True(first.MeasureCount > 1);
        Assert.Contains(first.Runs, r => ReferenceEquals(r.Arrangement, h.Rhythm));
    }

    [Fact]
    public void A_repeat_list_of_another_length_is_ignored_rather_than_misread()
    {
        var h = LinkHarness.WithRhythm();

        var suggestions = LinkSuggestionFinder.Find(h.Project, [h.Lead, h.Rhythm], [0, 1, 0, 1], h.Rule);

        Assert.DoesNotContain(suggestions, s => s.FromRepeats);
    }

    [Fact]
    public void The_same_written_position_does_not_make_different_parts_repeats()
    {
        var h = StandaloneRepeats();
        var other = ChartProjectCloner.CloneArrangement(h.Lead);
        foreach (var note in LinkHarness.Hardest(other).Notes) note.StringIndex++;
        h.Project.Arrangements.Add(other);
        var source = Enumerable.Range(0, MeasureGrid.Build(h.Project.Beats).Count).ToArray();

        var proposals = LinkSuggestionFinder.Find(h.Project, [h.Lead, other], source, h.Rule);

        Assert.Equal(2, proposals.Count);
        Assert.All(proposals, p => Assert.False(p.FromRepeats || p.RequiresReview));
    }

    [Fact]
    public void Measures_an_existing_group_holds_are_never_suggested_again()
    {
        var h = LinkHarness.WithRhythm();
        h.Link((h.Lead, 1), (h.Lead, 4));

        var suggestions = LinkSuggestionFinder.Find(h.Project, [h.Lead, h.Rhythm], PatternRepeats, h.Rule);

        var runs = suggestions.Where(s => !s.RequiresReview).SelectMany(s => RunsOf(s)
            .SelectMany(r => Enumerable.Range(r.First, s.MeasureCount).Select(m => (r.Arrangement, m)))).ToList();
        Assert.DoesNotContain((h.Lead, 1), runs);
        Assert.DoesNotContain((h.Lead, 4), runs);
        Assert.NotEmpty(runs);
    }

    [Fact]
    public void A_repeat_of_empty_measures_is_not_a_suggestion()
    {
        var h = LinkHarness.WithRhythm();
        var index = MeasureContentIndex.ForHardestLevel(h.Lead, h.Project.Beats, h.Rule);
        var empty = Enumerable.Range(0, index.Measures.Count).Where(m => index.KeyFor(m).IsEmpty).ToList();
        Assert.True(empty.Count >= 2, $"the fixture has {empty.Count} empty measures");
        var bars = Enumerable.Range(0, index.Measures.Count).ToArray();
        bars[empty[1]] = empty[0];

        var suggestions = LinkSuggestionFinder.Find(h.Project, [h.Lead, h.Rhythm], bars, h.Rule);

        Assert.DoesNotContain(suggestions, s => s.FromRepeats);
        Assert.DoesNotContain(suggestions.SelectMany(RunsOf), r => empty.Contains(r.First));
    }

    [Fact]
    public void Link_all_creates_every_group_with_its_own_letter_as_one_undo_step()
    {
        var h = LinkHarness.WithRhythm();
        var before = h.Snapshot();
        var suggestions = LinkSuggestionFinder.Find(h.Project, [h.Lead, h.Rhythm], PatternRepeats, h.Rule);
        Assert.NotEmpty(suggestions);
        var steps = h.History.GetUndoSteps().Count;

        var action = LinkSuggestionFinder.LinkAll(h.Project, suggestions, h.Rule).Action;

        Assert.Empty(h.Project.LinkGroups); // building it changes nothing; the history runs it
        Assert.True(h.History.Do(action!));
        Assert.Equal(steps + 1, h.History.GetUndoSteps().Count);
        Assert.Equal(suggestions.Count(s => !s.RequiresReview), h.Project.LinkGroups.Count);
        Assert.Equal(suggestions.Count, h.Project.LinkGroups.Select(g => g.Letter).Distinct().Count());
        foreach (var group in h.Project.LinkGroups)
            LinkHarness.AssertLinkedAndEqual(h, group, group.Placements.Count);

        h.History.Undo();
        Assert.Empty(h.Project.LinkGroups);
        Assert.Equal(before, h.Snapshot());

        h.History.Redo();
        Assert.Equal(suggestions.Count(s => !s.RequiresReview), h.Project.LinkGroups.Count);
    }

    [Fact]
    public void Link_all_of_suggestions_that_no_longer_hold_is_nothing()
    {
        var h = LinkHarness.WithRhythm();
        var suggestions = LinkSuggestionFinder.Find(h.Project, [h.Lead, h.Rhythm], PatternRepeats, h.Rule);
        Assert.True(h.History.Do(LinkSuggestionFinder.LinkAll(h.Project, suggestions, h.Rule).Action!));

        Assert.Null(LinkSuggestionFinder.LinkAll(h.Project, suggestions, h.Rule).Action);
    }

    [Fact]
    public void A_lone_run_equal_to_an_existing_group_is_suggested_to_join_it_and_link_all_adds_it()
    {
        var h = StandaloneRepeats();
        var group = h.Link((h.Lead, 1), (h.Lead, 4));
        var steps = h.History.GetUndoSteps().Count;

        var suggestions = LinkSuggestionFinder.Find(h.Project, [h.Lead], null, h.Rule);

        var join = Assert.Single(suggestions);
        Assert.Equal(new LinkJoinTarget(group.Id, group.Letter), join.Join);
        Assert.Equal([(h.Lead, 7)], RunsOf(join));
        Assert.True(h.History.Do(LinkSuggestionFinder.LinkAll(h.Project, suggestions, h.Rule).Action!));
        Assert.Equal(steps + 1, h.History.GetUndoSteps().Count);
        Assert.Same(group, Assert.Single(h.Project.LinkGroups));
        LinkHarness.AssertLinkedAndEqual(h, group, 3);
        var hash = h.Keys(group)[0]!.Hash;
        Assert.Empty(LinkGroupVerifier.Verify(h.Project, new Dictionary<LinkGroup, string> { [group] = hash }));
        Assert.Equal(3, group.Placements.Count);
        Assert.Empty(LinkSuggestionFinder.Find(h.Project, [h.Lead], null, h.Rule));

        h.History.Undo();
        Assert.Equal(2, Assert.Single(h.Project.LinkGroups).Placements.Count);
    }

    [Fact]
    public void Unlinked_runs_equal_to_an_existing_group_never_become_a_second_group()
    {
        var h = StandaloneRepeats();
        LinkHarness.Hardest(h.Lead).Notes.Add(new GuitarNote { Position = LinkFixture.At(2, 0), Fret = 5 });
        h.Lead.MarkEdited();
        var group = h.Link((h.Lead, 1), (h.Lead, 4));

        var suggestions = LinkSuggestionFinder.Find(h.Project, [h.Lead], null, h.Rule);

        var join = Assert.Single(suggestions);
        Assert.Equal(group.Id, join.Join?.GroupId);
        Assert.Equal([(h.Lead, 2), (h.Lead, 7)], RunsOf(join));
        Assert.True(h.History.Do(LinkSuggestionFinder.LinkAll(h.Project, suggestions, h.Rule).Action!));
        Assert.Same(group, Assert.Single(h.Project.LinkGroups));
        LinkHarness.AssertLinkedAndEqual(h, group, 4);
    }

    [Fact]
    public void Link_all_does_not_join_a_run_another_group_took_since()
    {
        var h = StandaloneRepeats();
        LinkHarness.Hardest(h.Lead).Notes.Add(new GuitarNote { Position = LinkFixture.At(2, 0), Fret = 5 });
        h.Lead.MarkEdited();
        var group = h.Link((h.Lead, 1), (h.Lead, 4));
        var run = new LinkRunRequest(h.Lead, LinkFixture.At(7, 0), LinkFixture.At(8, 0));
        var joining = new LinkSuggestion(group.Name, 1, [run], [7], FromRepeats: false)
        {
            Join = new LinkJoinTarget(group.Id, group.Letter)
        };
        h.Link((h.Lead, 2), (h.Lead, 7));

        Assert.Null(LinkSuggestionFinder.LinkAll(h.Project, [joining], h.Rule).Action);
    }

    [Fact]
    public void All_scope_can_propose_existing_placements_without_touching_the_project()
    {
        var h = StandaloneRepeats();
        var group = h.Link((h.Lead, 1), (h.Lead, 4));
        var before = h.Snapshot();

        var unlinked = LinkSuggestionFinder.Detect(h.Project, h.Lead, LinkDetectionScope.UnlinkedOnly, h.Rule);
        var all = LinkSuggestionFinder.Detect(h.Project, h.Lead, LinkDetectionScope.All, h.Rule);

        Assert.DoesNotContain(unlinked.SelectMany(s => s.FirstMeasures), m => m is 1 or 4);
        Assert.Contains(all, s => s.FirstMeasures.Contains(1) && s.FirstMeasures.Contains(4));
        Assert.Same(group, Assert.Single(h.Project.LinkGroups));
        Assert.Equal(before, h.Snapshot());
    }
}
