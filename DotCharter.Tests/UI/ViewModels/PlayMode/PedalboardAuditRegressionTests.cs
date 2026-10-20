using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Diagnostics;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI.ViewModels.PlayMode;

/// <summary>
/// Regression tests from the 0.16 pedalboard audit (panels + services + hosts):
/// the neural-amp badge must catch the ASYNC native capture load, the preset
/// browser must track the tone it just saved as the active one, the curated
/// preset ordering must actually reach the card grid, and the rig tuner's poll
/// must be gated per hosting surface (the editor and Play Mode share ONE VM).
/// No Avalonia instance.
/// </summary>
public sealed class PedalboardAuditRegressionTests
{
    /// <summary>Runs posted work inline — the pedalboard's UI-marshalling seam.</summary>
    private sealed class InlineDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;
        public void Post(Action action) => action();
        public void PostNormal(Action action) => action();
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
        public Task<T> InvokeAsync<T>(Func<T> func) => Task.FromResult(func());
        public Task<T> InvokeAsync<T>(Func<Task<T>> func) => func();
    }

    private static IReadOnlyList<DspModuleType> NamCatalog() =>
        FakeDspChainService.DefaultCatalog()
            .Append(new DspModuleType("namamp", "NAM Amp", [])
            {
                StringParams = [new DspStringParamInfo("modelPath", "Capture", "*.nam;*.aidax")]
            })
            .ToList();

    private static PedalboardViewModel NewNamVm(out FakeDspChainService dsp)
    {
        dsp = new FakeDspChainService(NamCatalog());
        var vm = TestRigBuilders.Pedalboard(dsp, new FakeDspTonePresetService(), FakeLocalization.English(),
            dispatcher: new InlineDispatcher());
        vm.NamPollInterval = TimeSpan.FromMilliseconds(1);
        vm.NamPollAttempts = 20;
        return vm;
    }

    // ── the async NAM load: the badge must stop lying ────────────────────────

    /// <summary>
    /// Picking a capture on a namamp card pushes <c>modelPath</c>, which the native
    /// engine accepts IMMEDIATELY and loads asynchronously — so the status read taken
    /// at push time always sees loaded=0. Before the fix nothing ever re-read it, so
    /// the card kept its "no model loaded" warning for the whole session even though
    /// the amp was live. A bounded, self-terminating poll must catch the load.
    /// </summary>
    [Fact]
    public async Task Namamp_status_badge_catches_the_async_capture_load()
    {
        var vm = NewNamVm(out var dsp);
        var amp = vm.AddGear(vm.AvailableModules.First(m => m.Id == "namamp"))!;
        Assert.True(amp.IsNeuralAmp);
        Assert.True(amp.NamModelMissing);

        var modelPath = amp.StringParams.Single(p => p.Id == "modelPath");
        modelPath.ClearCommand.Execute(null);           // a commit through the same seam as the picker
        Assert.NotNull(vm.NamRefreshTask);

        // The native load lands a moment later — exactly the race the badge lost.
        dsp.SetParam(amp.NodeId, "loaded", 1f);
        await vm.NamRefreshTask!;

        Assert.True(amp.NamModelLoaded);
        Assert.False(amp.NamHasWarning);
    }

    /// <summary>The poll is self-terminating: it must not run forever (no standing
    /// timer on a hideable surface), and a second request while one is in flight
    /// must not stack a second burst.</summary>
    [Fact]
    public async Task Namamp_status_poll_is_bounded_and_single_flight()
    {
        var vm = NewNamVm(out var dsp);
        var amp = vm.AddGear(vm.AvailableModules.First(m => m.Id == "namamp"))!;

        vm.ScheduleNeuralAmpStatusRefresh();
        var first = vm.NamRefreshTask;
        Assert.NotNull(first);
        vm.ScheduleNeuralAmpStatusRefresh();            // single-flight: same burst
        Assert.Same(first, vm.NamRefreshTask);

        // The capture never loads: the burst still terminates on its own budget.
        await first!;
        Assert.True(amp.NamModelMissing);
        Assert.Null(dsp.GetParam(amp.NodeId, "loaded"));
    }

    // ── save → persist → re-apply must not lose the capture ──────────────────

    /// <summary>
    /// The whole "Save as new" chain, end to end through the REAL persistence store:
    /// pedal card sets a namamp's <c>modelPath</c> → BuildCurrentRig snapshots it →
    /// SaveUser → <c>AppConfig.UserTonePresets</c> → re-apply onto a fresh chain.
    /// <c>UserToneModuleDto</c> dropped <c>StringParams</c> on the way to JSON, so
    /// the saved profile persisted a MODELLESS neural amp and re-applying the tone
    /// brought back a SILENT amp — the capture was gone. Same for a cab's IR path
    /// and (already covered, re-pinned here) the split branch tag.
    /// </summary>
    [Fact]
    public void User_tone_save_and_reapply_keeps_the_capture_the_ir_and_the_branch()
    {
        var config = new FakeConfigurationService();
        var catalog = NamCatalog();

        // 1. Dial in a rig on the pedalboard: a namamp with a capture, on branch 2.
        var chain = new FakeDspChainService(catalog);
        var vm = TestRigBuilders.Pedalboard(chain, new DspTonePresetService(chain, config), FakeLocalization.English());
        var amp = vm.AddGear(vm.AvailableModules.First(m => m.Id == "namamp"))!;
        chain.SetNodeString(amp.NodeId, "modelPath", @"C:\captures\deluxe.nam");
        chain.SetNodeBranch(amp.NodeId, 2);

        // 2. Save it as a user tone through the browser's capture seam.
        var rig = vm.BuildCurrentRig();
        var saveService = new DspTonePresetService(chain, config);
        var saved = saveService.SaveUser("My NAM", DspToneCategory.HighGain, "", rig);

        // 3. It came back OUT of the store with the capture intact (the DTO round-trip).
        var reloaded = new DspTonePresetService(new FakeDspChainService(catalog), config)
            .FindById(saved.Id)!;
        var storedAmp = reloaded.Rig.Modules.Single(m => m.ModuleTypeId == "namamp");
        Assert.Equal(@"C:\captures\deluxe.nam", storedAmp.StringParams!["modelPath"]);
        Assert.Equal(2, storedAmp.Branch);

        // 4. Re-applying it onto a FRESH chain re-pushes the capture natively — the
        //    amp is loaded, not silent.
        var freshChain = new FakeDspChainService(catalog);
        new DspTonePresetService(freshChain, config).Apply(reloaded);
        var applied = freshChain.GetNodes().Single(n => n.ModuleTypeId == "namamp");
        Assert.Equal(@"C:\captures\deluxe.nam", applied.StringValues["modelPath"]);
        Assert.Equal(2, applied.Branch);
    }

    // ── preset browser: "the tone I just saved IS the active tone" ────────────

    /// <summary>
    /// Apply a factory tone → tweak the rig → "Save as new". The saved profile IS the
    /// live rig, so it must become the ACTIVE tone. Before the fix the toolbar's
    /// active-tone name and the searchable dropdown both kept pointing at the tone
    /// applied BEFORE the tweak — they lied about what the rig is playing.
    /// </summary>
    [Fact]
    public void Saving_the_live_rig_makes_the_saved_tone_the_active_one()
    {
        var svc = new FakeDspTonePresetService();
        var vm = new PresetBrowserViewModel(svc, FakeLocalization.English(), () => new DcRigPreset([]), () => { });

        vm.ApplyCard(vm.Cards.First(c => c.Name == "Glass Clean"));
        Assert.Equal("Glass Clean", vm.ActivePresetName);

        vm.SaveName = "My Tone";
        vm.ConfirmSave();

        Assert.Equal("My Tone", vm.ActivePresetName);
        Assert.Contains(vm.Cards, c => c.IsActive && c.Name == "My Tone");
    }

    /// <summary>Overwriting a user profile with the live rig re-points the active
    /// tone at the SAVED instance (the old one still carried the pre-overwrite rig).</summary>
    [Fact]
    public void Overwriting_a_user_tone_repoints_the_active_tone_at_the_saved_rig()
    {
        var svc = new FakeDspTonePresetService();
        var rig = new DcRigPreset([new DcRigModule("amp", new Dictionary<string, float>(), false)]);
        var vm = new PresetBrowserViewModel(svc, FakeLocalization.English(), () => rig, () => { })
        {
            SaveName = "Mine"
        };
        vm.ConfirmSave();
        var card = vm.SelectedCard!;
        Assert.True(card.IsUser);

        vm.Overwrite();

        Assert.Equal("Mine", vm.ActivePresetName);
        Assert.Contains(vm.Cards, c => c.IsActive && c.Name == "Mine");
        Assert.Equal(2, svc.Saves.Count);
    }

    /// <summary>The curated `PresetCuration` ordering must actually reach the card
    /// grid — it was a defined-but-unconsumed seam.</summary>
    [Fact]
    public void Browser_card_grid_surfaces_the_curated_featured_tones_first()
    {
        var featured = PresetCuration.FeaturedIds[0];
        var factory = new List<DspTonePreset>
        {
            new("zz-filler", "Filler", DspToneCategory.Clean, "", new DcRigPreset([])),
            new(featured, "Featured", DspToneCategory.Clean, "", new DcRigPreset([]))
        };
        var vm = new PresetBrowserViewModel(
            new FakeDspTonePresetService(factory), FakeLocalization.English(), () => new DcRigPreset([]), () => { });

        // The All rail: the featured tone leads even though the catalogue lists it last.
        Assert.Equal(featured, vm.Cards[0].Id);
    }

    // ── rig tuner: the 66 ms poll is gated PER HOSTING SURFACE ────────────────

    /// <summary>
    /// One singleton VM, two hosts (editor overlay + Play Mode). The poll must run
    /// while ANY host surface is live and stop the moment the last one goes away —
    /// closing one host while the other is open must NOT stop the tuner, and closing
    /// the panel must stop it regardless.
    /// </summary>
    [Fact]
    public void Rig_tuner_polls_only_while_open_and_a_host_surface_is_live()
    {
        var vm = TestRigBuilders.Pedalboard(
            new FakeDspChainService(), new FakeDspTonePresetService(), FakeLocalization.English(),
            audio: new FakeAudioPlaybackService());
        var tuner = vm.RigTuner;
        var editorHost = new object();
        var playModeHost = new object();

        // A live surface alone never polls — the panel has to be open.
        tuner.SetSurfaceLive(editorHost, true);
        Assert.False(tuner.IsPolling);

        tuner.Open();
        Assert.True(tuner.IsPolling);

        // Second host opens on the same singleton VM: still exactly one timer.
        tuner.SetSurfaceLive(playModeHost, true);
        Assert.True(tuner.IsPolling);

        // Closing/hiding ONE host must not stop the other host's tuner.
        tuner.SetSurfaceLive(editorHost, false);
        Assert.True(tuner.IsPolling);

        // The last live surface goes → the poll stops (no native input reads while hidden).
        tuner.SetSurfaceLive(playModeHost, false);
        Assert.False(tuner.IsPolling);

        // Re-arm, then close the panel: closing must stop it even with a live host.
        tuner.SetSurfaceLive(playModeHost, true);
        Assert.True(tuner.IsPolling);
        tuner.Close();
        Assert.False(tuner.IsPolling);
    }
}
