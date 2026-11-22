using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Tone.Audio.Tonelab;

namespace DotCharter.Tests.Audio.Tonelab;

/// <summary>The session over the mock: discovery, starting → ready, a dying game, and the audio insert's open/close.</summary>
public sealed class TonelabSessionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal static TimeProvider ReplyClock => new TonelabPipeClientTests.ManualTimeProvider();

    internal static readonly TonelabTimeouts Quick = new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

    [Fact]
    public async Task ExitDuringLaunchStatus_DoesNotResurrectTheSessionOrItsLaunch()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var game = new TonelabGameMock
        {
            ReplyAsync = async (_, token) =>
            {
                entered.TrySetResult();
                await reply.Task.WaitAsync(token);
                return "ok ready v1 48000 128 0";
            }
        };
        var launcher = new FakeLauncher(game.ProcessId);
        using var session = new TonelabSession(() => [], null, null, Quick, time: ReplyClock)
        {
            Launcher = launcher,
            WaitForExit = (_, token) => exited.Task.WaitAsync(token)
        };
        var starting = session.LaunchAsync(Ct);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);
            exited.SetResult();
            await session.ExitWatch.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        }
        finally
        {
            reply.TrySetResult();
        }

        await starting.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        Assert.Equal(TonelabSessionState.Disconnected, session.State);
        Assert.Null(session.ProcessId);
        Assert.True(session.GameHoldsAudioDevice);
        await session.QuitAsync(Ct);
        Assert.False(launcher.Launch!.Ended);
    }

    [Fact]
    public async Task ADelayedReply_UsesTheInjectedClock_InsteadOfAHostDeadline()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var game = new TonelabGameMock
        {
            ReplyAsync = async (_, token) =>
            {
                entered.TrySetResult();
                await reply.Task.WaitAsync(token);
                return "ok ready v1 48000 128 0";
            }
        };
        var clock = new TonelabPipeClientTests.ManualTimeProvider();
        using var session = new TonelabSession(() => [game.ProcessId], null, null, Quick, time: clock);
        var probing = session.ProbeAsync(Ct);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);
            Assert.Equal(1, clock.PendingTimers);
            clock.Advance(TimeSpan.FromSeconds(4));
            Assert.False(probing.IsCompleted);
        }
        finally
        {
            reply.TrySetResult();
        }

        Assert.Equal(TonelabSessionState.Ready, await probing.WaitAsync(TimeSpan.FromSeconds(60), Ct));
        Assert.Equal(0, clock.PendingTimers);
    }

    [Fact]
    public async Task NoGameRunning_IsDisconnected()
    {
        using var session = new TonelabSession(() => [], null, null, Quick, time: ReplyClock);
        Assert.Equal(TonelabSessionState.Disconnected, await session.ProbeAsync(Ct));
        Assert.Null(session.ProcessId);
        Assert.Equal(TonelabOutcome.NotConnected, (await session.RestoreToneAsync(Ct)).Outcome);
    }

    [Fact]
    public async Task AStartingGame_BecomesReady_AndSaysSo()
    {
        using var game = new TonelabGameMock { Status = "starting v1" };
        using var session = new TonelabSession(() => [game.ProcessId], null, null, Quick, time: ReplyClock)
        {
            Delay = (_, token) => Task.Delay(Timeout.Infinite, token)
        };
        var changes = 0;
        session.StateChanged += (_, _) => changes++;

        Assert.Equal(TonelabSessionState.Starting, await session.ProbeAsync(Ct));
        Assert.Equal(game.ProcessId, session.ProcessId);

        game.Status = "ready v1 48000 128 0";
        Assert.Equal(TonelabSessionState.Ready, await session.ProbeAsync(Ct));
        Assert.Equal(128, session.LastStatus!.BlockFrames);
        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task AGameThatDies_DropsTheSession_AndAProbeFindsTheNextOne()
    {
        var insert = new FakeInsert();
        using var identity = TonelabMockIdentity.Reserve();
        var pid = identity.ProcessId;
        using var session = new TonelabSession(() => [pid], insert, null, Quick, time: ReplyClock);
        session.RouteAudio(Owner, true);

        using (new TonelabGameMock(pid))
        {
            Assert.Equal(TonelabSessionState.Ready, await session.ProbeAsync(Ct));
            Assert.Equal(new ExternalInsertEndpoint(@"Local\tonelab-" + pid, 48000, 128), insert.Opened);
            Assert.Equal(insert.Stats, session.AudioStats);
        }

        Assert.Equal(TonelabOutcome.NotConnected, (await session.MuteMixAsync(true, Ct)).Outcome);
        Assert.Equal(TonelabSessionState.Disconnected, session.State);
        Assert.Null(session.ProcessId);
        Assert.False(insert.IsOpen);   // the audio path never outlives the game
        Assert.Null(session.AudioStats);

        using var again = new TonelabGameMock(pid);
        Assert.Equal(TonelabSessionState.Ready, await session.ProbeAsync(Ct));
        Assert.True((await session.ShowWindowAsync(false, Ct)).IsOk);
    }

    [Fact]
    public async Task AGameClosedWhileReady_DropsTheStateTheInsertAndTheRoute_WithoutAnotherCommand()
    {
        var insert = new FakeInsert();
        using var game = new TonelabGameMock();
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var session = new TonelabSession(() => [game.ProcessId], insert, null, Quick, time: ReplyClock)
        {
            WaitForExit = (_, token) => exited.Task.WaitAsync(token)
        };
        session.RouteAudio(Owner, true);
        Assert.Equal(TonelabSessionState.Ready, await session.ProbeAsync(Ct));
        Assert.True(insert.IsOpen);
        var watch = session.ExitWatch;

        exited.SetResult();
        await watch.WaitAsync(TimeSpan.FromSeconds(60), Ct);

        Assert.Equal(TonelabSessionState.Disconnected, session.State);
        Assert.Null(session.ProcessId);
        Assert.False(insert.IsOpen);
        Assert.False(session.IsRoutingAudio);
    }

    [Fact]
    public async Task AGameOnAnotherProtocolVersion_IsNotAttached_AndSaysWhichVersion()
    {
        using var game = new TonelabGameMock { Status = "ready v2 48000 128 0" };
        using var session = new TonelabSession(() => [game.ProcessId], null, null, Quick, time: ReplyClock);

        Assert.Equal(TonelabSessionState.Disconnected, await session.ProbeAsync(Ct));
        Assert.Equal(TonelabOutcome.UnsupportedVersion, session.LastStatus!.Result.Outcome);
        Assert.Equal("v2", session.LastStatus.Result.Detail);
    }

    [Fact]
    public void SeveralGames_TheNewestIsChosen()
    {
        var started = new Dictionary<int, DateTime?>
        {
            [10] = new DateTime(2026, 10, 18, 9, 0, 0), [20] = new DateTime(2026, 10, 18, 10, 0, 0), [30] = null
        };
        Assert.Equal(20, TonelabSession.ChooseNewest([10, 20, 30], pid => started[pid]));
        Assert.Equal(30, TonelabSession.ChooseNewest([10, 30], _ => null));   // nothing readable: the higher pid
        Assert.Null(TonelabSession.ChooseNewest([], _ => null));
    }

    /// <summary>
    ///     A command whose client an attach replaced while it waited answers NotConnected; that must not
    ///     forget the REPLACEMENT game (its client and its insert), only the client the command used.
    /// </summary>
    [Fact]
    public async Task ACommandOnAReplacedClient_DoesNotForgetTheReplacement()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var older = new TonelabGameMock
        {
            ReplyAsync = async (line, token) =>
            {
                if (line == "status") return "ok ready v1 48000 128 0";
                entered.TrySetResult();
                await reply.Task.WaitAsync(token);
                return "ok";
            }
        };
        using var newer = new TonelabGameMock();
        using var session = new TonelabSession(() => [older.ProcessId], null, null, Quick, time: ReplyClock);
        Assert.Equal(TonelabSessionState.Ready, await session.ProbeAsync(Ct));

        var knob = session.SetKnobAsync("Amp", "Gain", 0.5f, Ct);
        TonelabResult result;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);
            Assert.Equal(TonelabSessionState.Ready, await session.AttachAsync(newer.ProcessId, Ct));
            result = await knob.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        }
        finally
        {
            reply.TrySetResult();
        }

        Assert.Equal(TonelabOutcome.NotConnected, result.Outcome);
        Assert.Equal(newer.ProcessId, session.ProcessId);
        Assert.Equal(TonelabSessionState.Ready, session.State);
        Assert.True((await session.MuteMixAsync(true, Ct)).IsOk);
        Assert.Contains("mix mute on", newer.Commands());
    }

    [Fact]
    public async Task Reconnect_DropsTheGame_AndAttachesTheNewestRunningOne()
    {
        using var older = new TonelabGameMock();
        using var session = new TonelabSession(() => [older.ProcessId], null, null, Quick, time: ReplyClock);
        Assert.Equal(TonelabSessionState.Ready, await session.ProbeAsync(Ct));

        using var newer = new TonelabGameMock();
        var running = new[] { older.ProcessId, newer.ProcessId };
        using var again = new TonelabSession(() => running, null, null, Quick,
            pid => pid == newer.ProcessId ? DateTime.Now : DateTime.Now.AddHours(-1), time: ReplyClock);
        Assert.Equal(TonelabSessionState.Ready, await again.ProbeAsync(Ct));
        Assert.Equal(newer.ProcessId, again.ProcessId);

        Assert.Equal(TonelabSessionState.Ready, await session.ReconnectAsync(Ct));
        Assert.Equal(older.ProcessId, session.ProcessId);
        // A NEW connection, not the old pipe (by connection of the two status lines, not by the game's accept count).
        Assert.NotEqual(older.Exchanges.First().Connection, older.Exchanges.Last().Connection);
    }

    [Fact]
    public async Task Dump_WritesTheGamesActiveTone_OrSaysThereIsNone()
    {
        using var game = new TonelabGameMock();
        using var session = new TonelabSession(() => [game.ProcessId], null, null, Quick, time: ReplyClock);
        await session.ProbeAsync(Ct);
        var path = game.TonePath + ".dump";
        var tone = path + ".tone";
        try
        {
            var none = await session.DumpToneAsync(path, Ct);
            Assert.Equal((TonelabOutcome.Rejected, "no-active-tone"), (none.Outcome, none.ErrorCode));

            await File.WriteAllTextAsync(tone, """{ "GearList": { "Amp": { "Key": "Amp_A" }, "Cabinet": { "Key": "Cab_A" } }, "Key": "t" }""", Ct);
            Assert.True((await session.ApplyToneFileAsync(tone, Ct)).IsOk);
            var dumped = await session.DumpToneAsync(path, Ct);

            Assert.Equal((TonelabOutcome.Ok, "2 live gears"), (dumped.Outcome, dumped.Detail));
            Assert.Contains("Cab_A", await File.ReadAllTextAsync(path, Ct), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
            File.Delete(tone);
        }
    }

    [Fact]
    public async Task AGameStillStarting_IsPolledWithBackoff_UntilItIsReady()
    {
        using var game = new TonelabGameMock { Status = "starting v1" };
        var waits = new List<TimeSpan>();
        using var session = new TonelabSession(() => [game.ProcessId], null, null, Quick, time: ReplyClock)
        {
            Delay = (wait, _) =>
            {
                waits.Add(wait);
                if (waits.Count == 3) game.Status = "ready v1 48000 128 -1";
                return Task.CompletedTask;
            }
        };
        var changes = new List<TonelabSessionState>();
        session.StateChanged += (_, _) => changes.Add(session.State);

        Assert.Equal(TonelabSessionState.Starting, await session.ProbeAsync(Ct));
        await session.ReadyPoll;

        Assert.Equal(TonelabSessionState.Ready, session.State);
        Assert.Equal([TonelabSessionState.Starting, TonelabSessionState.Ready], changes);
        Assert.Equal(TonelabSession.ReadyBackoff.Take(3), waits);
        Assert.True(waits[0] < waits[1] && waits[1] < waits[2], "the poll backs off");
        Assert.Equal(-1, session.LastStatus!.BlockOffset);
    }

    [Fact]
    public async Task AGameThatNeverGetsReady_GivesUpWithAReason_AfterTheLastStep_AndDoesNotPollAgainUntilReconnect()
    {
        using var game = new TonelabGameMock { Status = "starting v1" };
        var reconnect = false;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var session = new TonelabSession(() => [game.ProcessId], null, null, Quick, time: ReplyClock)
        {
            Delay = (_, token) => reconnect ? release.Task.WaitAsync(token) : Task.CompletedTask
        };

        await session.ProbeAsync(Ct);
        await session.ReadyPoll;

        Assert.Equal(TonelabSessionState.Disconnected, session.State);
        Assert.Equal(GameToneLaunchFailure.StartTimedOut, session.LastLaunchFailure);
        var asked = 1 + TonelabSession.ReadyBackoff.Count;
        Assert.Equal(asked, game.Commands().Count(c => c == "status"));

        // A probe on the same game asks once and starts no second wait: the UI does not spin.
        Assert.Equal(TonelabSessionState.Disconnected, await session.ProbeAsync(Ct));
        await session.ReadyPoll;
        Assert.Equal(asked + 1, game.Commands().Count(c => c == "status"));

        // The user's Reconnect begins a new bounded wait and clears the reason.
        reconnect = true;
        Assert.Equal(TonelabSessionState.Starting, await session.ReconnectAsync(Ct));
        Assert.Equal(GameToneLaunchFailure.None, session.LastLaunchFailure);
        release.SetResult();
        await session.ReadyPoll.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        Assert.Equal(GameToneLaunchFailure.StartTimedOut, session.LastLaunchFailure);
    }

    [Fact]
    public async Task TheLauncher_StartsTheGame_AndTheSessionAttachesToIt()
    {
        using var game = new TonelabGameMock();
        using var session = new TonelabSession(() => [], null, null, Quick, time: ReplyClock) { Launcher = new FakeLauncher(game.ProcessId) };

        Assert.True(session.CanLaunch);
        Assert.True(session.GameHoldsAudioDevice);   // before a launch, a game the user started is assumed to
        Assert.Equal(TonelabSessionState.Ready, await session.LaunchAsync(Ct));
        Assert.Equal(game.ProcessId, session.ProcessId);
        Assert.False(session.GameHoldsAudioDevice);   // --no-rsasio: the game opens no device

        using var failing = new TonelabSession(() => [], null, null, Quick, time: ReplyClock) { Launcher = new FakeLauncher(null) };
        Assert.Equal(TonelabSessionState.Disconnected, await failing.LaunchAsync(Ct));
    }

    [Fact]
    public async Task AfterALaunch_TheReadyPoll_AsksEvery250Ms_ForUpTo60s()
    {
        using var game = new TonelabGameMock { Status = "starting v1" };
        var waits = new List<TimeSpan>();
        using var session = new TonelabSession(() => [], null, null, Quick, time: ReplyClock)
        {
            Launcher = new FakeLauncher(game.ProcessId),
            Delay = (wait, _) =>
            {
                waits.Add(wait);
                if (waits.Count == 5) game.Status = "ready v1 48000 128 -1";
                return Task.CompletedTask;
            }
        };

        Assert.Equal(TonelabSessionState.Starting, await session.LaunchAsync(Ct));
        await session.ReadyPoll;

        Assert.Equal(TonelabSessionState.Ready, session.State);
        Assert.All(waits, w => Assert.Equal(TimeSpan.FromMilliseconds(250), w));
        Assert.Equal(TimeSpan.FromSeconds(60), TimeSpan.FromTicks(TonelabSession.LaunchReadyPoll.Sum(w => w.Ticks)));
    }

    [Fact]
    public async Task AFailed_LiveFolderLaunch_DoesNotFallBack_AndKeepsTheTimeoutReason()
    {
        using var stuck = new TonelabGameMock { Status = "starting v1" };
        var launcher = new FakeLauncher(stuck.ProcessId);
        using var session = new TonelabSession(() => [], null, null, Quick, time: ReplyClock) { Launcher = launcher, Delay = (_, _) => Task.CompletedTask };

        await session.LaunchAsync(Ct);
        await session.ReadyPoll;

        Assert.Equal(GameToneLaunchFailure.StartTimedOut, session.LastLaunchFailure);
        Assert.False(launcher.Launch!.Ended);
    }

    [Fact]
    public async Task AFailedLaunch_KeepsItsReason_UntilReconnect()
    {
        using var session = new TonelabSession(() => [], null, null, Quick, time: ReplyClock)
        {
            Launcher = new FakeLauncher(null) { Failure = GameToneLaunchFailure.SteamNotRunning }
        };

        Assert.Equal(TonelabSessionState.Disconnected, await session.LaunchAsync(Ct));
        Assert.Equal(GameToneLaunchFailure.SteamNotRunning, session.LastLaunchFailure);

        await session.ReconnectAsync(Ct);
        Assert.Equal(GameToneLaunchFailure.None, session.LastLaunchFailure);
    }

    [Fact]
    public async Task WithoutALauncher_ThereIsNothingToLaunch()
    {
        using var session = new TonelabSession(new FakeAppLogger(), [], null);
        Assert.False(session.CanLaunch);
        Assert.Equal(TonelabSessionState.Disconnected, await session.LaunchAsync(Ct));
    }

    [Fact]
    public async Task AttachingAlone_NeverTakesTheGuitar_OnlyRouteAudioDoes_AndUnroutingHandsItBack()
    {
        var insert = new FakeInsert();
        using var game = new TonelabGameMock();
        using var session = new TonelabSession(() => [game.ProcessId], insert, null, Quick, time: ReplyClock);

        Assert.Equal(TonelabSessionState.Ready, await session.ProbeAsync(Ct));
        Assert.Null(insert.Opened);   // the Tone Designer probing a running game must not reroute the rig

        session.RouteAudio(Owner, true);
        Assert.Equal(new ExternalInsertEndpoint(TonelabProtocol.SharedMemoryNameFor(game.ProcessId), 48000, 128), insert.Opened);
        Assert.True(insert.IsOpen);

        session.RouteAudio(Owner, false);
        Assert.False(insert.IsOpen);
        Assert.Null(session.AudioStats);
    }

    [Fact]
    public async Task TheDesignerAndTheEditor_DoNotFightOverTheRoute_TheLastOwnerToLetGoClosesIt()
    {
        var insert = new FakeInsert();
        using var game = new TonelabGameMock();
        using var session = new TonelabSession(() => [game.ProcessId], insert, null, Quick, time: ReplyClock);
        Assert.Equal(TonelabSessionState.Ready, await session.ProbeAsync(Ct));
        object designer = new(), editor = new();

        session.RouteAudio(designer, true);
        session.RouteAudio(editor, true);
        session.RouteAudio(designer, true);   // a second claim by one owner is still ONE claim
        session.RouteAudio(designer, false);
        Assert.True(insert.IsOpen);           // the editor still plays through the game

        session.RouteAudio(editor, false);
        Assert.False(insert.IsOpen);

        session.RouteAudio(editor, false);    // letting go twice is harmless
        session.RouteAudio(designer, true);
        Assert.True(insert.IsOpen);
    }

    [Fact]
    public async Task WithoutTheNativeInsert_NothingIsOpened()
    {
        var insert = new FakeInsert { IsSupported = false };
        using var game = new TonelabGameMock();
        using var session = new TonelabSession(() => [game.ProcessId], insert, null, Quick, time: ReplyClock);

        Assert.Equal(TonelabSessionState.Ready, await session.ProbeAsync(Ct));
        session.RouteAudio(Owner, true);
        Assert.Null(insert.Opened);
        Assert.Null(session.AudioStats);
    }

    [Fact]
    public async Task Quit_OfAnAttachedGame_EndsTheSession_ButNeverTheGame()
    {
        using var game = new TonelabGameMock();
        using var session = new TonelabSession(() => [game.ProcessId], null, null, Quick, time: ReplyClock);
        await session.ProbeAsync(Ct);

        Assert.True((await session.QuitAsync(Ct)).IsOk);
        Assert.Equal(TonelabSessionState.Disconnected, session.State);
        Assert.DoesNotContain("quit", game.Commands());
    }

    [Fact]
    public async Task Discovery_ReadsOnlyTonelabPipes()
    {
        using var game = new TonelabGameMock();
        using var client = new TonelabPipeClient(game.PipeName, Quick, ReplyClock);
        Assert.True((await client.StatusAsync(Ct).WaitAsync(TimeSpan.FromSeconds(60), Ct)).Result.IsOk);
        Assert.Contains(game.ProcessId, TonelabSession.DiscoverRunningGames());
    }

    [Fact]
    public async Task Quit_OfAGameTheAppStarted_SendsQuitThroughThePipe_AndHandsTheEndToTheLaunch()
    {
        using var game = new TonelabGameMock();
        var launcher = new FakeLauncher(game.ProcessId);
        using var session = new TonelabSession(() => [], null, null, Quick, time: ReplyClock) { Launcher = launcher };
        await session.LaunchAsync(Ct);

        Assert.True((await session.QuitAsync(Ct)).IsOk);
        Assert.Equal("quit", game.Commands()[^1]);
        Assert.True(launcher.Launch!.Ended);
        Assert.True(launcher.Launch.QuitAccepted);
        Assert.Equal(TonelabSessionState.Disconnected, session.State);
        Assert.True(session.GameHoldsAudioDevice);
    }

    [Fact]
    public async Task Quit_WhileTheGameIsStillStarting_SendsNoStatusAfterTheQuit()
    {
        using var game = new TonelabGameMock { Status = "starting v1", KeepServingAfterQuit = true };
        var launcher = new FakeLauncher(game.ProcessId);
        using var session = new TonelabSession(() => [], null, null, Quick, time: ReplyClock)
        {
            Launcher = launcher,
            Delay = (_, token) => Task.Delay(Timeout.Infinite, token)
        };
        Assert.Equal(TonelabSessionState.Starting, await session.LaunchAsync(Ct));

        Assert.True((await session.QuitAsync(Ct)).IsOk);

        Assert.Equal("quit", game.Commands()[^1]);
    }

    [Fact]
    public async Task Quit_WhileTheReadyPollWaits_SendsNoStatusWhenTheWaitEnds()
    {
        using var game = new TonelabGameMock { Status = "starting v1", KeepServingAfterQuit = true };
        var launcher = new FakeLauncher(game.ProcessId);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var session = new TonelabSession(() => [], null, null, Quick, time: ReplyClock)
        {
            Launcher = launcher,
            Delay = async (_, _) =>
            {
                entered.TrySetResult();
                await release.Task;
            }
        };
        Assert.Equal(TonelabSessionState.Starting, await session.LaunchAsync(Ct));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.True((await session.QuitAsync(Ct)).IsOk);
        release.SetResult();
        await session.ReadyPoll.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.Equal("quit", game.Commands()[^1]);
    }

    [Fact]
    public async Task Quit_OfAGameTheUserStarted_OnlyDetaches_AndSendsNothing()
    {
        using var game = new TonelabGameMock();
        var launcher = new FakeLauncher(game.ProcessId) { StartedByApp = false };
        using var session = new TonelabSession(() => [], null, null, Quick, time: ReplyClock) { Launcher = launcher };
        await session.LaunchAsync(Ct);
        var sent = game.Commands().Count();

        var result = await session.QuitAsync(Ct);

        Assert.True(result.IsOk);
        Assert.Equal("detached", result.Detail);
        Assert.Equal(sent, game.Commands().Count());   // no quit: the user's game keeps running
        Assert.False(launcher.Launch!.Ended);
        Assert.Equal(TonelabSessionState.Disconnected, session.State);
    }

    [Fact]
    public async Task AttachingToAGameTheUserStarted_AssumesItHoldsAnAudioDevice()
    {
        using var game = new TonelabGameMock();
        using var session = new TonelabSession(() => [game.ProcessId], null, null, Quick, time: ReplyClock);
        await session.ProbeAsync(Ct);
        Assert.True(session.GameHoldsAudioDevice);
        Assert.True((await session.SetFrameCapAsync(true, 5, Ct)).IsOk);
        Assert.Equal("perf hidden_fps 5", game.Commands()[^1]);
    }

    private sealed class FakeLauncher(int? pid) : IGameToneLauncher
    {
        public FakeLaunch? Launch { get; private set; }

        public bool StartedByApp { get; init; } = true;

        public GameToneLaunchFailure Failure { get; init; }

        public GameToneLaunchFailure LastFailure => pid is null ? Failure : GameToneLaunchFailure.None;

        public Task<IGameToneLaunch?> LaunchAsync(CancellationToken ct) =>
            Task.FromResult<IGameToneLaunch?>(pid is { } id ? Launch = new FakeLaunch(id, StartedByApp) : null);
    }

    private sealed class FakeLaunch(int pid, bool startedByApp) : IGameToneLaunch
    {
        public int ProcessId => pid;

        public bool GameOpensNoAudioDevice => true;

        public bool StartedByApp => startedByApp;

        public bool Ended { get; private set; }

        public bool QuitAccepted { get; private set; }

        public async Task EndAsync(Func<CancellationToken, Task<bool>> sendQuit, CancellationToken ct)
        {
            QuitAccepted = await sendQuit(ct);
            Ended = true;
        }
    }

    /// <summary>The route owner of the single-owner tests.</summary>
    private static readonly object Owner = new();

    internal sealed class FakeInsert : IExternalProcessInsert
    {
        public ExternalInsertStats Stats { get; } = new(256, 48000, 0, 0);

        public ExternalInsertEndpoint? Opened { get; private set; }

        public bool IsSupported { get; init; } = true;

        public bool IsOpen { get; private set; }

        public bool Open(ExternalInsertEndpoint endpoint)
        {
            Opened = endpoint;
            IsOpen = true;
            return true;
        }

        public void Close() => IsOpen = false;

        public ExternalInsertStats? ReadStats() => IsOpen ? Stats : null;
    }
}
