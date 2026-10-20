using DotCharter.Services.Parsing.Rscp;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     A project with stems must never OPEN silent.
///     <para>
///         Owner report, 2026-08-27: <i>"i started but have no audio wtf"</i>, with a
///         log showing a healthy ASIO engine, 11,254 completed callbacks and no
///         warning of any kind. Both charts had all six stems <c>muted="true"</c> in
///         the <c>.rscp</c>, which is the state the mixer's own cross-track exclusion
///         produces the moment you un-mute "Original" — it silences every stem so the
///         two mixes do not double.
///     </para>
///     <para>
///         Only the stem half of that pair was saved. The "Original" channel had no
///         backing <see cref="Stem" /> to persist to, so the mixer re-derived it on
///         every load as <c>_masterMixMuted = _stems.Count > 0</c> — "muted, because
///         stems exist" — and hard-coded its channel strip to <c>_isMuted = true</c>.
///         Saving "I want the original, not the stems" therefore reloaded as EVERY
///         channel muted: silence, played faithfully, with nothing to see anywhere.
///     </para>
/// </summary>
public sealed class StemMixerSilentProjectTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "dotcharter-silentmix-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => BestEffortDelete.Directory(_root);

    // ── the load-time guard ─────────────────────────────────────────────

    /// <summary>The owner's two charts, exactly as they sat on disk.</summary>
    [Fact]
    public async Task A_project_with_every_stem_muted_opens_with_the_original_audible()
    {
        var (project, path) = Project("silent", muteAll: true, "drums", "bass", "guitar");
        project.OriginalMixMuted = true; // what the old loader always derived

        var (vm, ui) = await MixerFor(project, path);

        var tracks = ui.Snapshot(vm);
        Assert.False(tracks.Single(t => t.IsMasterMix).IsMuted);
        Assert.False(ui.Read(() => project.OriginalMixMuted)); // written back, so it sticks
        Assert.Contains(tracks, t => !t.IsMuted);
    }

    /// <summary>
    ///     The guard is a floor, not a policy: a project that has something audible
    ///     opens exactly as it was saved, stems playing and the original silent.
    /// </summary>
    [Fact]
    public async Task A_project_with_an_audible_stem_is_left_exactly_as_saved()
    {
        var (project, path) = Project("normal", muteAll: false, "drums", "bass");
        project.OriginalMixMuted = true;

        var (vm, ui) = await MixerFor(project, path);

        var tracks = ui.Snapshot(vm);
        Assert.True(tracks.Single(t => t.IsMasterMix).IsMuted);
        Assert.True(ui.Read(() => project.OriginalMixMuted));
        Assert.All(tracks.Where(t => !t.IsMasterMix), t => Assert.False(t.IsMuted));
    }

    /// <summary>
    ///     The channel reads the PROJECT, not a constant. Pinned separately because
    ///     the load-time guard above rescues the all-muted case on its own, so
    ///     nothing else here would notice the strip going back to hard-coded muted —
    ///     and then a saved "original audible, stems audible" state (reachable by
    ///     un-muting a stem while the original plays, and by any project written
    ///     before the exclusion rules existed) would silently lose the original again.
    /// </summary>
    [Fact]
    public async Task The_original_channel_takes_its_mute_from_the_project()
    {
        var (project, path) = Project("fromproject", muteAll: false, "drums", "bass");
        project.OriginalMixMuted = false;

        var (vm, ui) = await MixerFor(project, path);

        Assert.False(ui.Snapshot(vm).Single(t => t.IsMasterMix).IsMuted);
    }

    /// <summary>
    ///     Load time only. Muting every channel while you work is a thing people do
    ///     — to chart against silence — and the mixer must not fight it.
    /// </summary>
    [Fact]
    public async Task Muting_every_channel_while_working_is_left_alone()
    {
        var (project, path) = Project("working", muteAll: false, "drums", "bass");
        var (vm, ui) = await MixerFor(project, path);

        foreach (var track in ui.Snapshot(vm)) track.IsMuted = true;

        Assert.All(ui.Snapshot(vm), t => Assert.True(t.IsMuted));
    }

    // ── the half that was never saved ───────────────────────────────────

    [Fact]
    public async Task Un_muting_the_original_writes_both_halves_to_the_project()
    {
        var (project, path) = Project("both", muteAll: false, "drums", "bass");
        var (vm, ui) = await MixerFor(project, path);

        ui.Snapshot(vm).Single(t => t.IsMasterMix).IsMuted = false;

        // The stems record their half — they always did. The original now records
        // its own, which is the whole bug: half a mutually exclusive pair is not
        // recoverable state.
        Assert.All(project.Stems, s => Assert.True(s.Muted));
        Assert.False(project.OriginalMixMuted);
    }

    /// <summary>
    ///     A gameplay run mutes the backing track without touching the project — the
    ///     same rule <c>Stem.Muted</c> already had, and for the same reason: a crash
    ///     mid-run must not leave the original permanently off.
    /// </summary>
    [Fact]
    public async Task A_run_scoped_mute_of_the_original_does_not_reach_the_project()
    {
        var (project, path) = Project("run", muteAll: true, "drums");
        var (vm, ui) = await MixerFor(project, path);

        var master = ui.Snapshot(vm).Single(t => t.IsMasterMix);
        Assert.False(master.IsMuted); // the guard opened it
        Assert.False(ui.Read(() => project.OriginalMixMuted));

        master.SetMutedForRun(true);

        Assert.True(master.IsMuted);
        Assert.False(ui.Read(() => project.OriginalMixMuted));
    }

    // ── the project file ────────────────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_original_mix_mute_survives_the_project_file(bool muted)
    {
        var project = new ChartProject { OriginalMixMuted = muted };
        project.Stems.Add(new Stem { Name = "drums", Path = "stems/drums.wav", Muted = true });

        var path = Path.Combine(Root(), "roundtrip.rscp");
        var serializer = new RscpChartSerializer(new FakeAppLogger());
        serializer.Save(path, project);

        Assert.Equal(muted, serializer.Load(path).OriginalMixMuted);
    }

    /// <summary>
    ///     Files written before the attribute existed carry only the stem half, and
    ///     re-deriving it the old way is the bug. "Muted iff any stem is audible"
    ///     reads them the way they were meant — which is what rescues the owner's
    ///     two charts without a re-save.
    /// </summary>
    [Theory]
    [InlineData(true, false)]  // every stem muted  ⇒ they wanted the original
    [InlineData(false, true)]  // a stem is audible ⇒ they wanted the stems
    public void A_project_file_without_the_attribute_derives_it_from_the_stems(
        bool stemsMuted, bool expectedOriginalMuted)
    {
        var project = new ChartProject();
        project.Stems.Add(new Stem { Name = "drums", Path = "stems/drums.wav", Muted = stemsMuted });
        project.Stems.Add(new Stem { Name = "bass", Path = "stems/bass.wav", Muted = stemsMuted });

        var path = Path.Combine(Root(), "legacy.rscp");
        var serializer = new RscpChartSerializer(new FakeAppLogger());
        serializer.Save(path, project);

        // Strip the attribute back out — this is byte-for-byte what an older
        // DotCharter wrote.
        var text = File.ReadAllText(path);
        text = text.Replace(" originalMuted=\"true\"", "").Replace(" originalMuted=\"false\"", "");
        Assert.DoesNotContain("originalMuted", text);
        File.WriteAllText(path, text);

        Assert.Equal(expectedOriginalMuted, serializer.Load(path).OriginalMixMuted);
    }

    // ---- helpers ------------------------------------------------------------

    /// <summary>
    ///     Builds a mixer over the project and waits for its rebuild — through a
    ///     GATED dispatcher, and settling on the STRIPS rather than on a count.
    ///     <para>
    ///         Both halves were wrong, and the class flaked twice on 2026-09-04.
    ///         <c>RebuildOnceAsync</c> awaits a worker probe with
    ///         <c>ConfigureAwait(false)</c>, so every dispatched step after it runs
    ///         on a POOL thread — a plain inline dispatcher is inline but not on the
    ///         test's thread, and a raw <c>vm.Tracks.Single(…)</c> then enumerates a
    ///         collection another thread is rebuilding. And <c>BuildStrips</c> adds
    ///         the stems and only THEN inserts the master "Original" strip at index
    ///         0, so <c>Tracks.Count &gt; 1</c> goes true one line before the master
    ///         exists: for a two-stem project the very next statement,
    ///         <c>Single(t =&gt; t.IsMasterMix)</c>, threw "sequence contains no
    ///         matching element" — a failure with no relation to what the case tests.
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

    private string Root()
    {
        Directory.CreateDirectory(_root);
        return _root;
    }

    private (ChartProject Project, string ProjectPath) Project(
        string folder, bool muteAll, params string[] stemNames)
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
                Muted = muteAll
            });
        }

        return (project, Path.Combine(dir, "song.rscp"));
    }
}
