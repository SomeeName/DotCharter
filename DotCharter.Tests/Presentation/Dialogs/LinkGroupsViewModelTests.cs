using CommunityToolkit.Mvvm.Input;
using DotCharter.Core.Linking;
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
        Assert.Equal("Unnamed · 4 measures", vm.Suggestions[0].Title);
        var steps = h.Steps;

        vm.LinkSelectedCommand.Execute(null);

        var group = Assert.Single(h.Link.Project.LinkGroups);
        Assert.Equal(suggestions[0].Name, group.Name);
        Assert.Equal(suggestions[0].Runs.Select(r => (r.Arrangement, r.Start, r.End)),
            group.Placements.Select(p => (p.Arrangement!, p.Position, p.EndPosition)));
        Assert.Equal(steps + 1, h.Steps);
        Assert.Single(vm.Groups);
        Assert.Equal(suggestions.Count - 1, vm.Suggestions.Count);

        foreach (var row in vm.Suggestions) row.IsSelected = false;
        Assert.False(vm.LinkSelectedCommand.CanExecute(null));
    }

    [Fact]
    public void The_summary_says_what_the_parts_share()
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
        Assert.Equal([$"Lead and Rhythm share {shared} of 9 measures."], vm.Details);

        vm.ReviewCommand.Execute(null);
        Assert.Equal(LinkSuggestionsChoice.Review, ChartEditHarness.Answer(vm.Result));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Project_edits_discard_in_flight_results(bool undoable)
    {
        var h = new Harness();
        h.Link.Link((h.Link.Lead, 1), (h.Link.Lead, 4));
        var cancellation = TestContext.Current.CancellationToken;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var vm = new LinkGroupsViewModel(h.Services, (snapshot, rule, _, _) =>
        {
            var proposals = LinkSuggestionFinder.Find(snapshot, snapshot.Arrangements, PatternRepeats, rule);
            entered.SetResult();
            release.Task.Wait(cancellation);
            return proposals;
        });
        var detection = (IAsyncRelayCommand)vm.DetectCommand;
        vm.Configure(new LinkGroupsRequest([]));
        await entered.Task.WaitAsync(cancellation);
        try
        {
            if (undoable)
            {
                Assert.True(h.Link.History.Do(LinkGroupEdits.Rename(h.Link.Project, h.Link.Project.LinkGroups[0].Id, "Edited")!));
            }
            else
            {
                h.Link.Project.LinkGroups[0].Name = "Edited";
                h.Link.State.NotifyProjectChanged(h.Link.Project);
            }
        }
        finally
        {
            release.SetResult();
        }

        await detection.ExecutionTask!.WaitAsync(cancellation);
        Assert.Empty(vm.Suggestions);
        Assert.True(vm.ShowDetectionRetry);
    }

    [Fact]
    public async Task Cancel_stops_the_run_and_retry_uses_a_fresh_token()
    {
        var h = new Harness();
        var cancellation = TestContext.Current.CancellationToken;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var vm = new LinkGroupsViewModel(h.Services, (snapshot, rule, token, _) =>
        {
            if (++calls == 1)
            {
                entered.SetResult();
                release.Task.Wait(cancellation);
            }

            token.ThrowIfCancellationRequested();
            return LinkSuggestionFinder.Find(snapshot, snapshot.Arrangements, PatternRepeats, rule);
        });
        var detection = (IAsyncRelayCommand)vm.DetectCommand;
        vm.Configure(new LinkGroupsRequest([]));
        await entered.Task.WaitAsync(cancellation);
        vm.CancelDetectionCommand.Execute(null);
        release.SetResult();
        await detection.ExecutionTask!.WaitAsync(cancellation);

        Assert.False(vm.Result.IsCompleted);
        Assert.Empty(vm.Suggestions);
        Assert.True(vm.ShowDetectionRetry);
        await Retry(vm);
        Assert.NotEmpty(vm.Suggestions);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(true, "Search stopped at the limit, results may be incomplete")]
    [InlineData(false, "Detection failed: Bad chart data")]
    public async Task Limit_and_failure_explain_why_detection_stopped(bool limited, string expectedStatus)
    {
        var h = new Harness();
        using var vm = new LinkGroupsViewModel(h.Services, (snapshot, rule, token, report) =>
            SearchUntilLimitOrFailure(snapshot, rule, token, report, limited));
        vm.Configure(new LinkGroupsRequest([]));
        await ((IAsyncRelayCommand)vm.DetectCommand).ExecutionTask!;

        Assert.Equal(expectedStatus, vm.DetectionStatus);
        Assert.False(vm.IsDetecting);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Detection_can_be_retried_after_a_limit_or_failure(bool limited)
    {
        var h = new Harness();
        var calls = 0;
        using var vm = new LinkGroupsViewModel(h.Services, (snapshot, rule, token, report) => ++calls == 1
            ? SearchUntilLimitOrFailure(snapshot, rule, token, report, limited)
            : LinkSuggestionFinder.Find(snapshot, snapshot.Arrangements, PatternRepeats, rule));
        vm.Configure(new LinkGroupsRequest([]));
        await ((IAsyncRelayCommand)vm.DetectCommand).ExecutionTask!;

        Assert.True(vm.ShowDetectionRetry);
        await Retry(vm);

        Assert.Equal(2, calls);
        Assert.Empty(vm.DetectionStatus);
        Assert.False(vm.ShowDetectionRetry);
        Assert.NotEmpty(vm.Suggestions);
    }

    private static IReadOnlyList<LinkSuggestion> SearchUntilLimitOrFailure(ChartProject snapshot,
        MeasureContentOptions rule, CancellationToken token, Action<string> report, bool limited)
    {
        if (!limited) throw new InvalidOperationException("Bad chart data\nMore details");
        return LinkSuggestionFinder.Find(snapshot, snapshot.Arrangements, PatternRepeats, rule,
            LinkDetectionScope.UnlinkedOnly, new LinkDetectionWork(token, 0), report);
    }

    private static async Task Retry(LinkGroupsViewModel vm)
    {
        Assert.True(vm.DetectCommand.CanExecute(null));
        vm.DetectCommand.Execute(null);
        await ((IAsyncRelayCommand)vm.DetectCommand).ExecutionTask!;
    }
}
