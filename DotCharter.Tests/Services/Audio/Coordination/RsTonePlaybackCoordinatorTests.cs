using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Parsing.Psarc;
using DotCharter.Services.Audio.Coordination;

namespace DotCharter.Tests.Services.Audio.Coordination;

/// <summary>
/// Verifies the seam between the pure Phase-2 pieces and playback: pre-play
/// conversion + badge summary, chain take-over on <c>BeginRun</c>, live switching
/// as the coalesced playhead crosses a ToneChange, and manual-chain restore on
/// <c>EndRun</c>. Audio behaviour itself is not observable headlessly (there is no
/// engine) — this drives the coordinator's LOGIC against an in-memory chain.
/// </summary>
[Collection("GameplayToneHud")]
public sealed class RsTonePlaybackCoordinatorTests
{
    private static IReadOnlyList<DspModuleType> Catalog() =>
        new[] { "amp", "cab", "gain", "overdrive", "distortion", "ds1" /* Pedal_Distortion converts to ds1 since 0.54 */, "eq", "reverb", "delay", "chorus", "flanger" }
            .Select(id => new DspModuleType(id, id,
                new[] { new DspParamInfo("p", "p", -100f, 100f, 0f) }))
            .ToList();

    private const string CleanJson =
        """{"Key":"clean","Name":"clean","GearList":{"Amp":{"Type":"Amps","Key":"Amp_TwinReverb","KnobValues":{}}}}""";

    private const string HeavyJson =
        """{"Key":"heavy","Name":"heavy","GearList":{"Amp":{"Type":"Amps","Key":"Amp_5150","KnobValues":{}},"PrePedal1":{"Type":"Pedals","Key":"Pedal_Distortion","Category":"Distortion","KnobValues":{}}}}""";

    private static RsTonePlaybackCoordinator NewCoordinator(
        IDspChainService chain, ProjectState state, FakeConfigurationService config,
        params (string Key, string Json)[] tones)
    {
        // The app's HUD adapter, so the HUD test below reads the real channel.
        var coord = new RsTonePlaybackCoordinator(chain, new DcRigApplier(chain), state, config,
            toneHud: new DotCharter.UI.Platform.RenderingGameplayToneHud())
        {
            ToneDefinitionSource = _ => tones.Select(t => new PsarcToneDefinition(t.Key, t.Key, t.Json)).ToList()
        };
        return coord;
    }

    private static Arrangement CleanToHeavyArrangement()
    {
        var arr = new Arrangement { ToneBase = "clean" };
        arr.ToneChanges.Add(new ToneChange { Position = new FractionalPosition(4), ToneName = "heavy" });
        return arr;
    }

    private static ProjectState StateWithBeats()
    {
        var state = new ProjectState();
        for (var i = 0; i < 8; i++)
            state.Beats.Add(new BeatMarker { Time = i * 1000, IsMeasure = i % 4 == 0 });
        return state;
    }

    private static string[] Order(IDspChainService chain) =>
        chain.GetNodes().Select(n => n.ModuleTypeId).ToArray();

    // -------------------------------------------------------------- pre-play

    [Fact]
    public void PrepareForSong_OwnChart_NoPsarc_HasNoTones()
    {
        var chain = new FakeDspChainService(Catalog());
        var coord = NewCoordinator(chain, new ProjectState(), new FakeConfigurationService(), ("clean", CleanJson));

        var summary = coord.PrepareForSong(null, new Arrangement { ToneBase = "clean" });

        Assert.False(summary.HasTones);
        Assert.True(summary.DspAvailable);
    }

    [Fact]
    public void PrepareForSong_NullArrangement_HasNoTones()
    {
        var chain = new FakeDspChainService(Catalog());
        var coord = NewCoordinator(chain, new ProjectState(), new FakeConfigurationService(), ("clean", CleanJson));

        var summary = coord.PrepareForSong("song.psarc", null);

        Assert.False(summary.HasTones);
    }

    [Fact]
    public void PrepareForSong_NullDsp_ReportsDspUnavailable()
    {
        var coord = NewCoordinator(new NullDspChainService(), new ProjectState(),
            new FakeConfigurationService(), ("clean", CleanJson));

        var summary = coord.PrepareForSong("song.psarc", new Arrangement { ToneBase = "clean" });

        Assert.False(summary.DspAvailable);
    }

    [Fact]
    public void PrepareForSong_CountsReferencedTones()
    {
        var chain = new FakeDspChainService(Catalog());
        var coord = NewCoordinator(chain, StateWithBeats(), new FakeConfigurationService(),
            ("clean", CleanJson), ("heavy", HeavyJson));

        var summary = coord.PrepareForSong("song.psarc", CleanToHeavyArrangement());

        Assert.True(summary.HasTones);
        Assert.Equal(2, summary.ToneCount); // base "clean" + change "heavy"
    }

    [Fact]
    public void PrepareForSong_SurfacesApproximationsNonBlocking()
    {
        // Every catalogue pedal converts since 2026-10 (#77), so the honest skip
        // is gear the catalogue does not know, filed as a Filter with no EQ band.
        const string fxJson =
            """{"Key":"fx","Name":"fx","GearList":{"Amp":{"Type":"Amps","Key":"Amp_TwinReverb","KnobValues":{}},"PrePedal1":{"Type":"Pedals","Key":"Pedal_FutureFilter","Category":"Filter","KnobValues":{"Pedal_FutureFilter_Sweep":50}}}}""";
        var chain = new FakeDspChainService(Catalog());
        var coord = NewCoordinator(chain, new ProjectState(), new FakeConfigurationService(), ("fx", fxJson));

        var summary = coord.PrepareForSong("song.psarc", new Arrangement { ToneBase = "fx" });

        Assert.True(summary.HasTones);          // still usable — never a hard failure
        Assert.True(summary.HasApproximations); // the unknown filter pedal is skipped
        Assert.Equal(1, summary.SkippedEffectCount);
        Assert.NotEmpty(summary.Notes);
    }

    // --------------------------------------------------- async pre-play (BUG 2)

    [Fact]
    public async Task PrepareForSongAsync_MatchesSyncSummary()
    {
        var chain = new FakeDspChainService(Catalog());
        var coord = NewCoordinator(chain, StateWithBeats(), new FakeConfigurationService(),
            ("clean", CleanJson), ("heavy", HeavyJson));

        var summary = await coord.PrepareForSongAsync("song.psarc", CleanToHeavyArrangement());

        Assert.True(summary.HasTones);
        Assert.Equal(2, summary.ToneCount);           // base "clean" + change "heavy"
        Assert.Same(summary, coord.LastSummary);       // LastSummary published by the async path
    }

    [Fact]
    public async Task PrepareForSongAsync_OwnChart_NoPsarc_ShortCircuits()
    {
        var chain = new FakeDspChainService(Catalog());
        var coord = NewCoordinator(chain, new ProjectState(), new FakeConfigurationService(), ("clean", CleanJson));

        var summary = await coord.PrepareForSongAsync(null, new Arrangement { ToneBase = "clean" });

        Assert.False(summary.HasTones);
        Assert.True(summary.DspAvailable);
    }

    [Fact]
    public async Task PrepareForSongAsync_ThenBeginRun_AppliesBaseTone()
    {
        var chain = new FakeDspChainService(Catalog());
        var config = new FakeConfigurationService { AppConfig = { UseInGameTones = true } };
        var coord = NewCoordinator(chain, StateWithBeats(), config, ("clean", CleanJson), ("heavy", HeavyJson));

        await coord.PrepareForSongAsync("song.psarc", CleanToHeavyArrangement());

        Assert.True(coord.BeginRun());               // prepared arrangement + LastSummary are set
        Assert.True(coord.IsDriving);
        Assert.Equal(new[] { "amp", "cab", "gain" }, Order(chain)); // base "clean" applied

        coord.EndRun();
    }

    // ------------------------------------------------------------- run drive

    [Fact]
    public void BeginRun_TogglesOff_DoesNotDrive()
    {
        var chain = new FakeDspChainService(Catalog());
        var config = new FakeConfigurationService(); // UseInGameTones defaults false
        var coord = NewCoordinator(chain, StateWithBeats(), config, ("clean", CleanJson), ("heavy", HeavyJson));
        coord.PrepareForSong("song.psarc", CleanToHeavyArrangement());

        Assert.False(coord.BeginRun());
        Assert.False(coord.IsDriving);
        Assert.Empty(chain.GetNodes()); // manual chain untouched
    }

    [Fact]
    public void BeginRun_AppliesBaseTone_AndArmsDriving()
    {
        var chain = new FakeDspChainService(Catalog());
        var config = new FakeConfigurationService { AppConfig = { UseInGameTones = true } };
        var coord = NewCoordinator(chain, StateWithBeats(), config, ("clean", CleanJson), ("heavy", HeavyJson));
        coord.PrepareForSong("song.psarc", CleanToHeavyArrangement());

        Assert.True(coord.BeginRun());
        Assert.True(coord.IsDriving);
        // Base tone "clean" is amp + cab + trailing gain.
        Assert.Equal(new[] { "amp", "cab", "gain" }, Order(chain));

        coord.EndRun();
    }

    /// <summary>
    ///     #155: Rocksmith runs every tone behind its own input gate (a Wwise Expander
    ///     on the Input_Calibration_P1 bus that follows the player's noise floor), and
    ///     a song tone is applied with that stage FIRST, ahead of every pre-pedal: a
    ///     <c>gate</c> in auto-floor mode at the game's 50:1 and -50 dB ceiling.
    /// </summary>
    [Fact]
    public void BeginRun_PutsTheGamesInputGateInFrontOfTheSongTone()
    {
        var gateType = new DspModuleType("gate", "gate",
            new[] { "thresholdDb", "ratio", "attackMs", "holdMs", "releaseMs", "autoFloor" }
                .Select(p => new DspParamInfo(p, p, -100f, 100f, 0f)).ToArray());
        var chain = new FakeDspChainService([.. Catalog(), gateType]);
        var config = new FakeConfigurationService { AppConfig = { UseInGameTones = true } };
        var coord = NewCoordinator(chain, StateWithBeats(), config, ("clean", CleanJson), ("heavy", HeavyJson));
        coord.PrepareForSong("song.psarc", CleanToHeavyArrangement());

        Assert.True(coord.BeginRun());
        Assert.Equal(new[] { "gate", "amp", "cab", "gain" }, Order(chain));
        var gate = chain.GetNodes()[0];
        Assert.Equal(1f, chain.GetParam(gate.NodeId, "autoFloor"));
        Assert.Equal(RsInputGate.CeilingDb, chain.GetParam(gate.NodeId, "thresholdDb"));
        Assert.Equal(RsInputGate.Ratio, chain.GetParam(gate.NodeId, "ratio"));

        coord.EndRun();
    }

    [Fact]
    public async Task IsPrepareInFlight_TrueWhileConverting_AndBeginRunSkipsWithoutBlocking()
    {
        var chain = new FakeDspChainService(Catalog());
        var config = new FakeConfigurationService { AppConfig = { UseInGameTones = true } };
        using var gate = new ManualResetEventSlim(false);
        var coord = new RsTonePlaybackCoordinator(chain, new DcRigApplier(chain), StateWithBeats(), config)
        {
            ToneDefinitionSource = _ =>
            {
                gate.Wait(TimeSpan.FromSeconds(10)); // hold the conversion open
                return new[] { new PsarcToneDefinition("clean", "clean", CleanJson) };
            }
        };

        var prepare = coord.PrepareForSongAsync("song.psarc", CleanToHeavyArrangement());

        // Conversion is parked on the gate: the state is exposed (the VM shows
        // 'Tones loading…') and BeginRun skips WITHOUT blocking on the task.
        Assert.True(coord.IsPrepareInFlight);
        Assert.False(coord.BeginRun());
        Assert.False(coord.IsDriving);

        gate.Set();
        await prepare;

        Assert.False(coord.IsPrepareInFlight);
        Assert.True(coord.BeginRun()); // same run now drives normally
        coord.EndRun();
    }

    // ---------------------------------------- per-song tone preference (2.4)

    [Fact]
    public void BeginRun_PerSongInGamePref_ForcesDrivingWithGlobalToggleOff()
    {
        var chain = new FakeDspChainService(Catalog());
        var config = new FakeConfigurationService(); // UseInGameTones defaults false
        config.AppConfig.SongTonePreferences["lib:SONG"] =
            new SongTonePreference { Mode = SongTonePreference.InGameMode };
        var coord = NewCoordinator(chain, StateWithBeats(), config, ("clean", CleanJson), ("heavy", HeavyJson));
        coord.PrepareForSong("song.psarc", CleanToHeavyArrangement(), "lib:SONG");

        Assert.True(coord.BeginRun()); // the per-song pref overrides the off toggle
        Assert.True(coord.IsDriving);
        Assert.Equal(new[] { "amp", "cab", "gain" }, Order(chain));

        coord.EndRun();
    }

    [Fact]
    public void BeginRun_PerSongPrefOnOtherSong_DoesNotForceDriving()
    {
        var chain = new FakeDspChainService(Catalog());
        var config = new FakeConfigurationService();
        config.AppConfig.SongTonePreferences["lib:OTHER"] =
            new SongTonePreference { Mode = SongTonePreference.InGameMode };
        var coord = NewCoordinator(chain, StateWithBeats(), config, ("clean", CleanJson), ("heavy", HeavyJson));
        coord.PrepareForSong("song.psarc", CleanToHeavyArrangement(), "lib:SONG");

        Assert.False(coord.BeginRun()); // global toggle off + no pref for THIS song
        Assert.Empty(chain.GetNodes());
    }

    [Fact]
    public void BeginRun_PerSongMyTonePref_AppliesUserPresetAndDoesNotDrive()
    {
        var chain = new FakeDspChainService(Catalog());
        var config = new FakeConfigurationService { AppConfig = { UseInGameTones = true } };
        var presets = new FakeDspTonePresetService();
        var saved = presets.SaveUser("Mine", DspToneCategory.Clean, "", new DcRigPreset([]));
        config.AppConfig.SongTonePreferences["lib:SONG"] =
            new SongTonePreference { Mode = SongTonePreference.MyToneMode, PresetId = saved.Id };

        var coord = new RsTonePlaybackCoordinator(
            chain, new DcRigApplier(chain), StateWithBeats(), config, presets)
        {
            ToneDefinitionSource = _ => new[] { new PsarcToneDefinition("clean", "clean", CleanJson) }
        };
        coord.PrepareForSong("song.psarc", CleanToHeavyArrangement(), "lib:SONG");

        Assert.False(coord.BeginRun());              // never drives switches
        Assert.False(coord.IsDriving);
        Assert.Equal(new[] { saved.Id }, presets.Applied); // the chosen profile was applied once
        Assert.Empty(chain.GetNodes());              // no RS-tone takeover of the chain
    }

    [Fact]
    public void BeginRun_PerSongMyTonePref_UnknownPreset_DegradesToNoOp()
    {
        var chain = new FakeDspChainService(Catalog());
        var config = new FakeConfigurationService { AppConfig = { UseInGameTones = true } };
        var presets = new FakeDspTonePresetService();
        config.AppConfig.SongTonePreferences["lib:SONG"] =
            new SongTonePreference { Mode = SongTonePreference.MyToneMode, PresetId = "deleted-preset" };

        var coord = new RsTonePlaybackCoordinator(
            chain, new DcRigApplier(chain), StateWithBeats(), config, presets)
        {
            ToneDefinitionSource = _ => new[] { new PsarcToneDefinition("clean", "clean", CleanJson) }
        };
        coord.PrepareForSong("song.psarc", CleanToHeavyArrangement(), "lib:SONG");

        Assert.False(coord.BeginRun()); // preset gone → keep current rig, never throw
        Assert.False(coord.IsDriving);
        Assert.Empty(presets.Applied);
    }

    [Fact]
    public async Task CrossingAToneChange_SwitchesTheRig_ThenEndRunRestoresManualChain()
    {
        var chain = new FakeDspChainService(Catalog());
        // The player's manual rig: a single reverb pedal that must come back.
        chain.AddNode("reverb");

        var config = new FakeConfigurationService { AppConfig = { UseInGameTones = true } };
        var state = StateWithBeats();
        var coord = NewCoordinator(chain, state, config, ("clean", CleanJson), ("heavy", HeavyJson));
        coord.PrepareForSong("song.psarc", CleanToHeavyArrangement());

        using var heavyApplied = new ManualResetEventSlim(false);
        coord.ActiveToneChanged += info =>
        {
            if (info.ToneName == "heavy") heavyApplied.Set();
        };

        Assert.True(coord.BeginRun());
        Assert.Equal(new[] { "amp", "cab", "gain" }, Order(chain)); // clean applied, reverb gone

        // Advance the coalesced playhead across the ToneChange at beat 4 (=4000 ms).
        state.CurrentTimeMs = 1000;                 // still base region
        Assert.Equal(new[] { "amp", "cab", "gain" }, Order(chain));
        state.CurrentTimeMs = 4500;                 // crossed → worker applies "heavy"

        Assert.True(heavyApplied.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken),
            "the heavy tone was not applied");
        // Heavy adds a distortion in front of the shared amp/cab/gain tail.
        Assert.Equal(new[] { "ds1", "amp", "cab", "gain" }, Order(chain));

        coord.EndRun();
        Assert.False(coord.IsDriving);

        // EndRun returns without waiting on the worker or the apply gate (it runs
        // on the UI thread); the restore lands on the background finisher.
        await coord.RestoreCompletion;
        // The player's manual reverb is restored; the RS-tone nodes are gone.
        Assert.Equal(new[] { "reverb" }, Order(chain));
    }

    /// <summary>Quit, then re-enter the same song before the previous run's restore
    /// has landed. The new run must snapshot the player's MANUAL chain (not the
    /// old run's tone rig), and the superseded restore must not stomp the new
    /// run's tone.</summary>
    [Fact]
    public async Task BeginRun_WhileThePreviousRestoreIsStillPending_KeepsTheManualChainAndTheNewTone()
    {
        var chain = new FakeDspChainService(Catalog());
        chain.AddNode("reverb"); // the player's manual rig

        var config = new FakeConfigurationService { AppConfig = { UseInGameTones = true } };
        var coord = NewCoordinator(chain, StateWithBeats(), config, ("clean", CleanJson), ("heavy", HeavyJson));
        coord.PrepareForSong("song.psarc", CleanToHeavyArrangement());

        Assert.True(coord.BeginRun());
        Assert.Equal(new[] { "amp", "cab", "gain" }, Order(chain));

        coord.EndRun();            // restore queued on the finisher
        Assert.True(coord.BeginRun()); // …and the player is already back in the song

        // The new run's tone is live, not a restored manual chain.
        Assert.Equal(new[] { "amp", "cab", "gain" }, Order(chain));
        await coord.RestoreCompletion; // the superseded restore drops out
        Assert.Equal(new[] { "amp", "cab", "gain" }, Order(chain));

        // …and the manual chain it adopted is still the reverb, not the tone rig.
        coord.EndRun();
        await coord.RestoreCompletion;
        Assert.Equal(new[] { "reverb" }, Order(chain));
    }

    [Fact]
    public void MismatchedCaseAndWhitespace_ToneNames_StillMatch()
    {
        // The chart's tone references differ from the manifest keys by case +
        // stray padding — a common cause of a silent no-op. Matching must be
        // case- and whitespace-tolerant so the base tone + change still apply.
        var chain = new FakeDspChainService(Catalog());
        var config = new FakeConfigurationService { AppConfig = { UseInGameTones = true } };
        var state = StateWithBeats();
        var coord = NewCoordinator(chain, state, config, ("clean", CleanJson), ("heavy", HeavyJson));

        var arr = new Arrangement { ToneBase = "  Clean " };
        arr.ToneChanges.Add(new ToneChange { Position = new FractionalPosition(4), ToneName = "HEAVY  " });
        coord.PrepareForSong("song.psarc", arr);

        using var heavyApplied = new ManualResetEventSlim(false);
        coord.ActiveToneChanged += info =>
        {
            if (info.ToneName.Trim().Equals("heavy", StringComparison.OrdinalIgnoreCase))
                heavyApplied.Set();
        };

        Assert.True(coord.BeginRun());
        // Padded/mixed-case "  Clean " still built the clean rig.
        Assert.Equal(new[] { "amp", "cab", "gain" }, Order(chain));

        state.CurrentTimeMs = 4500; // cross the change
        Assert.True(heavyApplied.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken),
            "the padded/upper-case 'HEAVY' tone was not applied");
        Assert.Equal(new[] { "ds1", "amp", "cab", "gain" }, Order(chain));

        coord.EndRun();
    }

    // ------------------------------------------- editor tone preview (0.56)

    /// <summary>A project whose tone bodies are the project's OWN authored ones
    /// (what the Tone Designer writes and the rscp carries), not a package's.</summary>
    private static (ChartProject Project, Arrangement Arrangement) ProjectWithAuthoredTones()
    {
        var project = new ChartProject();
        for (var i = 0; i < 8; i++)
            project.Beats.Add(new BeatMarker { Time = i * 1000, IsMeasure = i % 4 == 0 });
        project.ToneDefinitions.Add(new ToneDefinition { Key = "clean", Name = "clean", Json = CleanJson });
        project.ToneDefinitions.Add(new ToneDefinition { Key = "heavy", Name = "heavy", Json = HeavyJson });

        var arr = CleanToHeavyArrangement();
        project.Arrangements.Add(arr);
        return (project, arr);
    }

    private static ProjectState StateOn(ChartProject project, Arrangement arrangement) =>
        new() { ActiveProject = project, ActiveArrangement = arrangement };

    [Fact]
    public void PrepareForProject_ReadsTheProjectsOwnToneBodies_WithNoPackageInSight()
    {
        var (project, arr) = ProjectWithAuthoredTones();
        var chain = new FakeDspChainService(Catalog());
        var state = StateOn(project, arr);
        // ToneDefinitionSource is the PACKAGE reader: if the preview went through
        // it, this would throw instead of reading the project.
        var coord = new RsTonePlaybackCoordinator(chain, new DcRigApplier(chain), state, new FakeConfigurationService())
        {
            ToneDefinitionSource = _ => throw new InvalidOperationException("the preview must not read a package")
        };

        var summary = coord.PrepareForProject(project, arr);

        Assert.True(summary.HasTones);
        Assert.Equal(2, summary.ToneCount); // base "clean" + change "heavy"
    }

    [Fact]
    public void BeginPreview_AppliesTheToneAtThePlayhead_AndEndPreviewRestoresTheManualChain()
    {
        var (project, arr) = ProjectWithAuthoredTones();
        var chain = new FakeDspChainService(Catalog());
        chain.AddNode("reverb"); // the charter's own board
        var state = StateOn(project, arr);
        var coord = new RsTonePlaybackCoordinator(chain, new DcRigApplier(chain), state, new FakeConfigurationService());
        coord.PrepareForProject(project, arr);

        Assert.True(coord.BeginPreview());
        Assert.True(coord.IsPreviewing);
        Assert.Equal(RsToneDriver.EditorPreview, coord.Driver);
        Assert.Equal(new[] { "amp", "cab", "gain" }, Order(chain));

        coord.EndPreview();
        Assert.Equal(RsToneDriver.None, coord.Driver);
    }

    [Fact]
    public void BeginPreview_WithNoNativeRig_Refuses()
    {
        var (project, arr) = ProjectWithAuthoredTones();
        var state = StateOn(project, arr);
        var chain = new NullDspChainService();
        var coord = new RsTonePlaybackCoordinator(chain, new DcRigApplier(chain), state, new FakeConfigurationService());

        Assert.False(coord.PrepareForProject(project, arr).DspAvailable);
        Assert.False(coord.BeginPreview());
        Assert.False(coord.IsDriving);
    }

    // -------------------------------------------- own charts in Play Mode (run)

    /// <summary>
    ///     THE REGRESSION. <c>PrepareForSong</c> took a psarc path, and an own
    ///     chart has none — so a chart authored in this app, with tones dialled in
    ///     the Tone Designer and tone changes placed on its timeline, played
    ///     completely dry in Play Mode. The chart editor's preview drove those
    ///     exact bodies one tab away.
    /// </summary>
    [Fact]
    public async Task AnOwnChartsAuthoredTones_DriveARunJustLikeAPackagesDo()
    {
        var (project, arr) = ProjectWithAuthoredTones();
        var chain = new FakeDspChainService(Catalog());
        chain.AddNode("reverb"); // the player's own board
        var state = StateOn(project, arr);
        var config = new FakeConfigurationService { AppConfig = { UseInGameTones = true } };
        var coord = new RsTonePlaybackCoordinator(chain, new DcRigApplier(chain), state, config)
        {
            // No package in sight: reaching for one would throw.
            ToneDefinitionSource = _ => throw new InvalidOperationException("an own chart has no package")
        };

        // No psarc path — exactly what PlayModeViewModel passes for an own chart.
        var summary = await coord.PrepareForSongAsync(null, arr, "C:/charts/mine.rscp");
        Assert.True(summary.HasTones);
        Assert.Equal(2, summary.ToneCount); // base "clean" + change "heavy"

        Assert.True(coord.BeginRun());
        Assert.Equal(["amp", "cab", "gain"], Order(chain));

        using var heavyApplied = new ManualResetEventSlim(false);
        coord.ActiveToneChanged += info =>
        {
            if (info.ToneName == "heavy") heavyApplied.Set();
        };
        state.CurrentTimeMs = 4500;
        Assert.True(heavyApplied.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken),
            "an own chart's tone change did not switch the rig");
        Assert.Equal(["ds1", "amp", "cab", "gain"], Order(chain));

        coord.EndRun();
        await coord.RestoreCompletion;
        Assert.Equal(["reverb"], Order(chain));
    }

    /// <summary>An own chart with no authored tone bodies is still "no tones" —
    /// the fallback must not invent a card for a chart that has none.</summary>
    [Fact]
    public void AnOwnChartWithNoAuthoredTones_StillReportsNoTones()
    {
        var project = new ChartProject();
        var arr = CleanToHeavyArrangement();
        project.Arrangements.Add(arr);
        var chain = new FakeDspChainService(Catalog());
        var coord = new RsTonePlaybackCoordinator(
            chain, new DcRigApplier(chain), StateOn(project, arr), new FakeConfigurationService());

        Assert.False(coord.PrepareForSong(null, arr).HasTones);
        Assert.False(coord.BeginRun());
    }

    /// <summary>A package song must keep converting from the PACKAGE even with a
    /// project open — the project's bodies are a fallback, never an override.</summary>
    [Fact]
    public void APackageSong_StillConvertsFromThePackage()
    {
        var (project, arr) = ProjectWithAuthoredTones();
        var chain = new FakeDspChainService(Catalog());
        var state = StateOn(project, arr);
        var config = new FakeConfigurationService { AppConfig = { UseInGameTones = true } };
        var coord = new RsTonePlaybackCoordinator(chain, new DcRigApplier(chain), state, config)
        {
            // The package's "clean" is the HEAVY body — if the run drove the
            // project's tones instead, the rig would come up without the pedal.
            ToneDefinitionSource = _ => [new PsarcToneDefinition("clean", "clean", HeavyJson)]
        };

        coord.PrepareForSong("song.psarc", arr);
        Assert.True(coord.BeginRun());
        Assert.Equal(["ds1", "amp", "cab", "gain"], Order(chain));

        coord.EndRun();
    }

    // -------------------------------------------------- the live tone read-out

    /// <summary>
    ///     A read-out driven only by <c>ActiveToneChanged</c> would FREEZE on the
    ///     last tone it saw and go on claiming a rig nobody drives. The stop is a
    ///     separate event because an apply of "" is not a thing.
    /// </summary>
    [Fact]
    public async Task StoppingTheRun_AnnouncesThatNothingIsDrivingAnyMore()
    {
        var (project, arr) = ProjectWithAuthoredTones();
        var chain = new FakeDspChainService(Catalog());
        var state = StateOn(project, arr);
        var coord = new RsTonePlaybackCoordinator(
            chain, new DcRigApplier(chain), state, new FakeConfigurationService());
        coord.PrepareForProject(project, arr);

        var applied = new List<string>();
        var stops = 0;
        coord.ActiveToneChanged += info => applied.Add(info.ToneName);
        coord.DrivingStopped += () => stops++;

        Assert.True(coord.BeginPreview());
        Assert.Equal(["clean"], applied);
        Assert.Equal(0, stops);

        coord.EndPreview();
        Assert.Equal(1, stops);
        await coord.RestoreCompletion;

        // Idempotent: every exit path calls the stop, and a read-out must not be
        // cleared twice as if something had changed.
        coord.EndPreview();
        Assert.Equal(1, stops);
    }

    /// <summary>A handler that throws must not take a teardown down with it — the
    /// stop runs from quit and dispose paths.</summary>
    [Fact]
    public void AThrowingStopHandler_DoesNotBreakTheTeardown()
    {
        var (project, arr) = ProjectWithAuthoredTones();
        var chain = new FakeDspChainService(Catalog());
        var state = StateOn(project, arr);
        var coord = new RsTonePlaybackCoordinator(
            chain, new DcRigApplier(chain), state, new FakeConfigurationService());
        coord.PrepareForProject(project, arr);
        coord.DrivingStopped += () => throw new InvalidOperationException("a read-out blew up");

        Assert.True(coord.BeginPreview());
        coord.EndPreview();

        Assert.False(coord.IsDriving);
        Assert.Equal(RsToneDriver.None, coord.Driver);
    }

    // ------------------------------------- live re-arm while the chart is edited

    /// <summary>A body keyed "heavy" that is no longer heavy — for proving an
    /// edited tone reaches the rig under an unchanged NAME.</summary>
    private const string HeavyJsonEditedClean =
        """{"Key":"heavy","Name":"heavy","GearList":{"Amp":{"Type":"Amps","Key":"Amp_TwinReverb","KnobValues":{}}}}""";

    /// <summary>Records every apply, in order, so a stop/start (which restores the
    /// manual chain and re-applies on top of it) is distinguishable from a schedule
    /// swap. Concurrent because <c>ActiveToneChanged</c> fires on the apply
    /// worker.</summary>
    private static System.Collections.Concurrent.ConcurrentQueue<string> RecordApplies(
        RsTonePlaybackCoordinator coord)
    {
        var applied = new System.Collections.Concurrent.ConcurrentQueue<string>();
        coord.ActiveToneChanged += info => applied.Enqueue(info.ToneName);
        return applied;
    }

    /// <summary>
    ///     Wait until <paramref name="count" /> applies have COMPLETED.
    ///     <para>
    ///         Never spin on the chain's node count instead: <c>DcRigApplier</c>
    ///         appends a new node at the tail and only then moves it into signal
    ///         order, so the count reaches its final value while the ORDER is
    ///         still wrong, and a test that waits on length reads a torn chain.
    ///         <c>ActiveToneChanged</c> fires after <c>Apply</c> returns, under
    ///         the same gate, which is the only settled moment there is.
    ///     </para>
    /// </summary>
    private static void WaitForApplies(
        System.Collections.Concurrent.ConcurrentQueue<string> applied, int count, string because)
    {
        Assert.True(
            SpinWait.SpinUntil(() => applied.Count >= count, TimeSpan.FromSeconds(2)),
            $"{because} (saw {applied.Count} of {count} applies: {string.Join(", ", applied)})");
    }

    /// <summary>
    ///     THE REGRESSION. The editor's schedule is ms-resolved once at
    ///     <c>BeginPreview</c>, so dragging an existing tone change — the gesture
    ///     the whole feature exists to make audible — used to need the toggle
    ///     switched off and back on before it was heard.
    /// </summary>
    [Fact]
    public void MovingAToneChange_IsHeardWithoutAStopAndStart()
    {
        var (project, arr) = ProjectWithAuthoredTones();
        var chain = new FakeDspChainService(Catalog());
        var state = StateOn(project, arr);
        var coord = new RsTonePlaybackCoordinator(chain, new DcRigApplier(chain), state, new FakeConfigurationService());
        coord.PrepareForProject(project, arr);

        var applied = RecordApplies(coord);

        Assert.True(coord.BeginPreview());
        state.CurrentTimeMs = 4500; // past the change at beat 4 ⇒ heavy
        WaitForApplies(applied, 2, "the tone change at beat 4 was not applied");
        Assert.Equal(["ds1", "amp", "cab", "gain"], Order(chain));

        // Drag the marker past the playhead. Nothing about the CHART's collection
        // changed — only this object's position — which is exactly the case the
        // collection watcher cannot see.
        arr.ToneChanges[0].Position = new FractionalPosition(6);
        Assert.True(coord.RefreshPreviewSchedule());

        WaitForApplies(applied, 3, "the moved tone change did not put the playhead back in the base tone");
        Assert.Equal(["amp", "cab", "gain"], Order(chain));

        // Three applies: the take-over, the crossing, the re-arm. A stop/start
        // would have restored the manual chain in between.
        Assert.Equal(["clean", "heavy", "clean"], applied.ToArray());
        Assert.Equal(RsToneDriver.EditorPreview, coord.Driver);
        coord.EndPreview();
    }

    /// <summary>A refresh that resolves to the tone already on the rig must cost
    /// nothing — this runs on every editing gesture, so a rebuild that re-applies
    /// unconditionally would put a native chain diff behind every keystroke.</summary>
    [Fact]
    public void ARefreshThatChangesNothing_QueuesNoApply()
    {
        var (project, arr) = ProjectWithAuthoredTones();
        var chain = new FakeDspChainService(Catalog());
        var state = StateOn(project, arr);
        var coord = new RsTonePlaybackCoordinator(chain, new DcRigApplier(chain), state, new FakeConfigurationService());
        coord.PrepareForProject(project, arr);

        var applied = RecordApplies(coord);
        Assert.True(coord.BeginPreview());

        // Move the marker somewhere the playhead still is not.
        arr.ToneChanges[0].Position = new FractionalPosition(6);
        Assert.False(coord.RefreshPreviewSchedule());

        Assert.Equal(["clean"], applied.ToArray());
        coord.EndPreview();
    }

    /// <summary>
    ///     An edited tone BODY under an unchanged NAME. <c>PrepareForProject</c>
    ///     publishes a NEW preset dictionary, but the apply worker reads the
    ///     reference captured at <c>BeginPreview</c> — so without the re-point an
    ///     edited tone converted perfectly and reached nothing.
    /// </summary>
    [Fact]
    public void AnEditedToneBody_ReachesTheRigUnderTheSameName()
    {
        var (project, arr) = ProjectWithAuthoredTones();
        var chain = new FakeDspChainService(Catalog());
        var state = StateOn(project, arr);
        var coord = new RsTonePlaybackCoordinator(chain, new DcRigApplier(chain), state, new FakeConfigurationService());
        coord.PrepareForProject(project, arr);
        var applied = RecordApplies(coord);

        Assert.True(coord.BeginPreview());
        state.CurrentTimeMs = 4500;
        WaitForApplies(applied, 2, "the tone change at beat 4 was not applied");

        // The Tone Designer rewrites "heavy" — same key, different gear.
        project.ToneDefinitions[1].Json = HeavyJsonEditedClean;
        coord.PrepareForProject(project, arr);
        Assert.True(coord.RefreshPreviewSchedule(reapplyCurrentTone: true));

        WaitForApplies(applied, 3, "the edited tone body never reached the rig");
        Assert.Equal(["amp", "cab", "gain"], Order(chain));
        coord.EndPreview();
    }

    /// <summary>The rig has ONE owner: a chart edited behind a Play Mode run must
    /// not re-arm that run's schedule.</summary>
    [Fact]
    public void RefreshPreviewSchedule_IsANoOpWhenThePreviewIsNotDriving()
    {
        var (project, arr) = ProjectWithAuthoredTones();
        var chain = new FakeDspChainService(Catalog());
        var state = StateOn(project, arr);
        var config = new FakeConfigurationService { AppConfig = { UseInGameTones = true } };
        var coord = new RsTonePlaybackCoordinator(chain, new DcRigApplier(chain), state, config)
        {
            ToneDefinitionSource = _ => [new PsarcToneDefinition("clean", "clean", CleanJson)]
        };
        coord.PrepareForProject(project, arr);

        // Nothing driving at all.
        Assert.False(coord.RefreshPreviewSchedule());

        coord.PrepareForSong("song.psarc", arr);
        Assert.True(coord.BeginRun());
        Assert.Equal(RsToneDriver.PlayModeRun, coord.Driver);
        Assert.False(coord.RefreshPreviewSchedule(reapplyCurrentTone: true));

        coord.EndRun();
    }

    /// <summary>
    ///     A Play Mode run OWNS the rig. Before the driver existed, <c>BeginRun</c>
    ///     opened with <c>if (IsDriving) return true</c> — so a run started while
    ///     the editor preview was live reported success, built no schedule of its
    ///     own, and played the whole song on the PREVIEW's tone timeline.
    /// </summary>
    [Fact]
    public async Task APlayModeRun_TakesTheRigOverFromALiveEditorPreview()
    {
        var (project, arr) = ProjectWithAuthoredTones();
        var chain = new FakeDspChainService(Catalog());
        var state = StateOn(project, arr);
        var config = new FakeConfigurationService { AppConfig = { UseInGameTones = true } };
        var coord = new RsTonePlaybackCoordinator(chain, new DcRigApplier(chain), state, config)
        {
            // The package's tones for the RUN: a different rig from the project's,
            // so "which schedule is live" is visible in the chain itself.
            ToneDefinitionSource = _ => new[] { new PsarcToneDefinition("heavy", "heavy", HeavyJson) }
        };

        coord.PrepareForProject(project, arr);
        Assert.True(coord.BeginPreview());
        Assert.Equal(new[] { "amp", "cab", "gain" }, Order(chain)); // the project's base tone

        // The run's arrangement names only the package's "heavy" tone.
        coord.PrepareForSong("song.psarc", new Arrangement { ToneBase = "heavy" });
        Assert.True(coord.BeginRun());

        Assert.Equal(RsToneDriver.PlayModeRun, coord.Driver);
        Assert.False(coord.IsPreviewing);
        Assert.Equal(new[] { "ds1", "amp", "cab", "gain" }, Order(chain));

        coord.EndRun();
        await coord.RestoreCompletion;
    }

    /// <summary>Play Mode's teardown paths call <c>EndRun</c> unconditionally —
    /// an editor preview is not theirs to stop.</summary>
    [Fact]
    public void EndRun_DoesNotStopAnEditorPreview()
    {
        var (project, arr) = ProjectWithAuthoredTones();
        var chain = new FakeDspChainService(Catalog());
        var state = StateOn(project, arr);
        var coord = new RsTonePlaybackCoordinator(chain, new DcRigApplier(chain), state, new FakeConfigurationService());
        coord.PrepareForProject(project, arr);
        Assert.True(coord.BeginPreview());

        coord.EndRun();

        Assert.True(coord.IsPreviewing);
        Assert.Equal(new[] { "amp", "cab", "gain" }, Order(chain));
        coord.EndPreview();
    }

    /// <summary>The two tone SOURCES are separate maps on purpose: preparing a
    /// package while a preview drives must not swap the maps under the live
    /// scanner (every later apply would silently no-op).</summary>
    [Fact]
    public async Task PreparingAPackageWhileThePreviewDrives_DoesNotBreakTheLivePreview()
    {
        var (project, arr) = ProjectWithAuthoredTones();
        var chain = new FakeDspChainService(Catalog());
        var state = StateOn(project, arr);
        var coord = new RsTonePlaybackCoordinator(chain, new DcRigApplier(chain), state, new FakeConfigurationService())
        {
            ToneDefinitionSource = _ => new[] { new PsarcToneDefinition("other", "other", CleanJson) }
        };
        coord.PrepareForProject(project, arr);

        using var heavyApplied = new ManualResetEventSlim(false);
        coord.ActiveToneChanged += info =>
        {
            if (info.ToneName == "heavy") heavyApplied.Set();
        };

        Assert.True(coord.BeginPreview());
        coord.PrepareForSong("song.psarc", new Arrangement { ToneBase = "other" });

        state.CurrentTimeMs = 4500; // the preview's own tone change
        Assert.True(heavyApplied.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken),
            "the package prepare stole the preview's converted tones");
        Assert.Equal(new[] { "ds1", "amp", "cab", "gain" }, Order(chain));

        coord.EndPreview();
        await coord.RestoreCompletion;
    }

    [Fact]
    public void ApplyingATone_PublishesItToTheGameplayToneHud_AndEndRunClearsIt()
    {
        DotCharter.Rendering.Data.GameplayToneHud.ClearRig();

        var chain = new FakeDspChainService(Catalog());
        var config = new FakeConfigurationService { AppConfig = { UseInGameTones = true } };
        var coord = NewCoordinator(chain, StateWithBeats(), config, ("clean", CleanJson), ("heavy", HeavyJson));
        coord.PrepareForSong("song.psarc", CleanToHeavyArrangement());

        Assert.True(coord.BeginRun());
        // The base tone is published to the HUD channel (proves switching fires).
        Assert.Equal("clean", DotCharter.Rendering.Data.GameplayToneHud.Rig?.Name);

        coord.EndRun();
        Assert.Null(DotCharter.Rendering.Data.GameplayToneHud.Rig);
    }

    // ---- the closed project must not stay pinned -------------------------------

    /// <summary>
    ///     The coordinator is an app-wide singleton and remembers the arrangement
    ///     the last prepare was handed. Held strongly, that pinned the whole chart
    ///     graph — levels, notes, chords, hand shapes, anchors — of every
    ///     arrangement the session ever pre-played, for the life of the process.
    ///     <para>
    ///         Structural assertion per the root <c>AGENTS.md</c> rule: whether the
    ///         closed graph is collectable, never a byte count.
    ///     </para>
    /// </summary>
    [Fact]
    public void PreparingASong_LeavesTheArrangementCollectableOnceTheProjectCloses()
    {
        var chain = new FakeDspChainService(Catalog());
        var coord = NewCoordinator(chain, StateWithBeats(), new FakeConfigurationService(),
            ("clean", CleanJson), ("heavy", HeavyJson));

        var probe = PrepareASongThenAbandonIt(coord);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(probe.IsAlive,
            "the tone coordinator is still holding the arrangement it last prepared for a run.");
    }

    /// <summary>The editor preview's arrangement is the same field one level in,
    /// and had the same bug.</summary>
    [Fact]
    public void PreparingTheEditorPreview_LeavesTheArrangementCollectableOnceTheProjectCloses()
    {
        var chain = new FakeDspChainService(Catalog());
        var coord = NewCoordinator(chain, StateWithBeats(), new FakeConfigurationService());

        var probe = PrepareThePreviewThenAbandonIt(coord);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(probe.IsAlive,
            "the tone coordinator is still holding the arrangement it last prepared for the editor preview.");
    }

    /// <summary>
    ///     …and the prepare must survive a stop, or the fix would be "satisfied" by
    ///     clearing the field in <c>EndRun</c>. <c>PlayModeViewModel.Tones.cs</c>
    ///     calls <c>EndRun</c> when the player switches 'Use in-game tones' OFF
    ///     mid-run and <c>BeginRun</c> when they switch it back ON — with no second
    ///     prepare in between. Clearing on stop would make the second BeginRun bail
    ///     with "no prepared arrangement" and the rest of the song play dry.
    /// </summary>
    [Fact]
    public async Task BeginRun_AfterAnEndRun_StillFindsThePrepare()
    {
        var chain = new FakeDspChainService(Catalog());
        var config = new FakeConfigurationService { AppConfig = { UseInGameTones = true } };
        var coord = NewCoordinator(chain, StateWithBeats(), config, ("clean", CleanJson), ("heavy", HeavyJson));
        var arrangement = CleanToHeavyArrangement();
        coord.PrepareForSong("song.psarc", arrangement);

        Assert.True(coord.BeginRun());
        coord.EndRun();
        await coord.RestoreCompletion;

        // The toggle came back on: same run, no new prepare.
        Assert.True(coord.BeginRun());
        Assert.True(coord.IsDriving);
        Assert.Equal(new[] { "amp", "cab", "gain" }, Order(chain));

        coord.EndRun();
        await coord.RestoreCompletion;

        // Keep the arrangement alive to the end of the case: with the field weak,
        // this local is the only thing rooting it.
        Assert.NotNull(arrangement);
    }

    /// <summary>Kept out of the caller's frame so the arrangement it builds cannot
    /// be held alive by a stack slot the JIT has not yet reused.</summary>
    private static WeakReference PrepareASongThenAbandonIt(RsTonePlaybackCoordinator coord)
    {
        var arrangement = CleanToHeavyArrangement();
        coord.PrepareForSong("song.psarc", arrangement);
        return new WeakReference(arrangement);
    }

    /// <summary>As above, for the editor preview's own prepare.</summary>
    private static WeakReference PrepareThePreviewThenAbandonIt(RsTonePlaybackCoordinator coord)
    {
        var (project, arrangement) = ProjectWithAuthoredTones();
        coord.PrepareForProject(project, arrangement);
        return new WeakReference(arrangement);
    }

    // ---------------------------------------------- the manual-chain snapshot

    /// <summary>
    ///     The restore has to bring back the player's board as it WAS — which
    ///     means the ABI-v22 string params and the ABI-v23 branch tag, not just
    ///     the type / knobs / bypass.
    ///     <para>
    ///         An RS tone never converts to a <c>namamp</c>, so a player whose own
    ///         board carries a NAM capture has that node REMOVED for the run and
    ///         RE-ADDED by the restore — and <c>DcRigApplier</c> only re-pushes the
    ///         strings a preset actually CARRIES. The snapshot used to drop them,
    ///         so every run handed the player back a MODELLESS namamp (a silent
    ///         amp) and a split board flattened onto branch 0, reported nowhere.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task EndRun_RestoresTheBoardsCaptureAndBranchTags_NotJustItsKnobs()
    {
        var chain = new FakeDspChainService(CatalogWithNeuralAmp());

        // The player's own board: a neural amp with a capture loaded, and a
        // reverb parked on parallel branch 2.
        const string capture = @"C:\caps\plexi-lead.nam";
        var nam = chain.AddNode("namamp")!;
        Assert.True(chain.SetNodeString(nam.NodeId, "modelPath", capture));
        var verb = chain.AddNode("reverb")!;
        Assert.True(chain.SetNodeBranch(verb.NodeId, 2));

        var config = new FakeConfigurationService { AppConfig = { UseInGameTones = true } };
        var coord = NewCoordinator(chain, StateWithBeats(), config, ("clean", CleanJson));
        coord.PrepareForSong("song.psarc", new Arrangement { ToneBase = "clean" });

        Assert.True(coord.BeginRun());
        // The run owns the rig: neither of the player's own nodes survives it.
        Assert.Equal(new[] { "amp", "cab", "gain" }, Order(chain));

        coord.EndRun();
        await coord.RestoreCompletion;

        Assert.Equal(new[] { "namamp", "reverb" }, Order(chain));
        var restoredNam = chain.GetNodes()[0];
        Assert.Equal(capture, restoredNam.StringValues["modelPath"]);
        Assert.Equal(2, chain.GetNodes()[1].Branch);
    }

    private static IReadOnlyList<DspModuleType> CatalogWithNeuralAmp() =>
        Catalog()
            .Append(new DspModuleType("namamp", "namamp",
                new[] { new DspParamInfo("inputDb", "inputDb", -20f, 20f, 0f) }))
            .ToList();
}

/// <summary>Serializes every test that reads/writes the process-wide
/// <c>GameplayToneHud</c> statics (the coordinator sets them on apply; the
/// snapshot tests read them) so parallel test classes never interfere.</summary>
[CollectionDefinition("GameplayToneHud", DisableParallelization = true)]
public sealed class GameplayToneHudCollection;
