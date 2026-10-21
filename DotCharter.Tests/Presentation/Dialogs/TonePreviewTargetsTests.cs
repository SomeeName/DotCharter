using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Tests.Audio.Tonelab;
using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Audio.Dsp.Tones;
using DotCharter.Tone.Audio.Tonelab;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Presentation.Dialogs;

/// <summary>
///     The Tone designer's "Preview in: DotCharter rig / Rocksmith" switch: Rocksmith is
///     never live without a ready tonelab session (and says why), a running preview moves
///     with the switch (the rig is restored, the game takes the tone), a session that ends
///     falls back to the rig, and every in-game result is one readable line.
/// </summary>
public sealed class TonePreviewTargetsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter-tonelab-vm-").FullName;
    private readonly ManualTonelabScheduler _scheduler = new();
    private readonly FakeConfigurationService _config = new();
    private readonly FakeDspChainService _chain = new([new DspModuleType("amp", "amp", [new DspParamInfo("gain", "gain", 0, 1, 0.5f)])]);

    public void Dispose() => BestEffortDelete.Directory(_dir);

    [Fact]
    public void WithoutASession_RocksmithIsDisabled_WithItsReason()
    {
        using var targets = Targets(new FakeTonelabSession());

        Assert.True(targets.IsClone);
        Assert.False(targets.CanUseInGame);
        Assert.True(targets.HasInGameUnavailableReason);
        Assert.Equal(FakeLocalization.English()["Dialog_Tones_InGameNotConnected"], targets.InGameUnavailableReason);

        targets.IsInGame = true;
        Assert.False(targets.IsInGame);
    }

    [Fact]
    public async Task Reconnect_LooksAgain_AndEnablesAGameItFinds()
    {
        var session = new FakeTonelabSession();
        using var targets = Targets(session);
        Assert.False(targets.CanUseInGame);

        session.ProbeTo = TonelabSessionState.Ready;
        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)targets.ReconnectCommand).ExecuteAsync(null);

        Assert.Equal(1, session.Reconnects);
        Assert.True(targets.CanUseInGame);
        Assert.False(targets.HasInGameUnavailableReason);
    }

    [Fact]
    public async Task WithALauncher_TheReasonOffersIt_AndLaunchAttaches()
    {
        var session = new FakeTonelabSession { CanLaunch = true };
        using var targets = Targets(session);
        Assert.True(targets.CanLaunch);
        Assert.Equal(FakeLocalization.English()["Dialog_Tones_InGameNotRunning"], targets.InGameUnavailableReason);

        session.ProbeTo = TonelabSessionState.Ready;
        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)targets.LaunchCommand).ExecuteAsync(null);

        Assert.Equal(1, session.Launches);
        Assert.True(targets.CanUseInGame);
    }

    [Fact]
    public void AFailedLaunch_PrintsItsReason_SteamNotRunningByName()
    {
        var english = FakeLocalization.English();
        var session = new FakeTonelabSession { CanLaunch = true, LastLaunchFailure = GameToneLaunchFailure.SteamNotRunning };
        using var targets = Targets(session);
        Assert.Equal(english["Dialog_Tones_InGameSteamNotRunning"], targets.InGameUnavailableReason);
        Assert.Contains("Steam is not running", targets.InGameUnavailableReason, StringComparison.Ordinal);

        session.LastLaunchFailure = GameToneLaunchFailure.InjectFailed;
        Assert.Equal(english["Dialog_Tones_InGameLaunchFailed"], targets.InGameUnavailableReason);
    }

    [Fact]
    public void WithoutALauncher_TheReasonSaysStartItThenReconnect()
    {
        using var targets = Targets(new FakeTonelabSession());
        Assert.False(targets.CanLaunch);
        Assert.Equal("In-game unavailable: start Rocksmith with tonelab, then Reconnect.", targets.InGameUnavailableReason);
    }

    [Fact]
    public void AGameOnAnotherProtocol_SaysWhichVersion()
    {
        var session = new FakeTonelabSession
        {
            LastStatus = TonelabStatus.Failed(new TonelabResult(TonelabOutcome.UnsupportedVersion, "v2"))
        };
        using var targets = Targets(session);
        Assert.Equal("Rocksmith's tonelab speaks protocol v2; DotCharter needs v1.", targets.InGameUnavailableReason);
    }

    [Theory]
    [InlineData("ASIO", true)]
    [InlineData("WASAPI", false)]
    public void ChoosingRocksmith_WhileDotCharterIsOnAsio_SaysTheGameNeedsTheDevice(string driver, bool noted)
    {
        _config.AppConfig.AudioDriver = driver;
        using var targets = Targets(new FakeTonelabSession { State = TonelabSessionState.Ready });
        Assert.False(targets.HasAsioNote);

        targets.IsInGame = true;

        Assert.Equal(noted, targets.HasAsioNote);
        if (noted) Assert.Equal(FakeLocalization.English()["Dialog_Tones_InGameAsioNote"], targets.AsioNote);
        targets.IsClone = true;
        Assert.False(targets.HasAsioNote);
    }

    [Fact]
    public void AGameLaunchedWithoutItsOwnDevice_NeedsNoAsioNote()
    {
        _config.AppConfig.AudioDriver = "ASIO";
        using var targets = Targets(new FakeTonelabSession { State = TonelabSessionState.Ready, GameHoldsAudioDevice = false });
        targets.IsInGame = true;
        Assert.False(targets.HasAsioNote);
    }

    [Fact]
    public void AStartingGame_SaysItIsStarting()
    {
        using var targets = Targets(new FakeTonelabSession { State = TonelabSessionState.Starting });
        Assert.False(targets.CanUseInGame);
        Assert.Equal(FakeLocalization.English()["Dialog_Tones_InGameStarting"], targets.InGameUnavailableReason);
    }

    [Fact]
    public async Task AReadyGame_CanBeChosen_AndAProbeThatFindsOne_EnablesIt()
    {
        var session = new FakeTonelabSession { ProbeTo = TonelabSessionState.Ready };
        using var targets = Targets(session);

        await targets.InitialProbe;
        Assert.True(targets.CanUseInGame);
        Assert.False(targets.HasInGameUnavailableReason);

        targets.IsInGame = true;
        Assert.True(targets.IsInGame);
        Assert.False(targets.IsClone);
    }

    [Fact]
    public async Task ARunningPreview_MovesWithTheSwitch()
    {
        var session = new FakeTonelabSession { State = TonelabSessionState.Ready };
        using var targets = Targets(session);
        var tone = RsToneAuthoring.CreateDefaultTone("tl", "TL", RsGearCatalog.Shared);

        targets.Apply(tone);
        Assert.True(targets.Clone.IsActive);

        targets.IsInGame = true;
        Assert.False(targets.Clone.IsActive);   // the player's rig is back
        Assert.True(targets.InGame.IsActive);
        _scheduler.Elapse();
        await targets.InGame.LastTask;
        Assert.StartsWith(_dir, Assert.Single(session.AppliedFiles), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(FakeLocalization.English()["Dialog_Tones_InGameApplied"], targets.Status);

        targets.IsClone = true;
        await targets.InGame.LastTask;
        Assert.Equal(1, session.Restores);
        Assert.True(targets.Clone.IsActive);
        Assert.False(targets.HasStatus);
    }

    [Fact]
    public void ASessionThatEnds_FallsBackToTheRig_AndSaysSo()
    {
        var session = new FakeTonelabSession { State = TonelabSessionState.Ready };
        using var targets = Targets(session);
        targets.IsInGame = true;
        targets.Apply(RsToneAuthoring.CreateDefaultTone("tl", "TL", RsGearCatalog.Shared));
        var switched = 0;
        targets.TargetChanged += (_, _) => switched++;

        session.Change(TonelabSessionState.Disconnected);

        Assert.True(targets.IsClone);
        Assert.Equal(1, switched);
        Assert.True(targets.Clone.IsActive);   // the preview goes on, on the rig
        Assert.Equal(FakeLocalization.English()["Dialog_Tones_InGameEnded"], targets.Status);
        Assert.False(targets.CanUseInGame);
    }

    [Theory]
    [InlineData(TonelabOutcome.UnknownGear, "Amp_Nope", "Rocksmith does not know the gear Amp_Nope.")]
    [InlineData(TonelabOutcome.Invalid, "knob out of range", "Not sent to Rocksmith: knob out of range")]
    [InlineData(TonelabOutcome.Timeout, "", "Rocksmith did not answer in time.")]
    [InlineData(TonelabOutcome.Malformed, "", "Rocksmith sent a reply DotCharter does not understand.")]
    [InlineData(TonelabOutcome.Rejected, "loading", "Rocksmith refused the tone: busy loading")]
    public void EveryResult_IsOneReadableLine(TonelabOutcome outcome, string detail, string expected)
    {
        using var targets = Targets(new FakeTonelabSession { State = TonelabSessionState.Ready });
        var result = new TonelabResult(outcome, detail) { ErrorCode = outcome == TonelabOutcome.Rejected ? "busy" : "" };
        Assert.Equal(expected, targets.Describe(result, null));
    }

    [Fact]
    public void WithTheAudioInsertOpen_TheLatencyIsShown()
    {
        using var targets = Targets(new FakeTonelabSession { State = TonelabSessionState.Ready });
        var line = targets.Describe(new TonelabResult(TonelabOutcome.Ok), new ExternalInsertStats(480, 48000, 0, 0));
        Assert.Equal("Playing in Rocksmith, 10 ms added latency.", line);
    }

    [Fact]
    public async Task TheDesigner_RoutesItsPreview_ThroughTheChosenTarget_AndRestoresTheGameOnClose()
    {
        var project = new ChartProject();
        project.Arrangements.Add(new Arrangement { ArrangementType = ArrangementType.Lead, Name = "Lead", ToneBase = "clean" });
        project.ToneDefinitions.Add(new ToneDefinition
        {
            Key = "clean", Name = "Clean",
            Json = RsToneJsonWriter.Write(RsToneAuthoring.CreateDefaultTone("clean", "Clean", RsGearCatalog.Shared))
        });
        var session = new FakeTonelabSession { State = TonelabSessionState.Ready };
        var targets = Targets(session);
        var logger = new FakeAppLogger();
        var history = new HistoryService(logger);
        var edit = new ChartEditDialogServices(RecordingProxy.Create<DotCharter.Services.Editing.IChartEditorSurface>().Proxy, history,
            new ProjectState { ActiveProject = project }, new FakeConfigurationService(), FakeLocalization.English(), logger);
        var vm = new ToneDesignerViewModel(edit, new FakeFileStorageService(), targets,
            new ToneDesignerTools(new DotCharter.Services.Editing.Tones.ToneGenerationRun(history, new ExportTargetWriter(), logger),
                new DotCharter.Presentation.Dialogs.DialogsSlot()), new DialogMemory(),
            DotCharter.Services.Plugins.PackageFeatures.All);

        Assert.True(vm.HasSelectedTone);
        vm.IsPreviewOn = true;
        Assert.True(targets.Clone.IsActive);

        ((DotCharter.Presentation.Contracts.Dialogs.IToneDesignerScreen)vm).PreviewTargets.IsInGame = true;
        Assert.True(vm.IsPreviewOn);
        Assert.False(targets.Clone.IsActive);
        _scheduler.Elapse();
        await targets.InGame.LastTask;
        Assert.Single(session.AppliedFiles);

        vm.Dispose();
        await targets.InGame.LastTask;
        Assert.Equal(1, session.Restores);
        Assert.False(targets.Clone.IsActive);
    }

    private TonePreviewTargets Targets(ITonelabSession session)
    {
        var clone = new TonePreviewSession(_chain, new DcRigApplier(_chain))
        {
            OffThreadRunner = action =>
            {
                action();
                return Task.CompletedTask;
            }
        };
        return FakeTonelabSession.For(clone, session, _config, _scheduler, _dir);
    }
}
