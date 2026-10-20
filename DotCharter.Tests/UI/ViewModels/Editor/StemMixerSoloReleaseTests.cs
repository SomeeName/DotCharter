using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     Solo is the mixer's most-clicked button, and it has to be a TOGGLE both ways.
///     <para>
///         The mixer silences the master "Original" strip while a stem is soloed, which
///         is right — but it did that through
///         <c>StemTrackViewModel.SetMutedSilently</c>, which also calls
///         <c>PersistOriginalMixMuted</c>, and it did nothing at all on release
///         (<c>if (!nowSoloed) return;</c>). So one click on Solo turned the original
///         mix off PERMANENTLY and wrote it into the project, where it survived a
///         reload. The engine's own solo handling is transient by design
///         (<c>JuceAudioPlaybackService.ApplyGain</c>: "the toggle is restored as soon
///         as the solo is released") — the UI was the half that made it stick.
///     </para>
/// </summary>
public sealed class StemMixerSoloReleaseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "dotcharter-solorelease-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => BestEffortDelete.Directory(_root);

    /// <summary>
    ///     The user is listening to the original mix. They solo a stem to check one
    ///     part, then release it — and the original has to come back.
    /// </summary>
    [Fact]
    public async Task Releasing_the_last_solo_restores_the_originals_own_mute()
    {
        var (project, path) = Project("release", stemsMuted: true, "drums", "bass");
        project.OriginalMixMuted = false; // the user wants the original audible
        var (vm, ui) = await MixerFor(project, path);

        var tracks = ui.Snapshot(vm);
        var master = tracks.Single(t => t.IsMasterMix);
        var drums = tracks.Single(t => t.Name == "drums");
        Assert.False(master.IsMuted);

        drums.IsSolo = true;
        Assert.True(master.IsMuted); // while soloed the original must be out of the way

        drums.IsSolo = false;
        Assert.False(master.IsMuted); // …and back when the solo is released
    }

    /// <summary>
    ///     A solo is transient state, so — exactly like a run-scoped mute — it must not
    ///     reach the <c>.rscp</c>. This is the half that made the bug survive a reload.
    /// </summary>
    [Fact]
    public async Task A_solo_never_writes_the_original_mix_mute_into_the_project()
    {
        var (project, path) = Project("persist", stemsMuted: true, "drums");
        project.OriginalMixMuted = false;
        var (vm, ui) = await MixerFor(project, path);

        var drums = ui.Snapshot(vm).Single(t => t.Name == "drums");
        drums.IsSolo = true;

        Assert.False(ui.Read(() => project.OriginalMixMuted));

        drums.IsSolo = false;
        Assert.False(ui.Read(() => project.OriginalMixMuted));
    }

    /// <summary>
    ///     Two solos, one released: the original stays silent until NO stem is soloed.
    ///     Restoring on the first release would un-silence the original over the stem
    ///     the user is still soloing.
    /// </summary>
    [Fact]
    public async Task Releasing_one_of_two_solos_keeps_the_original_silent()
    {
        var (project, path) = Project("two", stemsMuted: true, "drums", "bass");
        project.OriginalMixMuted = false;
        var (vm, ui) = await MixerFor(project, path);

        var tracks = ui.Snapshot(vm);
        var master = tracks.Single(t => t.IsMasterMix);
        var drums = tracks.Single(t => t.Name == "drums");
        var bass = tracks.Single(t => t.Name == "bass");

        drums.IsSolo = true;
        bass.IsSolo = true;
        drums.IsSolo = false;

        Assert.True(master.IsMuted);

        bass.IsSolo = false;
        Assert.False(master.IsMuted);
    }

    /// <summary>
    ///     A user who had the original OFF keeps it off across a solo — the baseline is
    ///     whatever they had, not a constant.
    /// </summary>
    [Fact]
    public async Task A_solo_over_an_already_muted_original_leaves_it_muted_on_release()
    {
        var (project, path) = Project("wasmuted", stemsMuted: false, "drums");
        project.OriginalMixMuted = true;
        var (vm, ui) = await MixerFor(project, path);

        var tracks = ui.Snapshot(vm);
        var master = tracks.Single(t => t.IsMasterMix);
        var drums = tracks.Single(t => t.Name == "drums");
        Assert.True(master.IsMuted);

        drums.IsSolo = true;
        drums.IsSolo = false;

        Assert.True(master.IsMuted);
        Assert.True(ui.Read(() => project.OriginalMixMuted));
    }

    // ---- helpers ------------------------------------------------------------

    /// <summary>
    ///     Builds a mixer over the project and waits for its rebuild — through a
    ///     GATED dispatcher, and settling on the STRIPS rather than on a count.
    ///     <para>
    ///         Both halves were wrong, and this class flaked twice on 2026-09-04.
    ///         <c>RebuildOnceAsync</c> awaits a worker probe with
    ///         <c>ConfigureAwait(false)</c>, so every dispatched step after it runs
    ///         on a POOL thread — a plain inline dispatcher is inline but not on the
    ///         test's thread, and a raw <c>vm.Tracks.Single(…)</c> then enumerates a
    ///         collection another thread is rebuilding. And <c>BuildStrips</c> adds
    ///         the stems and only THEN inserts the master "Original" strip at index
    ///         0, so <c>Tracks.Count &gt; 1</c> goes true one line before the master
    ///         exists: for the two-stem cases here the very next statement,
    ///         <c>Single(t =&gt; t.IsMasterMix)</c>, threw "sequence contains no
    ///         matching element" — a failure with no relation to solo at all. Same
    ///         remedy as <c>StemMixerRebuildFailureTests</c>'.
    ///     </para>
    /// </summary>
    private static async Task<(StemMixerViewModel Vm, GatedInlineDispatcher Ui)> MixerFor(
        ChartProject project, string path)
    {
        var expectedStems = project.Stems.Count;
        var ui = new GatedInlineDispatcher();
        var state = new ProjectState { CurrentProjectPath = path, ActiveProject = project };
        var vm = new StemMixerViewModel(state, new FakeAudioPlaybackService(), FakeLocalization.KeysOnly(), ui: ui);

        await StemMixerSettle.Async(() =>
        {
            var tracks = ui.Snapshot(vm);
            // …AND the whole rebuild is over: FinishRebuild re-derives the
            // master's solo-scoped mute after the strips appear, and a solo
            // pressed before it runs is recorded against the wrong baseline.
            return tracks.Any(t => t.IsMasterMix) && tracks.Count(t => !t.IsMasterMix) == expectedStems
                   && !ui.Read(() => vm.IsRebuilding);
        });

        return (vm, ui);
    }

    // stemsMuted true is "the user is listening to the ORIGINAL mix" — the state the
    // mixer's own cross-track exclusion produces. False is the ordinary stems-playing
    // state, and it also keeps the load-time "never open silent" guard from firing when
    // the original is muted too.
    private (ChartProject Project, string ProjectPath) Project(string folder, bool stemsMuted,
        params string[] stemNames)
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
                Volume = 1.0,
                Muted = stemsMuted
            });
        }

        return (project, Path.Combine(dir, "song.rscp"));
    }
}
