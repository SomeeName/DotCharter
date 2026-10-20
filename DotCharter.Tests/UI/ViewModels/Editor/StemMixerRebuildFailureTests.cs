using DotCharter.Tone.Diagnostics;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     <see cref="StemMixerViewModel" />'s rebuild loop is started as a DISCARDED
///     task (<c>_ = RunRebuildLoopAsync()</c>) and its body was
///     <c>try</c>/<c>finally</c> with no <c>catch</c>, so anything escaping it
///     faulted a task nobody observes: no log, no status, and the mixer AND the
///     engine kept the previous project's stems.
///     <para>
///         <b>The ordinary failure never reached that gap</b> —
///         <c>RebuildOnceAsync</c> catches its own body and logs at Error, which
///         is what <see cref="AStemLoadFailureIsLoggedAndDegradesToNoStems" />
///         pins. What DID reach it is a throw from inside that catch HANDLER: it
///         sets <c>HasStems = false</c> outside any try, and that raises
///         <c>PropertyChanged</c> into arbitrary subscribers. One throwing
///         binding target turned a logged, degraded rebuild into a silent one.
///     </para>
/// </summary>
public sealed class StemMixerRebuildFailureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "dotcharter-stemfail-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => BestEffortDelete.Directory(_root);

    /// <summary>The inner guard, which already worked: a stem that will not load
    /// degrades to "no stems" and is logged, rather than propagating.</summary>
    [Fact]
    public async Task AStemLoadFailureIsLoggedAndDegradesToNoStems()
    {
        var (project, path) = Project("boom", "gtr");
        var audio = new FakeAudioPlaybackService
        {
            StemLoadFault = new IOException("the stem file went away")
        };
        var logger = new FakeAppLogger();
        var state = new ProjectState();

        var vm = new StemMixerViewModel(state, audio, FakeLocalization.KeysOnly(), logger, new InlineDispatcher());
        state.CurrentProjectPath = path;
        state.ActiveProject = project;

        // The rebuild logs from a pool thread: every read of the log goes through
        // its locked Snapshot (an enumerating Any() raced the Add and threw).
        await StemMixerSettle.Async(() => logger.Snapshot().Any(e => e.Level == "ERROR"));

        var error = Assert.Single(logger.Snapshot(), e => e.Level == "ERROR");
        Assert.Contains("Stem mixer rebuild failed", error.Message, StringComparison.Ordinal);
        Assert.IsType<IOException>(error.Exception);
        Assert.False(vm.HasStems);
    }

    /// <summary>
    /// THE outer gap. <c>RebuildOnceAsync</c>'s catch handler writes
    /// <c>HasStems = false</c> outside any try, so a throwing
    /// <c>PropertyChanged</c> subscriber escapes the whole rebuild. Without the
    /// loop's own catch that faults a discarded task: the degraded rebuild
    /// becomes completely silent, which is the one thing the inner handler was
    /// written to avoid.
    /// </summary>
    [Fact]
    public async Task AThrowFromTheFailureHandlerIsStillReported()
    {
        var (project, path) = Project("double", "gtr");
        var audio = new FakeAudioPlaybackService
        {
            StemLoadFault = new IOException("the stem file went away")
        };
        var logger = new FakeAppLogger();
        var state = new ProjectState();

        var vm = new StemMixerViewModel(state, audio, FakeLocalization.KeysOnly(), logger, new InlineDispatcher());
        vm.PropertyChanged += (_, e) =>
        {
            // Stands in for a binding target that throws — the notification the
            // catch handler raises is delivered to arbitrary subscribers.
            if (e.PropertyName == nameof(StemMixerViewModel.HasStems) && !vm.HasStems)
                throw new InvalidOperationException("a binding target blew up");
        };

        // The subscriber is on before the project arrives, so the one build we
        // observe is the one it can see.
        state.CurrentProjectPath = path;
        state.ActiveProject = project;
        await StemMixerSettle.Async(() => logger.Snapshot().Any(
            e => e.Level == "ERROR" && e.Exception is InvalidOperationException));

        Assert.Contains(logger.Snapshot(), e =>
            e.Level == "ERROR" && e.Exception is InvalidOperationException &&
            e.Message.Contains("previous project's stems", StringComparison.Ordinal));
    }

    /// <summary>
    /// …and the loop's in-flight latch is still released, so the NEXT project
    /// switch rebuilds instead of being coalesced into a run that already died.
    /// </summary>
    [Fact]
    public async Task AFailedRebuildStillReleasesTheLoop()
    {
        var (bad, badPath) = Project("bad", "gtr");
        var (good, goodPath) = Project("good", "bass");

        var audio = new FakeAudioPlaybackService
        {
            StemLoadFault = new IOException("the stem file went away")
        };
        var logger = new FakeAppLogger();
        var state = new ProjectState();

        var ui = new InlineDispatcher();
        var vm = new StemMixerViewModel(state, audio, FakeLocalization.KeysOnly(), logger, ui);
        state.CurrentProjectPath = badPath;
        state.ActiveProject = bad;
        await StemMixerSettle.Async(() => logger.Snapshot().Any(e => e.Level == "ERROR"));

        audio.StemLoadFault = null;
        state.CurrentProjectPath = goodPath;
        state.ActiveProject = good;

        // Snapshot Tracks THROUGH the dispatcher the view model mutates it
        // through. `vm.Tracks.Any(...)` straight from the polling thread raced
        // the rebuild's Clear/Add and threw "Collection was modified" in roughly
        // three runs out of four — the collection is bound to the UI, so the one
        // legal way to read it is on the thread that writes it.
        await StemMixerSettle.Async(() => ui.Snapshot(vm).Any(t => t.Name == "bass"));

        Assert.Contains(ui.Snapshot(vm), t => t.Name == "bass");
    }

    // ---- fixtures -----------------------------------------------------------

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

    /// <summary>
    ///     Runs dispatched work inline, but under a gate, so a test can read the
    ///     bound <c>Tracks</c> collection with the same exclusion the view model
    ///     writes it with. A plain inline dispatcher is not enough: the view
    ///     model's rebuild resumes on a POOL thread after its worker probe, so
    ///     "inline" means "on some other thread", and an unsynchronised
    ///     <c>Tracks.Any(...)</c> from the test thread enumerates mid-Clear.
    /// </summary>
    private sealed class InlineDispatcher : IUiDispatcher
    {
        private readonly Lock _gate = new();

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
            lock (_gate) return Task.FromResult(func());
        }

        public Task<T> InvokeAsync<T>(Func<Task<T>> func) => func();

        /// <summary>Reads the mixer's bound collection under the same gate.</summary>
        public IReadOnlyList<StemTrackViewModel> Snapshot(StemMixerViewModel vm)
        {
            lock (_gate) return vm.Tracks.ToArray();
        }

        private void Run(Action action)
        {
            lock (_gate) action();
        }
    }
}
