using System.Collections.Concurrent;
using DotCharter.Tone.Diagnostics;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     <see cref="StemMixerViewModel.Rebuild" /> fires from the constructor and
///     from every <c>ActiveProject</c>/<c>CurrentProjectPath</c> change, always
///     on the UI thread. Two defects lived in it: the whole probe (a
///     <c>Directory.Exists</c>, a <c>Directory.EnumerateFiles</c> and one
///     <c>File.Exists</c> per stem) ran inline before the first await, and a
///     rebuild requested while one was in flight was DISCARDED rather than
///     coalesced.
/// </summary>
public sealed class StemMixerRebuildTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "dotcharter-stemmixer-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => BestEffortDelete.Directory(_root);

    [Fact]
    public async Task The_same_stem_set_is_not_pushed_into_the_engine_twice_for_one_load()
    {
        // Project loading raises ActiveProject and then CurrentProjectPath; both
        // post a rebuild. The second used to reload the identical set into the
        // engine — 37 s of a 75 s project open on 2026-08-29.
        var (project, path) = Project("a", "bass", "drums");
        var audio = new FakeAudioPlaybackService();
        var state = new ProjectState();

        var ui = new GatedInlineDispatcher();
        var vm = new StemMixerViewModel(state, audio, FakeLocalization.KeysOnly(), ui: ui);
        state.CurrentProjectPath = path;
        state.ActiveProject = project;
        await StemMixerSettle.Async(() => audio.StemLoadCalls.Count >= 1);
        Assert.Single(audio.StemLoadCalls);

        // The second event of the same load, and a gratuitous third rebuild.
        state.CurrentProjectPath = path;
        vm.Rebuild();
        await StemMixerSettle.Async();
        Assert.Single(audio.StemLoadCalls);
        Assert.Contains(ui.Snapshot(vm), t => t.Name == "bass");

        // A genuinely different set still loads.
        var (projectC, pathC) = Project("a", "bass", "drums", "vocals");
        state.CurrentProjectPath = pathC;
        state.ActiveProject = projectC;
        await StemMixerSettle.Async(() => audio.StemLoadCalls.Count >= 2);
        Assert.Equal(2, audio.StemLoadCalls.Count);
        Assert.Equal(["bass", "drums", "vocals"], audio.StemLoadCalls[1]);
    }

    [Fact]
    public async Task A_new_audio_file_name_reloads_the_stems_the_project_now_lists()
    {
        // Add silence (#313) swaps the song and its stems in one step without a new
        // project or path: the audio file name is the only state that changes.
        var (project, path) = Project("splice", "drums");
        var audio = new FakeAudioPlaybackService();
        var state = new ProjectState { CurrentProjectPath = path, ActiveProject = project };
        var ui = new GatedInlineDispatcher();
        _ = new StemMixerViewModel(state, audio, FakeLocalization.KeysOnly(), ui: ui);
        await StemMixerSettle.Async(() => audio.StemLoadCalls.Count >= 1);

        var padded = Path.Combine("stems", "silence-1", "drums.wav");
        Directory.CreateDirectory(Path.Combine(_root, "splice", "stems", "silence-1"));
        File.WriteAllBytes(Path.Combine(_root, "splice", padded), [0]);
        project.Stems[0].Path = padded;
        state.CurrentAudioFileName = "song-silence1.wav";
        await StemMixerSettle.Async(() => audio.StemLoadCalls.Count >= 2);

        Assert.Equal(2, audio.StemLoadCalls.Count);
    }

    [Fact]
    public async Task A_project_swap_during_a_stem_load_is_coalesced_not_dropped()
    {
        // The failure: load project A (long stem load starts) -> open project B
        // -> B's ActiveProject change posts a rebuild that is thrown away -> the
        // mixer keeps showing A's stems and the ENGINE keeps A's stems loaded,
        // until some later unrelated ProjectState change happens to fire.
        var (projectA, pathA) = Project("a", "bass", "drums");
        var (projectB, pathB) = Project("b", "vocals");

        var audio = new FakeAudioPlaybackService();
        var state = new ProjectState();
        var gate = new TaskCompletionSource();
        audio.StemLoadGate = gate;

        state.CurrentProjectPath = pathA;
        state.ActiveProject = projectA;

        var ui = new GatedInlineDispatcher();
        var vm = new StemMixerViewModel(state, audio, FakeLocalization.KeysOnly(), ui: ui);
        await StemMixerSettle.Async(() => audio.StemLoadCalls.Count >= 1);

        Assert.Single(audio.StemLoadCalls);
        Assert.Equal(["bass", "drums"], audio.StemLoadCalls[0]);

        // Swap projects while A's load is still parked on the gate.
        state.CurrentProjectPath = pathB;
        state.ActiveProject = projectB;
        await StemMixerSettle.Async();

        // A is still loading, so B must be queued rather than lost.
        Assert.Single(audio.StemLoadCalls);

        audio.StemLoadGate = null;
        gate.SetResult();
        await StemMixerSettle.Async(() => audio.StemLoadCalls.Count >= 2);

        Assert.Equal(2, audio.StemLoadCalls.Count);
        Assert.Equal(["vocals"], audio.StemLoadCalls[1]);
        // The rebuild that loaded B applies on a pool thread after the engine
        // call: settle on the strip this reads, through the gate (#127).
        await StemMixerSettle.Async(() => ui.Snapshot(vm).Any(t => t.Name == "vocals"));
        var tracks = ui.Snapshot(vm);
        Assert.Contains(tracks, t => t.Name == "vocals");
        Assert.DoesNotContain(tracks, t => t.Name == "bass");
    }

    [Fact]
    public async Task Rebuild_does_no_filesystem_work_on_the_calling_thread()
    {
        // Directory.Exists, Directory.EnumerateFiles and one File.Exists per
        // stem used to run INLINE before the first await, so a project on a
        // network share, a cloud placeholder folder that has to hydrate, or a
        // cold spinning disk froze the whole window — on every project load AND
        // every project-path change.
        //
        // A dispatcher that QUEUES rather than runs makes that observable: with
        // the probe on a worker and the apply marshalled through the dispatcher,
        // nothing past the first await can have run when the constructor
        // returns. Inline probing reaches the engine call before ever yielding.
        //
        // This used to be staged on a pumped SynchronizationContext, which
        // worked only while the view model resumed with ConfigureAwait(true) and
        // therefore depended on an ambient context nothing installs outside
        // Avalonia — the fragility that raced StemMixerRebuildFailureTests. The
        // injected IUiDispatcher is the explicit version of the same staging.
        var (project, path) = Project("probe", "gtr");
        var audio = new FakeAudioPlaybackService();
        var state = new ProjectState { CurrentProjectPath = path, ActiveProject = project };

        var ui = new DeferredDispatcher();
        var vm = new StemMixerViewModel(state, audio, FakeLocalization.KeysOnly(), ui: ui);

        Assert.Empty(audio.StemLoadCalls);
        Assert.Empty(vm.Tracks);

        await ui.DrainAsync(() => audio.StemLoadCalls.Count >= 1);

        Assert.Single(audio.StemLoadCalls);
        Assert.Contains(vm.Tracks, t => t.Name == "gtr");
    }

    [Fact]
    public async Task An_unreferenced_stems_folder_is_still_adopted()
    {
        // The recovery path (projects from older builds, or separation runs that
        // finished before the project was auto-saved) has to survive moving the
        // probe off-thread.
        var dir = Path.Combine(_root, "recovered");
        Directory.CreateDirectory(Path.Combine(dir, "stems"));
        File.WriteAllBytes(Path.Combine(dir, "stems", "other.wav"), [0]);
        File.WriteAllBytes(Path.Combine(dir, "stems", "guitar.wav"), [0]);

        var project = new ChartProject();
        var audio = new FakeAudioPlaybackService();
        var state = new ProjectState
        {
            CurrentProjectPath = Path.Combine(dir, "song.rscp"),
            ActiveProject = project
        };

        var ui = new GatedInlineDispatcher();
        var vm = new StemMixerViewModel(state, audio, FakeLocalization.KeysOnly(), ui: ui);
        // Settle on every strip read below, the master included: it is inserted
        // AFTER the stems, so HasStems alone goes true one step early (#127).
        await StemMixerSettle.Async(() => ui.Snapshot(vm) is [{ IsMasterMix: true }, ..] tracks &&
                                tracks.Any(t => t.Name == "guitar") && tracks.Any(t => t.Name == "other"));

        Assert.True(ui.Read(() => vm.HasStems));
        Assert.Equal(2, ui.Read(() => project.Stems.Count));
        var settled = ui.Snapshot(vm);
        Assert.Contains(settled, t => t.Name == "guitar");
        Assert.Contains(settled, t => t.Name == "other");
        // The master "Original" strip always sits at the front.
        Assert.True(settled[0].IsMasterMix);
    }

    [Fact]
    public async Task A_stem_whose_file_is_missing_is_skipped()
    {
        var (project, path) = Project("missing", "present");
        project.Stems.Add(new Stem { Name = "ghost", Path = Path.Combine("stems", "ghost.wav"), Local = true });

        var audio = new FakeAudioPlaybackService();
        var state = new ProjectState { CurrentProjectPath = path, ActiveProject = project };

        var ui = new GatedInlineDispatcher();
        var vm = new StemMixerViewModel(state, audio, FakeLocalization.KeysOnly(), ui: ui);
        // Read through the gate: the apply runs on a pool thread, and enumerating
        // Tracks while it refills threw "Collection was modified" in the gate (#127).
        await StemMixerSettle.Async(() => ui.Snapshot(vm).Any(t => t.Name == "present"));

        var tracks = ui.Snapshot(vm);
        Assert.DoesNotContain(tracks, t => t.Name == "ghost");
        Assert.Contains(tracks, t => t.Name == "present");
    }

    [Fact]
    public async Task Clearing_the_active_project_empties_the_mixer()
    {
        var (project, path) = Project("cleared", "bass");
        var audio = new FakeAudioPlaybackService();
        var state = new ProjectState { CurrentProjectPath = path, ActiveProject = project };

        var ui = new GatedInlineDispatcher();
        var vm = new StemMixerViewModel(state, audio, FakeLocalization.KeysOnly(), ui: ui);
        await StemMixerSettle.Async(() => ui.Read(() => vm.HasStems));
        Assert.True(ui.Read(() => vm.HasStems));

        state.ActiveProject = null;
        await StemMixerSettle.Async(() => ui.Read(() => !vm.HasStems) && ui.Snapshot(vm).Count == 0);

        Assert.False(ui.Read(() => vm.HasStems));
        Assert.Empty(ui.Snapshot(vm));
    }

    // ---- fixtures -----------------------------------------------------------

    /// <summary>Creates a project folder with real stem WAV files on disk and a
    /// ChartProject that references them relatively.</summary>
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
    ///     Queues every dispatched action instead of running it, so a test can
    ///     assert exactly what has and has not run at a given point. Stands in
    ///     for the Avalonia dispatcher, which behaves the same way.
    /// </summary>
    private sealed class DeferredDispatcher : IUiDispatcher
    {
        private readonly ConcurrentQueue<Action> _queue = new();

        public bool CheckAccess() => false;

        public void Post(Action action) => _queue.Enqueue(action);

        public void PostNormal(Action action) => _queue.Enqueue(action);

        public Task InvokeAsync(Action action) => InvokeAsync(() =>
        {
            action();
            return true;
        });

        public Task<T> InvokeAsync<T>(Func<T> func)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Enqueue(() =>
            {
                try { tcs.SetResult(func()); }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            return tcs.Task;
        }

        public Task<T> InvokeAsync<T>(Func<Task<T>> func) => func();

        /// <summary>Runs queued work until <paramref name="until" /> holds —
        /// a CONDITION, not a budget (see <see cref="StemMixerSettle" />).</summary>
        public async Task DrainAsync(Func<bool> until)
        {
            while (true)
            {
                while (_queue.TryDequeue(out var work)) work();
                if (until()) return;
                await Task.Delay(5, TestContext.Current.CancellationToken).ConfigureAwait(false);
            }
        }
    }

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;
        public void Post(Action action) => action();
        public void PostNormal(Action action) => action();
        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }

        public Task<T> InvokeAsync<T>(Func<T> func) => Task.FromResult(func());
        public Task<T> InvokeAsync<T>(Func<Task<T>> func) => func();
    }
}
