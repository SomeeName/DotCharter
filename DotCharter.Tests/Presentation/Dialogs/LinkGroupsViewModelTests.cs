using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Linking;
using DotCharter.Tests.Services.State.Linking;
using static DotCharter.Tests.Core.Linking.LinkFixture;

namespace DotCharter.Tests.Presentation.Dialogs;

/// <summary>
///     Tools ▸ Linked measures and the import's link summary: rename, unlink all, go to a placement
///     and link the ticked suggestions — each edit one undo step — and the summary's lines.
/// </summary>
public sealed class LinkGroupsViewModelTests
{
    private static readonly int[] PatternRepeats = [0, 1, 2, 3, 1, 5, 6, 1, 8];

    private sealed class Harness
    {
        public Harness()
        {
            Link.Lead.EventPoints.Add(new EventPoint { Position = At(1, 0), Section = "chorus" });
            (var editor, Editor) = RecordingProxy.Create<IChartEditorSurface>();
            Services = new ChartEditDialogServices(editor, Link.History, Link.State,
                new FakeConfigurationService { AppConfig = Link.Config }, FakeLocalization.English(), new FakeAppLogger());
        }

        public LinkHarness Link { get; } = LinkHarness.WithRhythm();

        public RecordingProxy Editor { get; }

        public ChartEditDialogServices Services { get; }

        public int Steps => Link.History.GetUndoSteps().Count;

        public IReadOnlyList<LinkSuggestion> Suggestions() =>
            LinkSuggestionFinder.Find(Link.Project, [Link.Lead, Link.Rhythm], PatternRepeats, Link.Rule);
    }

    [Fact]
    public void Each_group_lists_its_letter_name_count_per_arrangement_and_placements()
    {
        var h = new Harness();
        h.Link.Link((h.Link.Lead, 1), (h.Link.Lead, 4), (h.Link.Rhythm, 4));

        var vm = new LinkGroupsViewModel(h.Services);

        var row = Assert.Single(vm.Groups);
        Assert.True(vm.HasGroups);
        Assert.False(vm.HasSuggestions);
        Assert.Equal("A", row.Letter);
        Assert.Equal("Riff", row.Name);
        Assert.Equal("Lead ×2, Rhythm ×1", row.Summary);
        Assert.Equal(["Lead · M2", "Lead · M5", "Rhythm · M5"], row.Placements.Select(p => p.Label));
    }

    [Fact]
    public void Rename_applies_the_edited_name_as_one_undo_step()
    {
        var h = new Harness();
        h.Link.Link((h.Link.Lead, 1), (h.Link.Lead, 4));
        var vm = new LinkGroupsViewModel(h.Services);
        var steps = h.Steps;

        vm.Groups[0].Name = "Chorus";
        vm.RenameCommand.Execute(vm.Groups[0]);

        Assert.Equal("Chorus", h.Link.Project.LinkGroups.Single().Name);
        Assert.Equal("Chorus", vm.Groups[0].Name);
        Assert.Equal(steps + 1, h.Steps);
        h.Link.History.Undo();
        Assert.Equal("Riff", h.Link.Project.LinkGroups.Single().Name);
    }

    [Fact]
    public void Unlink_all_removes_the_group_as_one_undo_step_and_keeps_the_notes()
    {
        var h = new Harness();
        h.Link.Link((h.Link.Lead, 1), (h.Link.Lead, 4));
        var notes = h.Link.Snapshot();
        var vm = new LinkGroupsViewModel(h.Services);

        vm.DeleteCommand.Execute(vm.Groups[0]);

        Assert.Empty(h.Link.Project.LinkGroups);
        Assert.False(vm.HasGroups);
        Assert.Equal(notes, h.Link.Snapshot());
        h.Link.History.Undo();
        Assert.Equal(2, h.Link.Project.LinkGroups.Single().Placements.Count);
    }

    [Fact]
    public void Go_to_shows_the_placements_arrangement_at_its_first_beat_and_closes()
    {
        var h = new Harness();
        h.Link.Link((h.Link.Lead, 1), (h.Link.Rhythm, 4));
        var vm = new LinkGroupsViewModel(h.Services);

        vm.GoToCommand.Execute(vm.Groups[0].Placements[1]);

        var (_, arrangement) = Assert.Single(h.Editor.Invocations, i => i.Method == nameof(IChartEditorSurface.SetActiveArrangement));
        Assert.Same(h.Link.Rhythm, arrangement[0]);
        var (_, seek) = Assert.Single(h.Editor.Invocations, i => i.Method == nameof(IChartEditorSurface.ExecuteSeek));
        Assert.Equal(TimelineMath.CalculateTimeMs(At(4, 0), h.Link.Project.Beats), (double)seek[0]!, 6);
        Assert.True(ChartEditHarness.Answer(vm.Result));
    }

    [Fact]
    public void Link_selected_links_only_the_ticked_suggestions_as_one_undo_step()
    {
        var h = new Harness();
        var suggestions = h.Suggestions();
        Assert.True(suggestions.Count >= 2, $"the fixture suggests {suggestions.Count} groups");
        var vm = new LinkGroupsViewModel(h.Services);
        vm.Configure(new LinkGroupsRequest(suggestions));
        Assert.True(vm.HasSuggestions);
        Assert.All(vm.Suggestions, s => Assert.True(s.IsSelected));
        foreach (var row in vm.Suggestions.Skip(1)) row.IsSelected = false;
        Assert.Equal("chorus · 1 measure", vm.Suggestions[0].Title);
        Assert.Contains("from the score's repeats", vm.Suggestions[0].Detail, StringComparison.Ordinal);
        var steps = h.Steps;

        vm.LinkSelectedCommand.Execute(null);

        var group = Assert.Single(h.Link.Project.LinkGroups);
        Assert.Equal("chorus", group.Name);
        Assert.Equal(6, group.Placements.Count);
        Assert.Equal(steps + 1, h.Steps);
        Assert.Single(vm.Groups);
        Assert.Equal(suggestions.Count - 1, vm.Suggestions.Count);

        foreach (var row in vm.Suggestions) row.IsSelected = false;
        Assert.False(vm.LinkSelectedCommand.CanExecute(null));
    }

    [Fact]
    public void The_summary_says_what_the_parts_share_and_what_repeats()
    {
        var h = new Harness();
        var suggestions = h.Suggestions();
        Assert.True(suggestions.Count >= 2, $"the fixture suggests {suggestions.Count} groups");
        var vm = new LinkSuggestionsViewModel(h.Services);

        vm.Configure(new LinkSuggestionsRequest(suggestions));

        var placements = suggestions.Sum(s => s.Runs.Count);
        Assert.Equal($"{suggestions.Count} groups to link, {placements} placements.", vm.Headline);
        var shared = suggestions
            .Where(s => s.Runs.Any(r => ReferenceEquals(r.Arrangement, h.Link.Rhythm)))
            .Sum(s => s.MeasureCount * s.Runs.Count(r => ReferenceEquals(r.Arrangement, h.Link.Lead)));
        Assert.Equal([$"Lead and Rhythm share {shared} of 9 measures.", "chorus repeats 3× in each."], vm.Details);

        vm.ReviewCommand.Execute(null);
        Assert.Equal(LinkSuggestionsChoice.Review, ChartEditHarness.Answer(vm.Result));
    }
}
