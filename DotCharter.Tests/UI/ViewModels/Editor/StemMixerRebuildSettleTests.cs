using System.Collections.Specialized;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     Why a mixer test may not settle on <c>Tracks.Count</c>, pinned rather than
///     left as a comment — two classes flaked on exactly this and neither failure
///     mentioned the mixer.
///     <para>
///         <c>BuildStrips</c> clears the collection, adds one strip per resolved
///         stem, and only THEN inserts the master "Original" strip at index 0. So
///         for a project with two or more stems there is a real window in which
///         <c>Tracks.Count &gt; 1</c> is true and no master strip exists — and
///         <c>StemMixerSoloReleaseTests</c> / <c>StemMixerSilentProjectTests</c>
///         both settled on that count and then immediately called
///         <c>Single(t =&gt; t.IsMasterMix)</c>, which throws "sequence contains no
///         matching element". Under load the poll landed in the window; on an idle
///         box it did not, which is what made it read as an unrelated flake.
///     </para>
///     <para>
///         Observed from a <c>CollectionChanged</c> handler, so it runs on the
///         thread doing the mutating and needs no timing at all. If the insert order
///         ever changes this goes red — and at that point the right move is to
///         delete this class, not to put a count-based settle back: the rule the
///         settles now follow ("wait for the strip you are about to read") is
///         correct either way.
///     </para>
/// </summary>
public sealed class StemMixerRebuildSettleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "dotcharter-settle-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => BestEffortDelete.Directory(_root);

    [Fact]
    public async Task Tracks_passes_through_a_state_with_several_strips_and_no_master()
    {
        var dir = Path.Combine(_root, "window");
        Directory.CreateDirectory(Path.Combine(dir, "stems"));

        var project = new ChartProject();
        foreach (var name in new[] { "drums", "bass" })
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

        // The constructor starts a rebuild on its own when the state already holds a
        // project, and the stem probe runs on the thread pool: under load the whole
        // build can finish before a handler attached AFTER construction ever sees it.
        // So the vm is built over an empty state, the handler goes on first, and only
        // then does the project arrive and start the one build we observe.
        var ui = new GatedInlineDispatcher();
        var state = new ProjectState();
        var vm = new StemMixerViewModel(state, new FakeAudioPlaybackService(), FakeLocalization.KeysOnly(), ui: ui);

        var sawCountWithoutMaster = false;
        vm.Tracks.CollectionChanged += OnChanged;

        state.CurrentProjectPath = Path.Combine(dir, "song.rscp");
        state.ActiveProject = project;

        await StemMixerSettle.Async(() =>
        {
            var tracks = ui.Snapshot(vm);
            return tracks.Any(t => t.IsMasterMix) && tracks.Count(t => !t.IsMasterMix) == 2;
        });

        vm.Tracks.CollectionChanged -= OnChanged;

        Assert.True(sawCountWithoutMaster,
            "BuildStrips no longer passes through 'several strips, no master'. If the insert order " +
            "changed deliberately, delete this class — do NOT reintroduce a count-based settle.");

        // …and the finished collection does of course have one.
        Assert.Single(ui.Snapshot(vm), t => t.IsMasterMix);
        return;

        void OnChanged(object? _, NotifyCollectionChangedEventArgs __)
        {
            // On the mutating thread, inside the dispatched build — no timing.
            if (vm.Tracks.Count > 1 && vm.Tracks.All(t => !t.IsMasterMix))
                sawCountWithoutMaster = true;
        }
    }

}
