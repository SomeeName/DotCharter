using DotCharter.Services.Audio.Coordination;
using DotCharter.Tests.Audio.Tonelab;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Audio.Dsp.Tones;
using DotCharter.Tone.Audio.Tonelab;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Audio.Coordination;

/// <summary>
///     The editor's "Preview tones" through Rocksmith (#7), over the tonelab loopback mock: a REAL
///     <see cref="TonelabSession" /> on a mock game pipe, a fake audio insert, the real coordinator
///     and controller. Asserts what the game RECEIVES (apply-file, tone restore) and whether the
///     insert is open, never the switch's own state.
/// </summary>
public sealed class EditorRocksmithTonePreviewTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // A deadlock detector, never a budget: this class shares the CPU-saturated parallel phase.
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);
    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter-tonelab-editor-").FullName;
    private readonly ManualTonelabScheduler _scheduler = new();
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var owned in Enumerable.Reverse(_owned)) owned.Dispose();
        BestEffortDelete.Directory(_dir);
    }

    [Fact]
    public async Task CrossingAToneChange_SendsApplyFile_AndRoutesTheAudio()
    {
        using var game = new TonelabGameMock();
        var insert = new TonelabSessionTests.FakeInsert();
        var (controller, rocksmith, state) = await Build(game, insert);

        Assert.True(rocksmith.Choose(true));
        Assert.True(controller.TurnOn());
        await Flush(rocksmith);
        Assert.Contains("\"clean\"", Assert.Single(game.AppliedBodies), StringComparison.Ordinal);
        Assert.True(insert.IsOpen);   // the guitar and the chart synth now go through the game

        state.CurrentTimeMs = 5000;   // past the tone change at beat 4 (4,000 ms)
        await Flush(rocksmith);

        Assert.Equal(2, game.Commands().Count(c => c.StartsWith("tone apply-file ", StringComparison.Ordinal)));
        Assert.Contains("\"heavy\"", game.AppliedBodies.Last(), StringComparison.Ordinal);
    }

    /// <summary>
    ///     Official packages name their tones after the song code, and those keys may start with a
    ///     digit ('311allm_clean', '38spcaug_drive'). The game loads them; the authoring key rule (a
    ///     letter first, for keys DotCharter WRITES) must not keep them from the game.
    /// </summary>
    [Theory]
    [InlineData("311allm_clean", "311allm_dist")]
    [InlineData("38spcaug_drive", "38spcaug_solo")]
    public async Task AnOfficialToneKeyStartingWithADigit_ReachesTheGame_AndRoutesTheAudio(string baseKey, string changeKey)
    {
        using var game = new TonelabGameMock();
        var insert = new TonelabSessionTests.FakeInsert();
        var (controller, rocksmith, state) = await Build(game, insert, baseKey, changeKey);

        Assert.True(rocksmith.Choose(true));
        Assert.True(controller.TurnOn());
        await Flush(rocksmith);
        Assert.Contains($"\"{baseKey}\"", Assert.Single(game.AppliedBodies), StringComparison.Ordinal);
        Assert.True(insert.IsOpen);   // the guitar now goes through the game

        state.CurrentTimeMs = 5000;   // past the tone change at beat 4
        await Flush(rocksmith);
        Assert.Contains($"\"{changeKey}\"", game.AppliedBodies.Last(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheGameGate_TakesAnyGameKey_ButStillRefusesGearTheGameDoesNotHave()
    {
        var tone = RsToneAuthoring.CreateDefaultTone("clean", "clean", RsGearCatalog.Shared);

        Assert.Null(EditorRocksmithTonePreview.Validate(tone with { Key = "311allm_clean" }));
        var refused = EditorRocksmithTonePreview.Validate(tone with { Amp = tone.Amp with { GearKey = "Amp_NotInTheGame" } });
        Assert.Equal(TonelabOutcome.Invalid, refused?.Outcome);
        Assert.Equal(DotCharter.Services.Ai.Generate.RsToneFaultCodes.UnknownGear, refused?.ErrorCode);
    }

    [Fact]
    public void ARefusedTone_IsLogged_NotOnlyShownInTheTip()
    {
        var session = new FakeTonelabSession { State = TonelabSessionState.Ready };
        var logger = new FakeAppLogger();
        using var inGame = new TonelabTonePreview(session, EditorRocksmithTonePreview.Validate, new TonelabToneFiles(_dir), _scheduler, logger);
        var tone = RsToneAuthoring.CreateDefaultTone("clean", "clean", RsGearCatalog.Shared);

        inGame.Apply(tone with { Amp = tone.Amp with { GearKey = "Amp_NotInTheGame" } });

        Assert.Empty(session.AppliedFiles);
        Assert.Contains(logger.Entries, e => e.Level == "WARN" && e.Message.Contains("Amp_NotInTheGame", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TurningThePreviewOff_RestoresTheGameTone_AndUnroutes()
    {
        using var game = new TonelabGameMock();
        var insert = new TonelabSessionTests.FakeInsert();
        var (controller, rocksmith, _) = await Build(game, insert);
        rocksmith.Choose(true);
        controller.TurnOn();
        await Flush(rocksmith);
        Assert.True(insert.IsOpen);

        controller.TurnOff();
        await rocksmith.InGame.LastTask;

        Assert.Equal("tone restore", game.Commands()[^1]);
        Assert.False(insert.IsOpen);
    }

    [Fact]
    public async Task SwitchingBackToTheRig_WhileOn_RestoresAndUnroutes_ButTheRigKeepsDriving()
    {
        using var game = new TonelabGameMock();
        var insert = new TonelabSessionTests.FakeInsert();
        var (controller, rocksmith, _) = await Build(game, insert);
        rocksmith.Choose(true);
        controller.TurnOn();
        await Flush(rocksmith);

        Assert.True(rocksmith.Choose(false));
        await rocksmith.InGame.LastTask;

        Assert.Equal("tone restore", game.Commands()[^1]);
        Assert.False(insert.IsOpen);
        Assert.True(controller.IsDriving);
    }

    [Fact]
    public async Task TheDesignerAndTheEditor_DoNotFightOverTheRoute()
    {
        using var game = new TonelabGameMock();
        var insert = new TonelabSessionTests.FakeInsert();
        var (controller, rocksmith, _) = await Build(game, insert, out var session);
        // The Tone Designer's own in-game target on the same app session.
        using var designer = new TonelabTonePreview(session, EditorRocksmithTonePreview.Validate, new TonelabToneFiles(_dir), _scheduler,
            new FakeAppLogger());
        designer.Apply(RsToneAuthoring.CreateDefaultTone("designed", "designed", RsGearCatalog.Shared));
        _scheduler.Elapse();
        await designer.LastTask;
        Assert.True(insert.IsOpen);

        rocksmith.Choose(true);
        controller.TurnOn();
        await Flush(rocksmith);
        controller.TurnOff();
        await rocksmith.InGame.LastTask;
        Assert.True(insert.IsOpen);   // the editor let go; the designer still holds the route

        designer.Stop();
        await designer.LastTask;
        Assert.False(insert.IsOpen);
    }

    [Fact]
    public async Task WithNoGame_RocksmithIsDisabled_AndSaysWhy()
    {
        var session = new TonelabSession(() => [], null, null, TonelabSessionTests.Quick);
        _owned.Add(session);
        await session.ProbeAsync(Ct);
        var (_, rocksmith, _) = Controller(session);

        Assert.False(rocksmith.IsAvailable);
        Assert.Equal("Toolbar_TonePreviewRocksmith_NotConnected", rocksmith.UnavailableReasonKey);
        Assert.Contains(FakeLocalization.English()["Toolbar_TonePreviewRocksmith_NotConnected"], rocksmith.Tip, StringComparison.Ordinal);
        Assert.False(rocksmith.Choose(true));
        Assert.False(rocksmith.IsChosen);
    }

    [Fact]
    public async Task WithALauncherAndNoGame_TheSwitchStartsRocksmith_AndPicksItOnceReady()
    {
        var session = new FakeTonelabSession { CanLaunch = true, ProbeTo = TonelabSessionState.Ready };
        var (_, rocksmith, _) = Controller(session);

        Assert.True(rocksmith.CanStart);
        Assert.Equal("Toolbar_TonePreviewRocksmith_Start", rocksmith.UnavailableReasonKey);
        Assert.Contains(FakeLocalization.English()["Toolbar_TonePreviewRocksmith_Start"], rocksmith.Tip, StringComparison.Ordinal);

        await rocksmith.StartAsync();

        Assert.Equal(1, session.Launches);
        Assert.True(rocksmith.IsChosen);
        Assert.False(rocksmith.CanStart);
    }

    [Theory]
    [InlineData(GameToneLaunchFailure.SteamNotRunning, "Dialog_Tones_InGameSteamNotRunning")]
    [InlineData(GameToneLaunchFailure.StartFailed, "Dialog_Tones_InGameLaunchFailed")]
    public void AFailedStart_ShowsWhyInTheTip_AndTheSwitchCanStillRetry(GameToneLaunchFailure failure, string key)
    {
        var session = new FakeTonelabSession { CanLaunch = true, LastLaunchFailure = failure };
        var (_, rocksmith, _) = Controller(session);

        Assert.True(rocksmith.CanStart);
        Assert.Equal(key, rocksmith.UnavailableReasonKey);
        Assert.Contains(FakeLocalization.English()[key], rocksmith.Tip, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutALauncher_TheSwitchStartsNothing()
    {
        var session = new FakeTonelabSession { CanLaunch = false };
        var (_, rocksmith, _) = Controller(session);

        Assert.False(rocksmith.CanStart);
        await rocksmith.StartAsync();
        Assert.Equal(0, session.Launches);
        Assert.Equal("Toolbar_TonePreviewRocksmith_NotConnected", rocksmith.UnavailableReasonKey);
    }

    [Fact]
    public async Task AStartingGame_IsDisabled_WithTheStartingReason()
    {
        using var game = new TonelabGameMock { Status = "starting v1" };
        var session = new TonelabSession(() => [game.ProcessId], null, null, TonelabSessionTests.Quick) { Delay = (_, ct) => Task.Delay(Timeout.Infinite, ct) };
        _owned.Add(session);
        Assert.Equal(TonelabSessionState.Starting, await session.ProbeAsync(Ct));
        var (_, rocksmith, _) = Controller(session);

        Assert.False(rocksmith.Choose(true));
        Assert.Equal("Dialog_Tones_InGameStarting", rocksmith.UnavailableReasonKey);
    }

    [Fact]
    public async Task ASessionThatEnds_WhileChosen_FallsBackToTheRig_AndSaysSo()
    {
        var insert = new TonelabSessionTests.FakeInsert();
        TonelabSession session;
        EditorRocksmithTonePreview rocksmith;
        ChartTonePreviewController controller;
        using (var game = new TonelabGameMock())
        {
            (controller, rocksmith, _) = await Build(game, insert, out session);
            rocksmith.Choose(true);
            controller.TurnOn();
            await Flush(rocksmith);
            Assert.True(insert.IsOpen);
        }

        // The game is gone: the next command finds no pipe and the session drops.
        await session.MuteMixAsync(true, Ct);
        await rocksmith.InGame.LastTask;

        Assert.Equal(TonelabSessionState.Disconnected, session.State);
        Assert.False(rocksmith.IsChosen);
        Assert.False(insert.IsOpen);
        Assert.Equal(FakeLocalization.English()["Dialog_Tones_InGameEnded"], rocksmith.Notice);
        Assert.True(controller.IsDriving);   // the DotCharter rig carries on
    }

    // ---- helpers ------------------------------------------------------------

    private Task<(ChartTonePreviewController, EditorRocksmithTonePreview, ProjectState)> Build(TonelabGameMock game,
        TonelabSessionTests.FakeInsert insert) => Build(game, insert, out _);

    private Task<(ChartTonePreviewController, EditorRocksmithTonePreview, ProjectState)> Build(TonelabGameMock game,
        TonelabSessionTests.FakeInsert insert, string baseKey, string changeKey) => Build(game, insert, out _, baseKey, changeKey);

    private Task<(ChartTonePreviewController, EditorRocksmithTonePreview, ProjectState)> Build(TonelabGameMock game,
        TonelabSessionTests.FakeInsert insert, out TonelabSession session, string baseKey = "clean", string changeKey = "heavy")
    {
        var s = new TonelabSession(() => [game.ProcessId], insert, null, TonelabSessionTests.Quick);
        _owned.Add(s);
        session = s;
        return ReadyThenController(s, baseKey, changeKey);
    }

    private async Task<(ChartTonePreviewController, EditorRocksmithTonePreview, ProjectState)> ReadyThenController(TonelabSession session,
        string baseKey, string changeKey)
    {
        Assert.Equal(TonelabSessionState.Ready, await session.ProbeAsync(Ct));
        return Controller(session, baseKey, changeKey);
    }

    private (ChartTonePreviewController, EditorRocksmithTonePreview, ProjectState) Controller(ITonelabSession session,
        string baseKey = "clean", string changeKey = "heavy")
    {
        var (project, arrangement) = CleanToHeavyProject(baseKey, changeKey);
        var state = new ProjectState { ActiveProject = project, ActiveArrangement = arrangement };
        var chain = new FakeDspChainService();
        var tones = new RsTonePlaybackCoordinator(chain, new DcRigApplier(chain), state, new FakeConfigurationService());
        var logger = new FakeAppLogger();
        var inGame = new TonelabTonePreview(session, EditorRocksmithTonePreview.Validate, new TonelabToneFiles(_dir), _scheduler, logger);
        var rocksmith = new EditorRocksmithTonePreview(tones, session, FakeLocalization.English(), logger, new TestUiDispatcher(), inGame);
        var controller = new ChartTonePreviewController(tones, state, FakeLocalization.English(), null, logger, null, rocksmith);
        _owned.Add(controller);
        Assert.True(controller.IsAvailable, controller.UnavailableReasonKey);
        return (controller, rocksmith, state);
    }

    /// <summary>Waits for the coordinator's worker to hand the tone over, then fires the debounce and awaits the send.</summary>
    private async Task Flush(EditorRocksmithTonePreview rocksmith)
    {
        Assert.True(SpinWait.SpinUntil(() => _scheduler.Pending > 0, Wait), "no tone reached the in-game preview");
        _scheduler.Elapse();
        await rocksmith.InGame.LastTask;
    }

    /// <summary>Base tone <paramref name="baseKey" />, a change to <paramref name="changeKey" /> at beat 4 (4,000 ms at
    /// 60 BPM); both bodies the game accepts. The keys go in as given, as a package's manifest names them.</summary>
    private static (ChartProject, Arrangement) CleanToHeavyProject(string baseKey = "clean", string changeKey = "heavy")
    {
        var project = new ChartProject();
        for (var i = 0; i < 8; i++)
            project.Beats.Add(new BeatMarker { Time = i * 1000, IsMeasure = i % 4 == 0 });
        var clean = RsToneAuthoring.CreateDefaultTone("clean", "clean", RsGearCatalog.Shared) with { Key = baseKey, Name = baseKey };
        var heavy = RsToneAuthoring.CreateDefaultTone("heavy", "heavy", RsGearCatalog.Shared) with
        {
            Key = changeKey,
            Name = changeKey,
            VolumeDb = clean.VolumeDb - 6
        };
        project.ToneDefinitions.Add(new ToneDefinition { Key = baseKey, Name = baseKey, Json = RsToneJsonWriter.Write(clean) });
        project.ToneDefinitions.Add(new ToneDefinition { Key = changeKey, Name = changeKey, Json = RsToneJsonWriter.Write(heavy) });
        var arrangement = new Arrangement { Name = "Lead", ToneBase = baseKey };
        arrangement.ToneChanges.Add(new ToneChange { Position = new FractionalPosition(4), ToneName = changeKey });
        project.Arrangements.Add(arrangement);
        return (project, arrangement);
    }
}
