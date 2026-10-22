using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Services.Audio.Coordination;

namespace DotCharter.Tests.Services.Audio.Coordination;

/// <summary>
///     The chart editor's "Preview tones" toggle: while it is on the live guitar
///     runs through the tone at the PLAYHEAD, and a tone change is heard as the
///     playhead crosses it.
///
///     <para>
///         These tests exist because a toggle that flips a bool nobody reads is
///         the failure this repo has shipped three times. Every one of them
///         asserts against the DSP CHAIN (the thing the audio actually runs
///         through) or against the mixer, never against the toggle's own state.
///     </para>
/// </summary>
public sealed class ChartTonePreviewControllerTests
{
    // A deadlock detector, never a budget: the coordinator applies on a worker, and this class
    // shares the CPU-saturated parallel phase with the rest of the gate.
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

    private static IReadOnlyList<DspModuleType> Catalog() =>
        new[] { "amp", "cab", "gain", "overdrive", "distortion", "ds1" /* Pedal_Distortion converts to ds1 since 0.54 */, "eq", "reverb", "delay", "chorus", "flanger" }
            .Select(id => new DspModuleType(id, id,
                new[] { new DspParamInfo("p", "p", -100f, 100f, 0f) }))
            .ToList();

    private const string CleanJson =
        """{"Key":"clean","Name":"clean","GearList":{"Amp":{"Type":"Amps","Key":"Amp_TwinReverb","KnobValues":{}}}}""";

    private const string HeavyJson =
        """{"Key":"heavy","Name":"heavy","GearList":{"Amp":{"Type":"Amps","Key":"Amp_5150","KnobValues":{}},"PrePedal1":{"Type":"Pedals","Key":"Pedal_Distortion","Category":"Distortion","KnobValues":{}}}}""";

    /// <summary>The four mixer facts that can silence a correctly routed guitar.</summary>
    private sealed class FakeMixer : IGuitarMonitorMixer
    {
        public bool IsInputMuted { get; set; }
        public bool IsInputSoloed { get; set; }
        public bool IsSongSoloed { get; set; }
        public bool IsAnythingSoloed => IsSongSoloed || IsInputSoloed;
        public double InputVolume { get; set; } = 1.0;
        public double MonitorVolume { get; set; } = 1.0;
    }

    private static string[] Order(IDspChainService chain) =>
        chain.GetNodes().Select(n => n.ModuleTypeId).ToArray();

    /// <summary>Every apply, in order — <c>ActiveToneChanged</c> fires on the
    /// coordinator's worker, hence the concurrent queue.</summary>
    private static System.Collections.Concurrent.ConcurrentQueue<string> RecordApplies(
        RsTonePlaybackCoordinator tones)
    {
        var applied = new System.Collections.Concurrent.ConcurrentQueue<string>();
        tones.ActiveToneChanged += info => applied.Enqueue(info.ToneName);
        return applied;
    }

    /// <summary>
    ///     Wait until <paramref name="count" /> applies have COMPLETED.
    ///     <para>
    ///         Never spin on the chain's node count instead: <c>DcRigApplier</c>
    ///         appends a new node at the tail and only then moves it into signal
    ///         order, so the count reaches its final value while the ORDER is
    ///         still wrong and the test reads a torn chain. <c>ActiveToneChanged</c>
    ///         fires after <c>Apply</c> returns, under the same gate.
    ///     </para>
    /// </summary>
    private static void WaitForApplies(
        System.Collections.Concurrent.ConcurrentQueue<string> applied, int count, string because)
    {
        Assert.True(
            SpinWait.SpinUntil(() => applied.Count >= count, Deadline),
            $"{because} (saw {applied.Count} of {count} applies: {string.Join(", ", applied)})");
    }

    /// <summary>A project whose base tone is "clean" and which switches to
    /// "heavy" at beat 4 (= 4,000 ms on this 60 BPM grid), with both tone bodies
    /// authored on the project the way the Tone Designer writes them.</summary>
    private static (ChartProject Project, Arrangement Arrangement) CleanToHeavyProject(bool authorTones = true)
    {
        var project = new ChartProject();
        for (var i = 0; i < 8; i++)
            project.Beats.Add(new BeatMarker { Time = i * 1000, IsMeasure = i % 4 == 0 });

        if (authorTones)
        {
            project.ToneDefinitions.Add(new ToneDefinition { Key = "clean", Name = "clean", Json = CleanJson });
            project.ToneDefinitions.Add(new ToneDefinition { Key = "heavy", Name = "heavy", Json = HeavyJson });
        }

        var arr = new Arrangement { Name = "Lead", ToneBase = "clean" };
        arr.ToneChanges.Add(new ToneChange { Position = new FractionalPosition(4), ToneName = "heavy" });
        project.Arrangements.Add(arr);
        return (project, arr);
    }

    private static ProjectState StateFor(ChartProject project, Arrangement arrangement)
    {
        var state = new ProjectState { ActiveProject = project, ActiveArrangement = arrangement };
        return state;
    }

    private static (ChartTonePreviewController Preview, IDspChainService Chain, FakeMixer Mixer)
        NewController(ProjectState state, IDspChainService? chain = null, FakeMixer? mixer = null)
    {
        var effective = chain ?? new FakeDspChainService(Catalog());
        var tones = new RsTonePlaybackCoordinator(
            effective, new DcRigApplier(effective), state, new FakeConfigurationService());
        var m = mixer ?? new FakeMixer();
        return (new ChartTonePreviewController(tones, state, FakeLocalization.English(), m), effective, m);
    }

    // ------------------------------------------------------ unavailable states

    [Fact]
    public void WithNoOpenProject_TheToggleIsUnavailableAndPrintsItsReason()
    {
        var (preview, _, _) = NewController(new ProjectState());

        Assert.False(preview.IsAvailable);
        Assert.Equal("Toolbar_TonePreview_NoProject", preview.UnavailableReasonKey);
        Assert.Equal(FakeLocalization.English()["Toolbar_TonePreview_NoProject"], preview.Tip);
    }

    [Fact]
    public void WithNoNativeToneRig_TheToggleIsUnavailableAndPrintsItsReason()
    {
        // The CoopRigApplier rule: a build whose dca_engine_dsp_* entry points are
        // missing resolves NullDspChainService, which hosts no rig at all. The
        // toggle must say so rather than look live over nothing.
        var (project, arr) = CleanToHeavyProject();
        var (preview, _, _) = NewController(StateFor(project, arr), new NullDspChainService());

        Assert.False(preview.IsAvailable);
        Assert.Equal("Toolbar_TonePreview_NoRig", preview.UnavailableReasonKey);
    }

    [Fact]
    public void WithNoAuthoredToneBodies_TheToggleIsUnavailableAndPrintsItsReason()
    {
        // Tone CHANGES with no tone DEFINITIONS is an ordinary half-authored
        // chart: there is nothing to build a rig from.
        var (project, arr) = CleanToHeavyProject(authorTones: false);
        var (preview, _, _) = NewController(StateFor(project, arr));

        Assert.False(preview.IsAvailable);
        Assert.Equal("Toolbar_TonePreview_NoTones", preview.UnavailableReasonKey);
    }

    [Fact]
    public void AnUnavailableToggle_RefusesToTurnOnAndTouchesNothing()
    {
        var (project, arr) = CleanToHeavyProject(authorTones: false);
        var (preview, chain, _) = NewController(StateFor(project, arr));
        chain.AddNode("reverb"); // the charter's own board

        Assert.False(preview.TurnOn());
        Assert.False(preview.IsOn);
        Assert.Equal(new[] { "reverb" }, Order(chain));
    }

    [Fact]
    public void AuthoringTheMissingTones_MakesTheToggleAvailableWithoutAReload()
    {
        var (project, arr) = CleanToHeavyProject(authorTones: false);
        var (preview, _, _) = NewController(StateFor(project, arr));
        Assert.False(preview.IsAvailable);

        project.ToneDefinitions.Add(new ToneDefinition { Key = "clean", Name = "clean", Json = CleanJson });

        Assert.True(preview.IsAvailable);
        Assert.Equal(FakeLocalization.English()["Toolbar_TonePreview_Tip"], preview.Tip);
    }

    // ------------------------------------------------------------- the drive

    [Fact]
    public void TurningItOn_RunsTheGuitarThroughTheToneAtThePlayhead()
    {
        var (project, arr) = CleanToHeavyProject();
        var state = StateFor(project, arr);
        var (preview, chain, _) = NewController(state);
        chain.AddNode("reverb"); // the charter's own board

        Assert.True(preview.TurnOn());
        Assert.True(preview.IsOn);
        // The base tone "clean" is amp + cab + trailing gain; the manual reverb is gone.
        Assert.Equal(new[] { "amp", "cab", "gain" }, Order(chain));

        preview.TurnOff();
    }

    /// <summary>
    ///     #116: the controller is disposed only at SHUTDOWN, after the audio engine
    ///     is already gone, and restoring the charter's board there rebuilt it into
    ///     detached chains — two WARN lines per module, 76 % of a real log's warnings.
    ///     Disposing abandons the preview without touching the chain; TurnOff (a
    ///     user gesture) still restores the board.
    /// </summary>
    [Fact]
    public async Task Disposing_AbandonsThePreview_WithoutRebuildingTheRig()
    {
        var (project, arr) = CleanToHeavyProject();
        var state = StateFor(project, arr);
        var chain = new FakeDspChainService(Catalog());
        var tones = new RsTonePlaybackCoordinator(
            chain, new DcRigApplier(chain), state, new FakeConfigurationService());
        var preview = new ChartTonePreviewController(tones, state, FakeLocalization.KeysOnly(), new FakeMixer());
        chain.AddNode("reverb"); // the charter's own board
        Assert.True(preview.TurnOn());
        var changesBefore = chain.NodesChangedCount;

        preview.Dispose();
        await tones.RestoreCompletion;

        Assert.False(preview.IsOn);
        Assert.False(tones.IsDriving);
        Assert.Equal(changesBefore, chain.NodesChangedCount);   // no restore apply
        Assert.Equal(new[] { "amp", "cab", "gain" }, Order(chain));
    }

    /// <summary>#116: re-applying the tone that is already on the rig (an arrangement
    /// switch that keeps it) is correct, so it logs "unchanged" at Debug — not the
    /// Info line that blamed a detached engine 12 times in a real log.</summary>
    [Fact]
    public void ReapplyingTheToneAlreadyOnTheRig_IsQuietAndNotBlamedOnTheEngine()
    {
        var (project, arr) = CleanToHeavyProject();
        var state = StateFor(project, arr);
        var chain = new FakeDspChainService(Catalog());
        var logger = new FakeAppLogger();
        var tones = new RsTonePlaybackCoordinator(
            chain, new DcRigApplier(chain), state, new FakeConfigurationService(), logger: logger);
        using var preview = new ChartTonePreviewController(tones, state, FakeLocalization.KeysOnly(), new FakeMixer());
        Assert.True(preview.TurnOn());
        logger.Entries.Clear();

        tones.ApplyToneKey("clean");   // the base tone, already applied by TurnOn

        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("likely not attached", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, e => e.Level == "DEBUG" && e.Message.Contains("unchanged", StringComparison.Ordinal));
        preview.TurnOff();
    }

    [Fact]
    public void ItStartsFromTheToneUnderThePLAYHEAD_NotAlwaysTheBaseTone()
    {
        var (project, arr) = CleanToHeavyProject();
        var state = StateFor(project, arr);
        state.CurrentTimeMs = 6000; // past the change at 4,000 ms
        var (preview, chain, _) = NewController(state);

        Assert.True(preview.TurnOn());
        Assert.Equal(new[] { "ds1", "amp", "cab", "gain" }, Order(chain));

        preview.TurnOff();
    }

    [Fact]
    public async Task CrossingAToneChangeWhileCharting_SwitchesTheRig()
    {
        var (project, arr) = CleanToHeavyProject();
        var state = StateFor(project, arr);
        var chain = new FakeDspChainService(Catalog());
        var tones = new RsTonePlaybackCoordinator(
            chain, new DcRigApplier(chain), state, new FakeConfigurationService());
        using var preview = new ChartTonePreviewController(tones, state, FakeLocalization.KeysOnly(), new FakeMixer());

        using var heavyApplied = new ManualResetEventSlim(false);
        tones.ActiveToneChanged += info =>
        {
            if (info.ToneName == "heavy") heavyApplied.Set();
        };

        Assert.True(preview.TurnOn());
        Assert.Equal(new[] { "amp", "cab", "gain" }, Order(chain));

        state.CurrentTimeMs = 1000; // still inside the base region
        Assert.Equal(new[] { "amp", "cab", "gain" }, Order(chain));

        state.CurrentTimeMs = 4500; // crossed the ToneChange
        Assert.True(heavyApplied.Wait(Deadline, TestContext.Current.CancellationToken),
            "scrubbing past the tone change did not switch the rig");
        Assert.Equal(new[] { "ds1", "amp", "cab", "gain" }, Order(chain));

        preview.TurnOff();
        await tones.RestoreCompletion;
    }

    [Fact]
    public async Task TurningItOff_PutsTheChartersOwnRigBack()
    {
        var (project, arr) = CleanToHeavyProject();
        var state = StateFor(project, arr);
        var chain = new FakeDspChainService(Catalog());
        var tones = new RsTonePlaybackCoordinator(
            chain, new DcRigApplier(chain), state, new FakeConfigurationService());
        chain.AddNode("reverb");
        chain.AddNode("delay");
        using var preview = new ChartTonePreviewController(tones, state, FakeLocalization.KeysOnly(), new FakeMixer());

        Assert.True(preview.TurnOn());
        Assert.Equal(new[] { "amp", "cab", "gain" }, Order(chain));

        preview.TurnOff();
        Assert.False(preview.IsOn);
        await tones.RestoreCompletion;
        Assert.Equal(new[] { "reverb", "delay" }, Order(chain));
    }

    [Fact]
    public async Task AddingAToneChangeWhileItIsOn_IsHeardWithoutAnOffOn()
    {
        // The authoring gesture the feature exists for: place a tone change, hear
        // it. The schedule is built once at take-over, so the collection has to be
        // watched or a just-placed marker is silent until the toggle is cycled.
        var project = new ChartProject();
        for (var i = 0; i < 8; i++) project.Beats.Add(new BeatMarker { Time = i * 1000, IsMeasure = i % 4 == 0 });
        project.ToneDefinitions.Add(new ToneDefinition { Key = "clean", Name = "clean", Json = CleanJson });
        project.ToneDefinitions.Add(new ToneDefinition { Key = "heavy", Name = "heavy", Json = HeavyJson });
        var arr = new Arrangement { Name = "Lead", ToneBase = "clean" };
        project.Arrangements.Add(arr);

        var state = StateFor(project, arr);
        state.CurrentTimeMs = 6000;
        var chain = new FakeDspChainService(Catalog());
        var tones = new RsTonePlaybackCoordinator(
            chain, new DcRigApplier(chain), state, new FakeConfigurationService());
        var applied = RecordApplies(tones);
        using var preview = new ChartTonePreviewController(tones, state, FakeLocalization.KeysOnly(), new FakeMixer());

        Assert.True(preview.TurnOn());
        Assert.Equal(new[] { "amp", "cab", "gain" }, Order(chain)); // base tone only

        arr.ToneChanges.Add(new ToneChange { Position = new FractionalPosition(4), ToneName = "heavy" });

        // The playhead (6,000 ms) now sits inside the new marker's region. The
        // apply lands on the coordinator's worker rather than inline: this fires
        // from an editing gesture, and a native chain diff on the UI thread is
        // exactly what the "never block the UI thread on an apply gate" rule
        // forbids. It used to be synchronous only because placing a marker went
        // through a full stop + start.
        WaitForApplies(applied, 2, "the just-placed tone change was not heard");
        Assert.Equal(new[] { "ds1", "amp", "cab", "gain" }, Order(chain));

        preview.TurnOff();
        await tones.RestoreCompletion;
    }

    /// <summary>
    ///     THE REGRESSION. A marker's POSITION is set on the object, so dragging
    ///     one raises no collection event — the schedule went on describing where
    ///     it used to be until the toggle was cycled. History is the only signal
    ///     the gesture raises, and it covers a tempo-map edit under a marker too.
    /// </summary>
    [Fact]
    public async Task DraggingAToneChange_IsHeardWhenHistorySaysTheChartChanged()
    {
        var (project, arr) = CleanToHeavyProject();
        var state = StateFor(project, arr);
        state.CurrentTimeMs = 4500; // past the change at beat 4 ⇒ heavy
        var chain = new FakeDspChainService(Catalog());
        var tones = new RsTonePlaybackCoordinator(
            chain, new DcRigApplier(chain), state, new FakeConfigurationService());
        var history = new HistoryService(new FakeAppLogger());
        var applied = RecordApplies(tones);
        using var preview = new ChartTonePreviewController(tones, state, FakeLocalization.KeysOnly(), new FakeMixer(), null, history);

        Assert.True(preview.TurnOn());
        Assert.Equal(new[] { "ds1", "amp", "cab", "gain" }, Order(chain));

        // Drag the marker past the playhead — the collection is untouched.
        arr.ToneChanges[0].Position = new FractionalPosition(6);
        Assert.Equal(new[] { "ds1", "amp", "cab", "gain" }, Order(chain)); // still stale, as expected

        // Any committed editing gesture — the controller re-arms on the SIGNAL,
        // not on knowing which action moved what.
        history.Do(new AddToneChangesAction(
            [],
            [new ToneChange { Position = new FractionalPosition(1), ToneName = "clean" }]));

        WaitForApplies(applied, 2,
            "the dragged tone change was never heard — the schedule stayed where the marker used to be");
        Assert.Equal(new[] { "amp", "cab", "gain" }, Order(chain));

        preview.TurnOff();
        await tones.RestoreCompletion;
    }

    /// <summary>
    ///     Placing a marker used to go through <c>EndPreview</c> + <c>BeginPreview</c>:
    ///     the charter's own board was restored and the tone rig rebuilt on top of
    ///     it for every gesture — audible, and exactly the teardown
    ///     <c>DcRigApplier</c> exists to avoid. A re-arm that resolves to the tone
    ///     already on the rig must now cost nothing at all.
    /// </summary>
    [Fact]
    public void PlacingAMarkerThePlayheadIsNotIn_DoesNotRebuildTheRig()
    {
        var (project, arr) = CleanToHeavyProject();
        arr.ToneChanges.Clear();
        var state = StateFor(project, arr);
        var chain = new FakeDspChainService(Catalog());
        var tones = new RsTonePlaybackCoordinator(
            chain, new DcRigApplier(chain), state, new FakeConfigurationService());
        var applied = new System.Collections.Concurrent.ConcurrentQueue<string>();
        tones.ActiveToneChanged += info => applied.Enqueue(info.ToneName);
        using var preview = new ChartTonePreviewController(tones, state, FakeLocalization.KeysOnly(), new FakeMixer());

        Assert.True(preview.TurnOn()); // one apply: the take-over

        // A marker placed well ahead of the playhead (which is at 0).
        arr.ToneChanges.Add(new ToneChange { Position = new FractionalPosition(6), ToneName = "heavy" });

        Assert.Equal(["clean"], applied.ToArray());
        Assert.Equal(new[] { "amp", "cab", "gain" }, Order(chain));

        preview.TurnOff();
    }

    /// <summary>
    ///     A tone body rewritten in the Tone Designer, under an unchanged NAME.
    ///     The re-convert publishes new presets, but the apply worker holds the
    ///     dictionary captured at take-over — so without the re-point an edited
    ///     tone converted perfectly and reached nothing.
    /// </summary>
    [Fact]
    public async Task AToneBodyRewrittenWhilePreviewing_ReachesTheRig()
    {
        var (project, arr) = CleanToHeavyProject();
        var state = StateFor(project, arr);
        state.CurrentTimeMs = 4500; // sitting in "heavy"
        var chain = new FakeDspChainService(Catalog());
        var tones = new RsTonePlaybackCoordinator(
            chain, new DcRigApplier(chain), state, new FakeConfigurationService());
        var applied = RecordApplies(tones);
        using var preview = new ChartTonePreviewController(tones, state, FakeLocalization.KeysOnly(), new FakeMixer());

        Assert.True(preview.TurnOn());
        Assert.Equal(new[] { "ds1", "amp", "cab", "gain" }, Order(chain));

        // What UpsertToneDefinitionAction does: same key, new body.
        project.ToneDefinitions.RemoveAt(1);
        project.ToneDefinitions.Add(new ToneDefinition
        {
            Key = "heavy",
            Name = "heavy",
            Json = """{"Key":"heavy","Name":"heavy","GearList":{"Amp":{"Type":"Amps","Key":"Amp_TwinReverb","KnobValues":{}}}}"""
        });

        // Two refreshes fire (remove + add) but only ONE applies: between them
        // "heavy" has no body, and ApplyToneKey keeps the current rig and raises
        // nothing rather than tearing it down over a half-finished edit.
        WaitForApplies(applied, 2, "the rewritten tone body never reached the rig");
        Assert.Equal(new[] { "amp", "cab", "gain" }, Order(chain));

        preview.TurnOff();
        await tones.RestoreCompletion;
    }

    [Fact]
    public void SwitchingToAnArrangementWithNoTones_SwitchesThePreviewOff()
    {
        var (project, arr) = CleanToHeavyProject();
        var bare = new Arrangement { Name = "Bass" };
        project.Arrangements.Add(bare);
        var state = StateFor(project, arr);
        var (preview, _, _) = NewController(state);

        Assert.True(preview.TurnOn());

        state.ActiveArrangement = bare;

        Assert.False(preview.IsOn);
        Assert.False(preview.IsAvailable);
        Assert.Equal("Toolbar_TonePreview_NoTones", preview.UnavailableReasonKey);
    }

    // ---------------------------------------------------------- audibility

    [Fact]
    public void TurningItOn_MakesAMutedGuitarAudible_AndTurningItOffPutsTheMixBack()
    {
        var (project, arr) = CleanToHeavyProject();
        var mixer = new FakeMixer
        {
            IsInputMuted = true,
            IsSongSoloed = true,   // …and everything not soloed is silent
            InputVolume = 0.0,
            MonitorVolume = 0.0
        };
        var (preview, _, _) = NewController(StateFor(project, arr), mixer: mixer);

        Assert.True(preview.TurnOn());
        Assert.False(mixer.IsInputMuted);
        Assert.True(mixer.IsInputSoloed);   // ADDED to the solo group; the song stays soloed
        Assert.True(mixer.IsSongSoloed);
        Assert.Equal(1.0, mixer.InputVolume);
        Assert.Equal(1.0, mixer.MonitorVolume);

        preview.TurnOff();
        Assert.True(mixer.IsInputMuted);
        Assert.False(mixer.IsInputSoloed);
        Assert.True(mixer.IsSongSoloed);
        Assert.Equal(0.0, mixer.InputVolume);
        Assert.Equal(0.0, mixer.MonitorVolume);
    }

    [Fact]
    public void AnAudibleGuitarIsLeftEntirelyAlone()
    {
        var (project, arr) = CleanToHeavyProject();
        var mixer = new FakeMixer { InputVolume = 0.6, MonitorVolume = 0.8 };
        var (preview, _, _) = NewController(StateFor(project, arr), mixer: mixer);

        Assert.True(preview.TurnOn());
        Assert.Equal(0.6, mixer.InputVolume);
        Assert.Equal(0.8, mixer.MonitorVolume);
        Assert.False(mixer.IsInputSoloed);

        preview.TurnOff();
        Assert.Equal(0.6, mixer.InputVolume);
        Assert.Equal(0.8, mixer.MonitorVolume);
    }

    [Fact]
    public void AFaderTheCharterMovedWhilePreviewingIsNotStompedOnTheWayOut()
    {
        var (project, arr) = CleanToHeavyProject();
        var mixer = new FakeMixer { MonitorVolume = 0.0 };
        var (preview, _, _) = NewController(StateFor(project, arr), mixer: mixer);

        Assert.True(preview.TurnOn());
        Assert.Equal(1.0, mixer.MonitorVolume);

        mixer.MonitorVolume = 0.35; // the charter set their own monitoring level

        preview.TurnOff();
        Assert.Equal(0.35, mixer.MonitorVolume);
    }
}
