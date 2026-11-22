using DotCharter.Tests.Presentation.Dialogs;
using DotCharter.Tone.Diagnostics;
using DotCharter.UI.ViewModels.Editor.Inspector;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     The Issues tab validates OFF the UI thread (#380): the rules run over a copy of the chart
///     on a worker, the result is posted back through the dispatcher, and a result the chart has
///     moved on from never lands. The dispatcher and the worker are injected, so nothing here
///     waits on a clock.
/// </summary>
public sealed class InspectorIssuesOffThreadTests
{
    /// <summary>Hang detector, not a timing assertion: a regression that blocks a pass fails the test instead of the run.</summary>
    private static readonly TimeSpan Detector = TimeSpan.FromSeconds(30);

    [Fact]
    public void TheRules_NeverRunOnTheCallingThread_AndTheListIsWrittenOnlyThroughTheDispatcher()
    {
        var (rules, vm, caller) = RunBlocking();

        Assert.NotEqual(caller, Assert.Single(rules.Threads));
        Assert.Equal(["pass 1"], vm.Items.Select(i => i.Message));
        Assert.Equal(1, vm.IssueCount);
        Assert.Same(vm.Items[0].Note, LiveNote(vm));
    }

    /// <summary>Not a test method: the calling thread stays BLOCKED until the pass has run, so the
    /// worker cannot be it, and the list is still empty until the dispatcher runs the posted landing.</summary>
    private static (FakeRules Rules, InspectorIssuesViewModel Vm, int Caller) RunBlocking()
    {
        var (_, rules, ui, vm) = Create(Task.Run);
        var caller = Environment.CurrentManagedThreadId;

        vm.RecomputeIssues();
        rules.FirstCall.Task.WaitAsync(Detector, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        ui.NextPost().GetAwaiter().GetResult();
        if (vm.Items.Count != 0 || vm.IssueCount != 0) throw new InvalidOperationException("the list was written before the dispatcher ran");

        ui.RunPosted();
        vm.SettleAsync().WaitAsync(Detector, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        return (rules, vm, caller);
    }

    [Fact]
    public async Task ABurstOfRequests_CostsOneTrailingPass_AndTheStaleResultIsDropped()
    {
        var gate = new Gate();
        var (_, rules, ui, vm) = Create(gate.Run);

        vm.RecomputeIssues();
        await gate.Started(1);
        vm.RecomputeIssues(); // edits while pass 1 is out: only the generation moves
        vm.RecomputeIssues();
        Assert.Equal(0, rules.Calls); // the rules wait behind the gate: pass 1 is the only one out

        gate.Release(1);
        await ui.NextPost();
        ui.RunPosted(); // pass 1 lands: stale, so dropped and the one trailing pass starts

        Assert.Empty(vm.Items);
        await gate.Started(2);
        gate.Release(2);
        await ui.NextPost();
        ui.RunPosted();
        await vm.SettleAsync().WaitAsync(Detector, TestContext.Current.CancellationToken);

        Assert.Equal(2, rules.Calls);
        Assert.Equal(["pass 2"], vm.Items.Select(i => i.Message));
    }

    [Fact]
    public async Task AnEdit_DuringThePass_DropsItsResult_AndTheNewerOneWins()
    {
        var gate = new Gate();
        var (harness, rules, ui, vm) = Create(gate.Run);

        vm.RecomputeIssues();
        await gate.Started(1);
        harness.Project.Arrangements[0].MarkEdited(); // a committed edit: the revision moved
        gate.Release(1);
        await ui.NextPost();
        ui.RunPosted();

        Assert.Empty(vm.Items);
        await gate.Started(2);
        gate.Release(2);
        await ui.NextPost();
        ui.RunPosted();
        await vm.SettleAsync().WaitAsync(Detector, TestContext.Current.CancellationToken);

        Assert.Equal(2, rules.Calls);
        Assert.Equal(["pass 2"], vm.Items.Select(i => i.Message));
    }

    [Fact]
    public async Task ASlowerOlderResult_NeverOverwritesANewerList()
    {
        var gate = new Gate();
        var (_, _, ui, vm) = Create(gate.Run);

        vm.RecomputeIssues();
        await gate.Started(1);
        gate.Release(1);
        await ui.NextPost();
        ui.RunPosted(); // pass 1 lands and shows
        Assert.Equal(["pass 1"], vm.Items.Select(i => i.Message));

        vm.RecomputeIssues(); // pass 2, which also lands
        await gate.Started(2);
        gate.Release(2);
        await ui.NextPost();
        ui.RunPosted();

        Assert.Equal(["pass 2"], vm.Items.Select(i => i.Message));
    }

    [Fact]
    public void TheEmptyGrid_StillClearsOnTheCallingThread_WithoutAPass()
    {
        var (harness, rules, _, vm) = Create(Task.Run);
        harness.State.Beats.Clear();

        vm.RecomputeIssues();

        Assert.Equal(0, rules.Calls);
        Assert.Empty(vm.Items);
    }

    [Fact]
    public void TheValidationService_IsOnlyCalledInsideTheOffThreadStep()
    {
        var src = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "ViewModels", "Editor",
            "Inspector", "InspectorIssuesViewModel.cs"));
        var at = src.IndexOf("_validationService.Validate(", StringComparison.Ordinal);
        Assert.True(at > 0);
        Assert.Equal(at, src.LastIndexOf("_validationService.Validate(", StringComparison.Ordinal));
        var step = src[src.IndexOf("private IssuePass Validate(", StringComparison.Ordinal)..];
        Assert.True(step.IndexOf("_validationService.Validate(", StringComparison.Ordinal) < step.IndexOf("private void Land(", StringComparison.Ordinal),
            "the rules must run in the worker step");
        Assert.Contains("await OffThread(() => Validate(", src);
        Assert.Contains("OffThread { get; set; } = Task.Run;", src);
    }

    private static GuitarNote LiveNote(InspectorIssuesViewModel vm) => vm.Items[0].Note!;

    private static (ChartEditHarness Harness, FakeRules Rules, ManualDispatcher Ui, InspectorIssuesViewModel Vm) Create(
        Func<Func<InspectorIssuesViewModel.IssuePass>, Task<InspectorIssuesViewModel.IssuePass>> offThread)
    {
        var harness = new ChartEditHarness();
        var arrangement = harness.Project.Arrangements[0];
        arrangement.Levels[0].Notes.Add(new GuitarNote { Position = new FractionalPosition(1), StringIndex = 0, Fret = 3 });
        var rules = new FakeRules();
        var ui = new ManualDispatcher();
        var vm = new InspectorIssuesViewModel(harness.State, harness.Editor, rules,
            RecordingProxy.Create<DotCharter.Services.Editing.Maintenance.IChartMaintenanceService>().Proxy,
            new ExportReadinessViewModel(harness.State, new CheckpointsViewModelTests.FakeDocumentService(),
                harness.Transport, harness.Editor, FakeLocalization.KeysOnly(),
                RecordingProxy.Create<DotCharter.Services.Editing.Maintenance.IChartMaintenanceService>().Proxy,
                new DotCharter.Presentation.Dialogs.DialogsSlot(), new FakeAppLogger(),
                new DotCharter.Services.Editing.Quality.ChartQualityReporter(new DotCharter.Services.Editing.Validation.ArrangementValidationService(new FakeAppLogger())),
                new DotCharter.Tests.Infrastructure.TestUiDispatcher()),
            harness.History, FakeLocalization.KeysOnly(), () => null, ui, new FakeAppLogger())
        {
            OffThread = offThread
        };
        return (harness, rules, ui, vm);
    }

    /// <summary>Answers one finding per call, named by the call number, on the first note of the copy it is given.</summary>
    private sealed class FakeRules : IArrangementValidationService
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public List<int> Threads { get; } = [];

        public TaskCompletionSource FirstCall { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<ArrangementIssue> Validate(Arrangement? arrangement, IList<BeatMarker> beats,
            double? totalAudioLengthMs = null, ChartProject? project = null)
        {
            var call = Interlocked.Increment(ref _calls);
            lock (Threads) Threads.Add(Environment.CurrentManagedThreadId);
            FirstCall.TrySetResult();
            return [new ArrangementIssue(ArrangementIssueType.NoEndPhrase, $"pass {call}", 500,
                Note: arrangement!.Levels[0].Notes[0])];
        }
    }

    /// <summary>A worker whose passes start when asked and finish when released.</summary>
    private sealed class Gate
    {
        private readonly Dictionary<int, TaskCompletionSource> _started = [];
        private readonly Dictionary<int, TaskCompletionSource> _release = [];
        private int _n;

        public Task<InspectorIssuesViewModel.IssuePass> Run(Func<InspectorIssuesViewModel.IssuePass> work)
        {
            int n;
            lock (_started) n = ++_n;
            var started = Slot(_started, n);
            var release = Slot(_release, n);
            return Task.Run(async () =>
            {
                started.TrySetResult();
                await release.Task;
                return work();
            });
        }

        public Task Started(int n) => Slot(_started, n).Task.WaitAsync(Detector, TestContext.Current.CancellationToken);

        public void Release(int n) => Slot(_release, n).TrySetResult();

        private static TaskCompletionSource Slot(Dictionary<int, TaskCompletionSource> map, int n)
        {
            lock (map)
            {
                if (!map.TryGetValue(n, out var slot))
                    map[n] = slot = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                return slot;
            }
        }
    }

    /// <summary>Posts wait until the test runs them; <see cref="NextPost" /> says when one is waiting.</summary>
    private sealed class ManualDispatcher : IUiDispatcher
    {
        private readonly Queue<Action> _posted = new();
        private TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool CheckAccess() => false;

        public void Post(Action action)
        {
            lock (_posted)
            {
                _posted.Enqueue(action);
                _arrived.TrySetResult();
            }
        }

        public void PostNormal(Action action) => Post(action);

        public Task InvokeAsync(Action action)
        {
            Post(action);
            return Task.CompletedTask;
        }

        public Task<T> InvokeAsync<T>(Func<T> func) => Task.FromResult(func());

        public Task<T> InvokeAsync<T>(Func<Task<T>> func) => func();

        public Task NextPost()
        {
            lock (_posted) return _arrived.Task.WaitAsync(Detector, TestContext.Current.CancellationToken);
        }

        public void RunPosted()
        {
            Action[] batch;
            lock (_posted)
            {
                batch = [.. _posted];
                _posted.Clear();
                _arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            foreach (var action in batch) action();
        }
    }
}
