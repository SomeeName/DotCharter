using System.Text.RegularExpressions;
using DotCharter.Tone.Diagnostics;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     A disposed <see cref="StemMixerViewModel" /> leaves no work that can reach the
///     dispatcher (#263).
///     <para>
///         The rebuild loop resumes on a POOL thread after the worker probe and the
///         engine load, and only then dispatches its next step. The singleton is
///         disposed with its container — in the headless session that is
///         <c>App.ReleaseForIsolatedTeardown</c>, after every dispatch — but it had
///         no <c>Dispose</c>, so the pool thread went on to call
///         <c>Dispatcher.UIThread</c> after the app was gone. Between two headless
///         apps that read CREATES a dispatcher on the pool thread and makes it the
///         UI thread: <c>headless-uithread-claims.log</c> named it, queue head
///         <c>StemMixerViewModel.TakePendingRebuildOrRelease</c>, and
///         <c>HeadlessUiThreadClaimUiPerfSmokeTests</c> lost its staged race to it.
///     </para>
///     <para>
///         Deterministic, not timed: the engine load is parked on a gate whose
///         continuations run synchronously, so <c>SetResult</c> drives the whole
///         rest of the rebuild on the test thread before it returns.
///     </para>
/// </summary>
public sealed class StemMixerReleaseTests : IDisposable
{
    private const string ViewModel = "DotCharter.UI/ViewModels/Editor/StemMixerViewModel.cs";

    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "dotcharter-stemmixer-release-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => BestEffortDelete.Directory(_root);

    [Fact]
    public async Task A_rebuild_in_flight_at_dispose_dispatches_nothing_afterwards()
    {
        var (project, path) = Project("inflight", "bass", "drums");
        var audio = new FakeAudioPlaybackService();
        // Default options: the gate's continuations run INLINE in SetResult.
        var gate = new TaskCompletionSource();
        audio.StemLoadGate = gate;
        var logger = new FakeAppLogger();
        var ui = new CountingInlineDispatcher();
        var state = new ProjectState { CurrentProjectPath = path, ActiveProject = project };

        var vm = new StemMixerViewModel(state, audio, FakeLocalization.KeysOnly(), logger, ui);
        await StemMixerSettle.Async(() => audio.StemLoadCalls.Count >= 1);
        Assert.Single(audio.StemLoadCalls);

        // The rebuild is parked in the engine load, on a pool continuation.
        vm.Dispose();
        var dispatchesAtDispose = ui.Calls;

        gate.SetResult();
        await StemMixerSettle.Async();

        Assert.True(ui.Calls == dispatchesAtDispose,
            $"the disposed mixer's rebuild dispatched {ui.Calls - dispatchesAtDispose} more step(s) after Dispose "
            + "(FinishRebuild / TakePendingRebuildOrRelease). On Avalonia that call reads Dispatcher.UIThread from a "
            + "pool thread after the app is gone, which is what stole the next headless app's UI thread (#263).");
        Assert.DoesNotContain(logger.Snapshot(), e => e.Level == "ERROR");
    }

    [Fact]
    public async Task A_project_change_after_dispose_starts_no_rebuild()
    {
        var (project, path) = Project("after", "vocals");
        var audio = new FakeAudioPlaybackService();
        var ui = new CountingInlineDispatcher();
        var state = new ProjectState();

        var vm = new StemMixerViewModel(state, audio, FakeLocalization.KeysOnly(), ui: ui);
        await StemMixerSettle.Async();
        vm.Dispose();
        var dispatchesAtDispose = ui.Calls;

        state.CurrentProjectPath = path;
        state.ActiveProject = project;
        await StemMixerSettle.Async();

        Assert.Equal(dispatchesAtDispose, ui.Calls);
        Assert.Empty(audio.StemLoadCalls);
    }

    /// <summary>
    ///     The guard above holds only while EVERY dispatcher call of the type goes
    ///     through the released check. A bare <c>_ui.InvokeAsync</c> /
    ///     <c>_ui.PostNormal</c> added later would reopen the leak on that one path.
    /// </summary>
    [Fact]
    public void Every_dispatch_in_the_mixer_goes_through_the_release_gate()
    {
        var source = SourceText.StripComments(File.ReadAllText(RepoPath(ViewModel)));

        var bare = Regex.Matches(source, @"\b_ui\s*\.\s*\w+").Select(m => m.Value).ToList();
        Assert.True(bare is ["_ui.InvokeAsync", "_ui.InvokeAsync", "_ui.PostNormal"],
            $"{ViewModel}: the only _ui calls allowed are the three inside InvokeWhileAliveAsync (x2) and "
            + $"PostWhileAlive; found [{string.Join(", ", bare)}]. Route the new call through those helpers, or a "
            + "disposed mixer can dispatch again (#263).");
        foreach (var helper in new[] { "Task InvokeWhileAliveAsync(Action action)",
                     "Task<T> InvokeWhileAliveAsync<T>(Func<T> func)", "void PostWhileAlive(Action action)" })
        {
            var at = source.IndexOf(helper, StringComparison.Ordinal);
            Assert.True(at >= 0, $"{ViewModel}: helper '{helper}' is gone; repoint this lint.");
            var body = source[at..source.IndexOf("\n    }", at, StringComparison.Ordinal)];
            Assert.Contains("TryEnterDispatch()", body);
            Assert.Contains("ExitDispatch()", body);
        }
    }

    private (ChartProject Project, string ProjectPath) Project(string folder, params string[] stemNames)
    {
        var dir = Path.Combine(_root, folder);
        Directory.CreateDirectory(Path.Combine(dir, "stems"));
        var project = new ChartProject();
        foreach (var name in stemNames)
        {
            File.WriteAllBytes(Path.Combine(dir, "stems", name + ".wav"), [0]);
            project.Stems.Add(new Stem
            {
                Name = name,
                Path = Path.Combine("stems", name + ".wav"),
                Local = true,
                Volume = 1.0
            });
        }

        return (project, Path.Combine(dir, "song.rscp"));
    }

    private static string RepoPath(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "DotCharter.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.False(dir is null, "Could not locate repository root (DotCharter.sln).");
        return Path.Combine(dir!, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>Runs dispatched work inline under a gate (like
    /// <c>GatedInlineDispatcher</c>) and counts every call.</summary>
    private sealed class CountingInlineDispatcher : IUiDispatcher
    {
        private readonly Lock _gate = new();
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public bool CheckAccess() => true;
        public void Post(Action action) => Run(action);
        public void PostNormal(Action action) => Run(action);

        public Task InvokeAsync(Action action)
        {
            Run(action);
            return Task.CompletedTask;
        }

        public Task<T> InvokeAsync<T>(Func<T> func)
        {
            Interlocked.Increment(ref _calls);
            lock (_gate) return Task.FromResult(func());
        }

        public Task<T> InvokeAsync<T>(Func<Task<T>> func)
        {
            Interlocked.Increment(ref _calls);
            return func();
        }

        private void Run(Action action)
        {
            Interlocked.Increment(ref _calls);
            lock (_gate) action();
        }
    }
}
