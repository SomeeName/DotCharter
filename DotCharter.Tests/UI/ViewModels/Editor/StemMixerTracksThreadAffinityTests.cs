using System.Collections.Concurrent;
using System.Collections.Specialized;
using DotCharter.Tone.Diagnostics;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     <see cref="StemMixerViewModel.Tracks" /> is an
///     <see cref="System.Collections.ObjectModel.ObservableCollection{T}" />
///     bound to the mixer panel, so every mutation of it must happen on the UI
///     thread — the root guide's "never raise <c>PropertyChanged</c> for bound
///     state from a non-UI thread" rule, applied to a collection.
///     <para>
///         The rebuild awaits a worker probe and an engine load and then clears
///         and refills that collection. Until 2026-09-04 it resumed with
///         <c>ConfigureAwait(true)</c> and mutated inline, which is correct only
///         while a <c>SynchronizationContext</c> is installed to return to.
///         Avalonia installs one, so the app was fine; nothing else does, so
///         under test the whole apply ran on a POOL thread and raced any reader
///         — <c>StemMixerRebuildFailureTests.AFailedRebuildStillReleasesTheLoop</c>
///         failed with "Collection was modified" in about three runs out of four.
///         The view model is handed an <see cref="IUiDispatcher" /> for exactly
///         this; it now uses it, and this class is what would fail if the
///         implicit resumption came back.
///     </para>
/// </summary>
public sealed class StemMixerTracksThreadAffinityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "dotcharter-stemthread-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => BestEffortDelete.Directory(_root);

    [Fact]
    public async Task EveryTracksMutation_HappensOnTheDispatcherThread()
    {
        var (project, path) = Project("affinity", "gtr", "bass");
        var audio = new FakeAudioPlaybackService();
        var state = new ProjectState();
        using var ui = new PumpDispatcher();

        // The pump stands in for the UI thread, and it is deliberately NOT the
        // thread this test runs on — otherwise "mutated on the dispatcher
        // thread" would be true of an undispatched mutation too.
        Assert.NotEqual(Environment.CurrentManagedThreadId, ui.ThreadId);

        var mutations = new ConcurrentQueue<int>();
        var vm = await ui.InvokeAsync(() =>
        {
            var created = new StemMixerViewModel(state, audio, FakeLocalization.KeysOnly(), ui: ui);
            ((INotifyCollectionChanged)created.Tracks).CollectionChanged +=
                (_, _) => mutations.Enqueue(Environment.CurrentManagedThreadId);
            return created;
        });

        ui.PostNormal(() =>
        {
            state.CurrentProjectPath = path;
            state.ActiveProject = project;
        });

        // Condition-driven, not a budget: one Reset for the Clear, one Add per
        // resolved stem and one Insert for the master strip.
        await StemMixerSettle.Async(() => mutations.Count >= 4);

        var observed = mutations.ToArray();
        // Non-vacuous: a rebuild that produced no mutation at all would pass a
        // bare "all of them were on the right thread".
        Assert.True(observed.Length >= 4,
            $"expected at least 4 Tracks mutations, saw {observed.Length}");
        Assert.All(observed, id => Assert.Equal(ui.ThreadId, id));
        Assert.Equal(3, await ui.InvokeAsync(() => vm.Tracks.Count)); // master + 2 stems
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
    ///     A real single-threaded dispatcher: one owned thread draining a queue,
    ///     the way <c>Dispatcher.UIThread</c> behaves. An INLINE fake cannot pin
    ///     thread affinity — it runs the delegate on whichever thread dispatched
    ///     it, so an undispatched mutation looks identical to a dispatched one.
    /// </summary>
    private sealed class PumpDispatcher : IUiDispatcher, IDisposable
    {
        private readonly BlockingCollection<Action> _queue = [];
        private readonly Thread _pump;

        public PumpDispatcher()
        {
            _pump = new Thread(() =>
            {
                foreach (var work in _queue.GetConsumingEnumerable()) work();
            })
            { IsBackground = true, Name = "stem-mixer-test-ui" };
            _pump.Start();
        }

        public int ThreadId => _pump.ManagedThreadId;

        public bool CheckAccess() => Environment.CurrentManagedThreadId == ThreadId;

        public void Post(Action action) => PostNormal(action);

        public void PostNormal(Action action) => _queue.Add(action);

        public Task InvokeAsync(Action action) => InvokeAsync(() =>
        {
            action();
            return true;
        });

        public Task<T> InvokeAsync<T>(Func<T> func)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Add(() =>
            {
                try { tcs.SetResult(func()); }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            return tcs.Task;
        }

        public Task<T> InvokeAsync<T>(Func<Task<T>> func)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Add(async void () =>
            {
                try { tcs.SetResult(await func().ConfigureAwait(false)); }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            return tcs.Task;
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            _pump.Join(TimeSpan.FromSeconds(5));
            _queue.Dispose();
        }
    }
}
