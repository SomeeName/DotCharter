using DotCharter.Core.Linking;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Editing;
using DotCharter.Tests.Presentation.Dialogs;
using DotCharter.Services.Editing.Linking;
using DotCharter.Tests.Core.Linking;

namespace DotCharter.Tests.Services.State.Linking;

/// <summary>
///     Suggestions and linking agree (#1034): a pair the strict link check refuses is never a plain
///     suggestion; it is a near match naming its first difference, and Link all reports every skip.
/// </summary>
public sealed class LinkNearMatchTests
{
    /// <summary>Measure 4 plays written bar 1 again; every other measure its own bar.</summary>
    private static readonly int[] Repeats = [0, 1, 2, 3, 1, 5, 6, 7, 8];

    /// <summary>One note at measures 1 and 4; <paramref name="change" /> makes the copy at measure 4 differ.</summary>
    private static LinkHarness Pair(Action<ChartLevel> change)
    {
        var project = LinkFixture.Project(seed: null);
        var level = new ChartLevel
        {
            Notes = [LinkFixture.Note(LinkFixture.At(1, 0), 0, 5, 1), LinkFixture.Note(LinkFixture.At(4, 0), 0, 5, 1)]
        };
        level.Anchors.Add(new FretAnchor(LinkFixture.At(1, 0), 5));
        level.Anchors.Add(new FretAnchor(LinkFixture.At(4, 0), 5));
        change(level);
        project.Arrangements[0].Levels = [level];
        return new LinkHarness(project);
    }

    private static LinkHarness SustainOff() =>
        Pair(level => level.Notes[1].Sustain = LinkFixture.Beats(0.75));

    private static LinkHarness FhpOff() => Pair(level => level.Anchors[1].Fret = 4);

    private static LinkSuggestion NearMatch(LinkHarness h)
    {
        var suggestions = LinkSuggestionFinder.Find(h.Project, [h.Lead], Repeats, h.Rule);
        Assert.DoesNotContain(suggestions, s => !s.RequiresReview);
        return Assert.Single(LinkSuggestionFinder.NearMatches(suggestions));
    }

    [Fact]
    public void A_sustain_a_sixteenth_shorter_is_a_near_match_naming_it()
    {
        var near = NearMatch(SustainOff());

        var mismatch = near.Mismatch!;
        Assert.Equal(LinkMismatchKind.Content, mismatch.Kind);
        Assert.Equal(4, mismatch.Measure);
        Assert.Equal(MeasureContentDifferenceKind.Sustain, mismatch.Difference!.Kind);
        Assert.Equal(new Fraction(-1, 16), mismatch.Difference.SustainChange);
        Assert.Equal(Fraction.Zero, mismatch.Difference.Beat);
    }

    [Fact]
    public void A_different_fret_hand_position_is_a_near_match_naming_both_frets()
    {
        var near = NearMatch(FhpOff());

        var mismatch = near.Mismatch!;
        Assert.Equal(4, mismatch.Measure);
        Assert.Equal(MeasureContentDifferenceKind.FretHandPosition, mismatch.Difference!.Kind);
        Assert.Equal(("5", "4"), (mismatch.Difference.Reference, mismatch.Difference.Other));
    }

    [Fact]
    public void Identical_copies_stay_a_plain_suggestion_that_links()
    {
        var h = Pair(_ => { });

        var suggestion = Assert.Single(LinkSuggestionFinder.Find(h.Project, [h.Lead], Repeats, h.Rule));
        var result = LinkSuggestionFinder.LinkAll(h.Project, [suggestion], h.Rule);

        Assert.False(suggestion.RequiresReview);
        Assert.Null(suggestion.Mismatch);
        Assert.NotNull(result.Action);
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public void Link_all_reports_a_plain_suggestion_it_cannot_link_with_the_difference()
    {
        var h = SustainOff();
        var runs = new[] { 1, 4 }.Select(m => new LinkRunRequest(h.Lead, LinkFixture.At(m, 0), LinkFixture.At(m + 1, 0))).ToList();
        var stale = new LinkSuggestion("Riff", 1, runs, [1, 4], FromRepeats: true);

        var result = LinkSuggestionFinder.LinkAll(h.Project, [stale], h.Rule);

        Assert.Null(result.Action);
        var skip = Assert.Single(result.Skipped);
        Assert.Same(stale, skip.Suggestion);
        Assert.Equal(LinkCreateRefusal.DifferentContent, skip.Mismatch.Refusal);
        Assert.Equal(MeasureContentDifferenceKind.Sustain, skip.Mismatch.Difference!.Kind);
    }

    [Fact]
    public void Link_all_counts_the_near_matches_it_skips()
    {
        var h = FhpOff();
        var suggestions = LinkSuggestionFinder.Find(h.Project, [h.Lead], Repeats, h.Rule);

        var result = LinkSuggestionFinder.LinkAll(h.Project, suggestions, h.Rule);

        Assert.Null(result.Action);
        Assert.Equal(suggestions.Count, result.Skipped.Count);
        Assert.All(result.Skipped, s => Assert.Equal(MeasureContentDifferenceKind.FretHandPosition, s.Mismatch.Difference!.Kind));
    }

    [Fact]
    public void A_join_whose_edge_cuts_a_sustain_across_the_barline_is_a_near_match_never_a_plain_join()
    {
        var project = LinkFixture.Project(seed: null);
        project.Arrangements[0].Levels =
        [
            new ChartLevel
            {
                Notes =
                [
                    .. new[] { 1, 4, 7 }.Select(m => new GuitarNote { Position = LinkFixture.At(m, 0), Fret = 5 }),
                    LinkFixture.Note(LinkFixture.At(6, 3), 2, 7, 2)
                ]
            }
        ];
        var h = new LinkHarness(project);
        var group = h.Link((h.Lead, 1), (h.Lead, 4));

        var suggestions = LinkSuggestionFinder.Find(h.Project, [h.Lead], null, h.Rule);

        Assert.DoesNotContain(suggestions, s => s.Join != null && !s.RequiresReview);
        var near = Assert.Single(LinkSuggestionFinder.NearMatches(suggestions), s => s.Join?.GroupId == group.Id);
        Assert.Equal(LinkMismatchKind.Edge, near.Mismatch!.Kind);
        Assert.Equal(7, near.Mismatch.Measure);
        Assert.Null(LinkSuggestionFinder.LinkAll(h.Project, suggestions, h.Rule).Action);
    }

    [Theory]
    [InlineData(true, "M5, beat 1: sustain 1/16 shorter")]
    [InlineData(false, "M5, beat 1: FHP 5 vs 4")]
    public void The_difference_reads_in_charter_words(bool sustain, string expected)
    {
        var near = NearMatch(sustain ? SustainOff() : FhpOff());
        Assert.Equal(expected, LinkGroupText.Mismatch(FakeLocalization.English(), near.Mismatch!));
    }

    [Fact]
    public void Both_link_dialogs_list_the_near_match_with_its_difference()
    {
        var h = SustainOff();
        var suggestions = LinkSuggestionFinder.Find(h.Project, [h.Lead], Repeats, h.Rule);
        var (editor, _) = RecordingProxy.Create<IChartEditorSurface>();
        var services = new ChartEditDialogServices(editor, h.History, h.State,
            new FakeConfigurationService { AppConfig = h.Config }, FakeLocalization.English(), new FakeAppLogger());
        var summary = new LinkSuggestionsViewModel(services);
        var manager = new LinkGroupsViewModel(services);

        summary.Configure(new LinkSuggestionsRequest(suggestions));
        manager.Configure(new LinkGroupsRequest(suggestions));

        Assert.Equal(["1 near match is not linked: its copies differ here.", "  M5, beat 1: sustain 1/16 shorter"], summary.Details);
        var row = Assert.Single(manager.Suggestions);
        Assert.EndsWith("near match: M5, beat 1: sustain 1/16 shorter", row.Detail);
        Assert.False(row.IsSelected);
    }
}
