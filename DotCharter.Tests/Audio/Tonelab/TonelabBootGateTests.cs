using DotCharter.Tone.Audio.Tonelab;

namespace DotCharter.Tests.Audio.Tonelab;

/// <summary>#461: any pipe connection while the game boots hangs it, so a launch DotCharter started reads tonelab's log first.</summary>
public sealed class TonelabBootGateTests
{
    private const string LogPath = @"C:\fake\tonelab.log";
    private const string ReadyLine = "[ 9.0] ready: profile loaded\n";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Evaluate_FollowsTheLogOfThisPidOnly()
    {
        Assert.Equal(TonelabBootStage.NotLoaded, TonelabBootGate.Evaluate(null, 7));
        Assert.Equal(TonelabBootStage.Booting, TonelabBootGate.Evaluate("[ 0.1] tonelab M2 loaded into pid 7\n[ 1] boot: x\n", 7));
        Assert.Equal(TonelabBootStage.Ready, TonelabBootGate.Evaluate("[ 0.1] tonelab M2 loaded into pid 7\r\n[ 9] ready: profile loaded\r\n", 7));
        Assert.Equal(TonelabBootStage.NotLoaded, TonelabBootGate.Evaluate("[ 0.1] tonelab M2 loaded into pid 8\n[ 9] ready: profile loaded\n", 7));
        Assert.Equal(TonelabBootStage.NotLoaded, TonelabBootGate.Evaluate("[ 0.1] tonelab M2 loaded into pid 77\n[ 9] ready: profile loaded\n", 7));
        Assert.Equal(TonelabBootStage.NotLoaded,
            TonelabBootGate.Evaluate("tonelab M2 loaded into pid 7\nready: profile loaded\ntonelab M2 loaded into pid 9\n", 7));
        Assert.Equal(TonelabBootGate.LogFileName, Path.GetFileName(TonelabBootGate.LogPathFor(@"C:\x\tools\tonelab\tonelab.dll")));
    }

    [Fact]
    public async Task NoPipeCommand_BeforeTheReadyLine_ThenTheFirstStatus()
    {
        using var game = new TonelabGameMock { Status = "ready v1 48000 128 -1" };
        var clock = new FakeClock();
        var log = new FakeLog(game.ProcessId);
        var commandsWhileBooting = new List<int>();
        using var session = Session(game.ProcessId, log, clock, onDelay: () =>
        {
            commandsWhileBooting.Add(game.Commands().Count());
            if (clock.Ticks == 5) log.Text += ReadyLine;
        });

        Assert.Equal(TonelabSessionState.Starting, await session.LaunchAsync(Ct));
        await session.ReadyPoll.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.All(commandsWhileBooting.Take(5), count => Assert.Equal(0, count));
        Assert.Equal(TonelabSessionState.Ready, session.State);
        Assert.Equal("status", game.Commands()[0]);
    }

    [Fact]
    public async Task AProbe_DuringTheBoot_DoesNotConnectEither()
    {
        using var game = new TonelabGameMock { Status = "ready v1 48000 128 -1" };
        var clock = new FakeClock();
        var log = new FakeLog(game.ProcessId);
        var blocked = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        using var session = Session(game.ProcessId, log, clock, delay: async () =>
        {
            blocked.TrySetResult();
            await release.Task;
        });

        await session.LaunchAsync(Ct);
        await blocked.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.Equal(TonelabSessionState.Starting, await session.ProbeAsync(Ct));
        Assert.Empty(game.Commands());

        log.Text += ReadyLine;
        release.SetResult();
        await session.ReadyPoll.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.Equal(TonelabSessionState.Ready, session.State);
    }

    [Fact]
    public async Task ALogOfAnotherPid_IsIgnored_AndEachBootIsGivenUpAfter45s()
    {
        using var game = new TonelabGameMock { Status = "starting v1" };
        var clock = new FakeClock();
        var log = new FakeLog(game.ProcessId + 1);
        log.Text += ReadyLine;
        var launcher = new Launcher([game.ProcessId]);
        using var session = Session(launcher, log, clock);

        await session.LaunchAsync(Ct);
        await Settle(session);

        Assert.Equal(TonelabBootGate.Timeout, TimeSpan.FromSeconds(45));
        Assert.Equal(TonelabSessionState.Disconnected, session.State);
        Assert.InRange(clock.Elapsed, TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(46));
        Assert.All(game.Commands(), c => Assert.Equal("quit", c));
    }

    [Fact]
    public async Task AHungBoot_IsQuitAndLaunchedAgain_AndTheSecondAttemptGetsReady()
    {
        using var hung = new TonelabGameMock { Status = "starting v1" };
        using var good = new TonelabGameMock { Status = "ready v1 48000 128 -1" };
        var clock = new FakeClock();
        var log = new FakeLog(hung.ProcessId);
        var launcher = new Launcher([hung.ProcessId, good.ProcessId]);
        var attemptsSeen = new List<int>();
        TonelabSession? session = null;
        launcher.OnLaunch = pid =>
        {
            attemptsSeen.Add(session!.LaunchAttempt);
            if (pid == good.ProcessId) log.Text += $"[ 0.1] tonelab M2 loaded into pid {pid}\n{ReadyLine}";
        };
        session = Session(launcher, log, clock);

        await session.LaunchAsync(Ct);
        await Settle(session);

        Assert.Equal(["launch", "launch"], launcher.Calls);
        Assert.Equal([1, 2], attemptsSeen);
        Assert.Contains("quit", hung.Commands());
        Assert.Equal(TonelabSessionState.Ready, session.State);
        Assert.Equal(good.ProcessId, session.ProcessId);
        Assert.Equal(2, session.LaunchAttempt);
        Assert.Equal(GameToneLaunchFailure.None, session.LastLaunchFailure);
        Assert.True(clock.Elapsed >= TonelabBootGate.Timeout);
        Assert.All(hung.Commands(), c => Assert.Equal("quit", c));
        session.Dispose();
    }

    [Fact]
    public async Task ThreeHungBoots_GiveUpWithStartTimedOut_AfterQuittingTheFirstTwo()
    {
        using var first = new TonelabGameMock { Status = "starting v1" };
        using var second = new TonelabGameMock { Status = "starting v1" };
        using var third = new TonelabGameMock { Status = "starting v1" };
        var clock = new FakeClock();
        var log = new FakeLog(first.ProcessId);
        var launcher = new Launcher([first.ProcessId, second.ProcessId, third.ProcessId])
        {
            OnLaunch = pid => log.Text += $"[ 0.1] tonelab M2 loaded into pid {pid}\n"
        };
        using var session = Session(launcher, log, clock);

        await session.LaunchAsync(Ct);
        await Settle(session);

        Assert.Equal(["launch", "launch", "launch"], launcher.Calls);
        Assert.Equal(GameToneLaunchFailure.StartTimedOut, session.LastLaunchFailure);
        Assert.Equal(TonelabSessionState.Disconnected, session.State);
        Assert.Equal(3, session.LaunchAttempt);
        Assert.Contains("quit", first.Commands());
        Assert.Contains("quit", second.Commands());
        Assert.InRange(clock.Elapsed, TimeSpan.FromSeconds(135), TimeSpan.FromSeconds(137));
        Assert.Empty(third.Commands());
    }

    [Fact]
    public void ABootThatHangs_LeavesNoMirrorFailedMarkerBehind()
    {
        Assert.Null(typeof(TonelabSession).Assembly.GetType("DotCharter.Tone.Audio.Tonelab.MirrorFailureMarker"));
        Assert.DoesNotContain(typeof(IGameToneLauncher).GetMethods(), m => m.Name == "MirrorFailed");
    }

    /// <summary>Awaits the ready poll until no retry replaced it.</summary>
    private static async Task Settle(TonelabSession session)
    {
        Task seen;
        do
        {
            seen = session.ReadyPoll;
            await seen.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        }
        while (!ReferenceEquals(seen, session.ReadyPoll));
    }

    private static TonelabSession Session(Launcher launcher, FakeLog log, FakeClock clock) =>
        new(() => [], null, null, TonelabSessionTests.Quick, time: TonelabSessionTests.ReplyClock)
        {
            Launcher = launcher,
            Delay = (wait, _) => clock.Advance(wait),
            Clock = clock.Now,
            ReadLog = _ => log.Text
        };

    private static TonelabSession Session(int pid, FakeLog log, FakeClock clock, Action? onDelay = null, Func<Task>? delay = null) =>
        new(() => [], null, null, TonelabSessionTests.Quick, time: TonelabSessionTests.ReplyClock)
        {
            Launcher = new Launcher([pid]),
            Delay = async (wait, _) =>
            {
                await clock.Advance(wait);
                onDelay?.Invoke();
                if (delay is not null) await delay();
            },
            Clock = clock.Now,
            ReadLog = _ => log.Text
        };

    private sealed class FakeClock
    {
        private static readonly DateTime Start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private DateTime _now = Start;

        public int Ticks { get; private set; }

        public TimeSpan Elapsed => _now - Start;

        public DateTime Now() => _now;

        public Task Advance(TimeSpan wait)
        {
            _now += wait;
            Ticks++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeLog(int pid)
    {
        public string Text { get; set; } = $"[ 0.0] tonelab M2 loaded into pid {pid}\n[ 1.0] boot: native sign-in trigger armed\n";
    }

    private sealed class Launcher(IReadOnlyList<int> pids) : IGameToneLauncher
    {
        public List<string> Calls { get; } = [];

        public Action<int>? OnLaunch { get; set; }

        public Task<IGameToneLaunch?> LaunchAsync(CancellationToken ct)
        {
            Calls.Add("launch");
            if (Calls.Count > pids.Count) return Task.FromResult<IGameToneLaunch?>(null);
            var pid = pids[Calls.Count - 1];
            OnLaunch?.Invoke(pid);
            return Task.FromResult<IGameToneLaunch?>(new Launch(pid));
        }
    }

    private sealed class Launch(int pid) : IGameToneLaunch
    {
        public int ProcessId => pid;

        public bool GameOpensNoAudioDevice => true;

        public bool StartedByApp => true;

        public string? BootLogPath => LogPath;

        public async Task EndAsync(Func<CancellationToken, Task<bool>> sendQuit, CancellationToken ct) => await sendQuit(ct);
    }
}
