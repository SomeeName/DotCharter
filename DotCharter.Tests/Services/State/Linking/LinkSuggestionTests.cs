using DotCharter.Core.Linking;
using DotCharter.Services.Editing.Linking;

namespace DotCharter.Tests.Services.State.Linking;

/// <summary>
///     What an import proposes to link (<see cref="LinkSuggestionFinder" />): the score's repeats
///     first, then repeated content, never an empty run or one that overlaps a group — and Link all
///     as ONE undo step.
/// </summary>
public sealed class LinkSuggestionTests
{
    /// <summary>Measures 1, 4 and 7 play written bar 1; every other measure its own bar.</summary>
    private static readonly int[] PatternRepeats = [0, 1, 2, 3, 1, 5, 6, 1, 8];

    private static List<(Arrangement Arrangement, int First)> RunsOf(LinkSuggestion suggestion) =>
        [.. suggestion.Runs.Select((r, i) => (r.Arrangement, suggestion.FirstMeasures[i]))];

    [Fact]
    public void The_scores_repeats_become_one_group_across_every_part_that_plays_them_the_same()
    {
        var h = LinkHarness.WithRhythm();

        var suggestions = LinkSuggestionFinder.Find(h.Project, [h.Lead, h.Rhythm], PatternRepeats, h.Rule);

        var repeat = suggestions[0];
        Assert.True(repeat.FromRepeats);
        Assert.Equal(1, repeat.MeasureCount);
        Assert.Equal(
            [(h.Lead, 1), (h.Lead, 4), (h.Lead, 7), (h.Rhythm, 1), (h.Rhythm, 4), (h.Rhythm, 7)],
            RunsOf(repeat));
        Assert.DoesNotContain(suggestions.Skip(1), s => s.Runs.Any(r => r.Start == repeat.Runs[0].Start));
    }

    [Fact]
    public void Without_a_repeat_structure_the_content_comparison_finds_what_the_parts_share()
    {
        var h = LinkHarness.WithRhythm();

        var suggestions = LinkSuggestionFinder.Find(h.Project, [h.Lead, h.Rhythm], null, h.Rule);

        // Rhythm is a copy of Lead, so the longest shared run spans the pattern measures in both.
        var found = Assert.Single(suggestions, s => RunsOf(s).Any(r =>
            ReferenceEquals(r.Arrangement, h.Lead) && r.First <= 4 && 4 < r.First + s.MeasureCount));
        Assert.False(found.FromRepeats);
        Assert.Contains(found.Runs, r => ReferenceEquals(r.Arrangement, h.Rhythm));
        Assert.True(found.MeasureCount > 1);
    }

    [Fact]
    public void Content_shared_between_the_repeats_is_still_offered_around_them()
    {
        var h = LinkHarness.WithRhythm();

        var suggestions = LinkSuggestionFinder.Find(h.Project, [h.Lead, h.Rhythm], PatternRepeats, h.Rule);

        var between = Assert.Single(suggestions, s => RunsOf(s).Contains((h.Lead, 2)));
        Assert.False(between.FromRepeats);
        Assert.Contains((h.Rhythm, 2), RunsOf(between));
        Assert.Equal(2, between.MeasureCount); // measures 2 and 3, up to the repeat at 4
        var all = suggestions.SelectMany(RunsOf).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
    }

    [Fact]
    public void A_repeat_list_of_another_length_is_ignored_rather_than_misread()
    {
        var h = LinkHarness.WithRhythm();

        var suggestions = LinkSuggestionFinder.Find(h.Project, [h.Lead, h.Rhythm], [0, 1, 0, 1], h.Rule);

        Assert.DoesNotContain(suggestions, s => s.FromRepeats);
    }

    [Fact]
    public void Measures_an_existing_group_holds_are_never_suggested_again()
    {
        var h = LinkHarness.WithRhythm();
        h.Link((h.Lead, 1), (h.Lead, 4));

        var suggestions = LinkSuggestionFinder.Find(h.Project, [h.Lead, h.Rhythm], PatternRepeats, h.Rule);

        var runs = suggestions.SelectMany(RunsOf).ToList();
        Assert.DoesNotContain((h.Lead, 1), runs);
        Assert.DoesNotContain((h.Lead, 4), runs);
        Assert.Contains((h.Lead, 7), runs);
        Assert.Contains((h.Rhythm, 4), runs);
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

        var action = LinkSuggestionFinder.LinkAll(h.Project, suggestions, h.Rule);

        Assert.Empty(h.Project.LinkGroups); // building it changes nothing; the history runs it
        Assert.True(h.History.Do(action!));
        Assert.Equal(steps + 1, h.History.GetUndoSteps().Count);
        Assert.Equal(suggestions.Count, h.Project.LinkGroups.Count);
        Assert.Equal(suggestions.Count, h.Project.LinkGroups.Select(g => g.Letter).Distinct().Count());
        foreach (var group in h.Project.LinkGroups)
            LinkHarness.AssertLinkedAndEqual(h, group, group.Placements.Count);

        h.History.Undo();
        Assert.Empty(h.Project.LinkGroups);
        Assert.Equal(before, h.Snapshot());

        h.History.Redo();
        Assert.Equal(suggestions.Count, h.Project.LinkGroups.Count);
    }

    [Fact]
    public void Link_all_of_suggestions_that_no_longer_hold_is_nothing()
    {
        var h = LinkHarness.WithRhythm();
        var suggestions = LinkSuggestionFinder.Find(h.Project, [h.Lead, h.Rhythm], PatternRepeats, h.Rule);
        Assert.True(h.History.Do(LinkSuggestionFinder.LinkAll(h.Project, suggestions, h.Rule)!));

        Assert.Null(LinkSuggestionFinder.LinkAll(h.Project, suggestions, h.Rule));
    }

    [Theory]
    [InlineData(new[] { 0, 1, 2, 3, 0, 1, 2, 4 }, 0, 3, new[] { 0, 4 })]
    [InlineData(new[] { 0, 1, 0, 1, 0, 1 }, 0, 2, new[] { 0, 2, 4 })]
    [InlineData(new[] { 5, 5, 6 }, 0, 1, new[] { 0, 1 })]
    public void The_repeat_runs_are_the_longest_runs_every_pass_plays_alike(int[] bars, int first, int length, int[] starts)
    {
        var run = Assert.Single(LinkSuggestionFinder.RepeatRuns(bars));

        Assert.Equal((first, length), (run.First, run.Length));
        Assert.Equal(starts, run.Starts);
    }

    [Fact]
    public void Unknown_bars_never_repeat() => Assert.Empty(LinkSuggestionFinder.RepeatRuns([-1, -1, -1]));
}
