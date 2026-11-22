using DotCharter.Tone.Audio.Tonelab;

namespace DotCharter.Tests.Audio.Tonelab;

/// <summary>
///     The command-pipe client against the loopback game mock: every command of draft v0,
///     error and malformed replies, a game that never answers, and a game that dies and
///     comes back. Results are asserted, never elapsed times.
/// </summary>
public sealed class TonelabPipeClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    ///     A bound that is not the subject of a test: a starved thread pool can stall a connect or a reply
    ///     for seconds, so only a timeout a test asserts on is ever short.
    /// </summary>
    private static readonly TimeSpan Unbounded = TimeSpan.FromSeconds(60);

    private static readonly TonelabTimeouts Patient = new(Unbounded, Unbounded, Unbounded);

    /// <summary>
    ///     A hidden game serves its pipe from a 10 frames/s main loop, so an answer can take well over a
    ///     second. The DEFAULT timeouts must wait for it: the first live run gave up after 1 s on a game
    ///     that did answer, and the abandoned exchange left the game's pipe unusable.
    /// </summary>
    [Fact]
    public async Task TheDefaultTimeouts_WaitForASlowHiddenGame()
    {
        using var game = new TonelabGameMock();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        game.ReplyAsync = async (line, token) =>
        {
            entered.TrySetResult();
            await reply.Task.WaitAsync(token);
            return line == "status" ? "ok ready v1 48000 128 -1" : "ok";
        };
        var clock = new ManualTimeProvider();
        using var client = new TonelabPipeClient(game.PipeName, time: clock);

        var pending = client.StatusAsync(Ct);
        try
        {
            await entered.Task.WaitAsync(Unbounded, Ct);
            clock.Advance(TimeSpan.FromMilliseconds(1500));
            Assert.False(pending.IsCompleted);
            Assert.Equal(1, clock.PendingTimers);
        }
        finally
        {
            reply.TrySetResult();
        }
        var status = await pending.WaitAsync(Unbounded, Ct);

        Assert.Equal(TonelabOutcome.Ok, status.Result.Outcome);
        Assert.Equal(TonelabGameState.Ready, status.State);
        Assert.Equal(-1, status.BlockOffset);
    }

    [Fact]
    public async Task EveryCommand_SendsItsLine_AndReadsItsReply()
    {
        using var game = new TonelabGameMock();
        using var client = new TonelabPipeClient(game.PipeName, Patient);
        var file = game.TonePath;
        await File.WriteAllTextAsync(file, """{ "GearList": { "Amp": { "Key": "Amp_A" } }, "Key": "my_tone" }""", Ct);
        try
        {
            var status = await client.StatusAsync(Ct);
            Assert.Equal((TonelabGameState.Ready, 1, 48000, 128), (status.State, status.Version, status.SampleRate, status.BlockFrames));

            var applied = await client.ApplyFileAsync(file, Ct);
            Assert.Equal(TonelabOutcome.Ok, applied.Outcome);
            Assert.Equal("my_tone", applied.Detail);

            Assert.True((await client.DumpAsync(file + ".dump", Ct)).IsOk);
            Assert.Contains("\"my_tone\"", await File.ReadAllTextAsync(file + ".dump", Ct), StringComparison.Ordinal);

            Assert.True((await client.KnobAsync("Amp", "Amp_A_Gain", 40f, Ct)).IsOk);
            Assert.True((await client.RestoreAsync(Ct)).IsOk);
            Assert.True((await client.MixMuteAsync(true, Ct)).IsOk);
            Assert.True((await client.MixMuteAsync(false, Ct)).IsOk);
            Assert.True((await client.WindowAsync(false, Ct)).IsOk);
            Assert.True((await client.WindowAsync(true, Ct)).IsOk);
            Assert.True((await client.PerfAsync(true, 10, Ct)).IsOk);
            Assert.True((await client.QuitAsync(Ct)).IsOk);
        }
        finally
        {
            File.Delete(file);
            File.Delete(file + ".dump");
        }

        Assert.Equal(
            ["status", "tone apply-file " + file, "tone dump " + file + ".dump", "tone knob Amp Amp_A_Gain 40", "tone restore", "mix mute on", "mix mute off",
                "window hide", "window show", "perf hidden_fps 10", "quit"],
            game.Commands());
        // One connection carried the whole exchange: the client reconnects only after a fault. Never a count of what the
        // game accepted, which a stray probe from another test host's session would raise.
        Assert.Single(game.Exchanges.Select(exchange => exchange.Connection).Distinct());
    }

    [Fact]
    public async Task AnErrReply_IsATypedResult_NotAnException()
    {
        using var game = new TonelabGameMock { Responder = _ => "err unknown-gear Amp_Nope" };
        using var client = new TonelabPipeClient(game.PipeName, Patient);

        var result = await client.ApplyFileAsync(@"C:\x.json", Ct);

        Assert.Equal(TonelabOutcome.UnknownGear, result.Outcome);
        Assert.Equal("Amp_Nope", result.Detail);
        Assert.Equal("err unknown-gear Amp_Nope", result.Raw);
    }

    [Fact]
    public async Task AMalformedReply_IsReported_AndTheNextCommandStillWorks()
    {
        var first = true;
        using var game = new TonelabGameMock();
        game.Responder = line =>
        {
            if (!first) return "ok restored";
            first = false;
            return "garbage!";
        };
        using var client = new TonelabPipeClient(game.PipeName, Patient);

        Assert.Equal(TonelabOutcome.Malformed, (await client.RestoreAsync(Ct)).Outcome);
        Assert.Equal(TonelabOutcome.Ok, (await client.RestoreAsync(Ct)).Outcome);
    }

    [Fact]
    public async Task AGameThatNeverAnswers_TimesOut_AndTheNextCommandReconnects()
    {
        var answer = false;
        using var game = new TonelabGameMock();
        game.Responder = _ => answer ? "ok applied my_tone" : null;
        // The reply deadline runs on a manual clock: the test advances it once the game HAS the command,
        // so no wall time is involved. The next command is an apply-file on the long ApplyFile bound.
        var clock = new ManualTimeProvider();
        using var client = new TonelabPipeClient(game.PipeName, new TonelabTimeouts(Unbounded, TimeSpan.FromSeconds(5), Unbounded), clock);

        var pending = client.MixMuteAsync(true, Ct);
        await WaitUntilAsync(() => clock.PendingTimers == 1 && game.Commands().Length == 1);
        clock.Advance(TimeSpan.FromSeconds(5));
        var timedOut = await pending;
        Assert.Equal(TonelabOutcome.Timeout, timedOut.Outcome);
        Assert.False(client.IsConnected);   // a late reply must never be read as the next command's

        answer = true;
        Assert.True((await client.ApplyFileAsync(@"C:\x.json", Ct)).IsOk);

        // The reconnect is proven by WHICH connection each command arrived on, never by how many the game
        // accepted: under load the game can accept a connect attempt that was abandoned before it sent a
        // byte, so the count is 2 or more (seen as 3 and 4 on a loaded machine).
        Assert.NotEqual(game.ConnectionOf("mix mute on"), game.ConnectionOf(@"tone apply-file C:\x.json"));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        // Ordering gate, not an assertion on elapsed time: bounded only by the test's cancellation.
        while (!condition()) await Task.Delay(5, Ct);
    }

    /// <summary>A clock that only moves when the test says so; its timers fire when <see cref="Advance" /> passes their due time.</summary>
    internal sealed class ManualTimeProvider : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        private TimeSpan _elapsed;

        public int PendingTimers
        {
            get
            {
                lock (_timers) return _timers.Count(t => !t.Done);
            }
        }

        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 11, 6, 0, 0, 0, TimeSpan.Zero) + _elapsed;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, _elapsed + dueTime);
            lock (_timers) _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            ManualTimer[] due;
            lock (_timers)
            {
                _elapsed += by;
                due = [.. _timers.Where(t => !t.Done && t.Due <= _elapsed)];
            }

            foreach (var timer in due) timer.Fire();
        }

        private sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan due) : ITimer
        {
            public TimeSpan Due { get; private set; } = due;

            public bool Done { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Due = dueTime;
                return true;
            }

            public void Fire()
            {
                Done = true;
                callback(state);
            }

            public void Dispose() => Done = true;

            public ValueTask DisposeAsync()
            {
                Done = true;
                return ValueTask.CompletedTask;
            }
        }
    }

    [Fact]
    public async Task NoGame_IsNotConnected()
    {
        using var identity = TonelabMockIdentity.Reserve();
        var clock = new ManualTimeProvider();
        using var client = new TonelabPipeClient(TonelabProtocol.PipeNameFor(identity.ProcessId),
            new TonelabTimeouts(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)), clock);

        var pending = client.RestoreAsync(Ct);
        await WaitUntilAsync(() => clock.PendingTimers == 1 || pending.IsCompleted);
        Assert.Equal(1, clock.PendingTimers);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal(TonelabOutcome.NotConnected, (await pending.WaitAsync(Unbounded, Ct)).Outcome);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task AGameThatDies_IsNotConnected_AndOneThatComesBack_IsReconnected()
    {
        using var identity = TonelabMockIdentity.Reserve();
        var pid = identity.ProcessId;
        using var client = new TonelabPipeClient(TonelabProtocol.PipeNameFor(pid),
            new TonelabTimeouts(Unbounded, Unbounded, Unbounded));

        using (var game = new TonelabGameMock(pid))
            Assert.True((await client.MixMuteAsync(true, Ct)).IsOk);

        Assert.Equal(TonelabOutcome.NotConnected, (await client.MixMuteAsync(true, Ct)).Outcome);

        using var again = new TonelabGameMock(pid);
        Assert.True((await client.MixMuteAsync(true, Ct)).IsOk);
        Assert.Contains("mix mute on", again.Commands());   // the new game got it: the client reconnected
    }

    [Fact]
    public async Task Cancellation_Throws_AndDropsTheConnection()
    {
        using var game = new TonelabGameMock { Responder = _ => null };
        using var client = new TonelabPipeClient(game.PipeName, Patient);
        using var cts = new CancellationTokenSource();

        var pending = client.RestoreAsync(cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(client.IsConnected);
    }

    /// <summary>
    ///     The session's exit watch disposes the client while a command is past its connect. The
    ///     command must answer NotConnected from the pipe it connected, not dereference the cleared fields
    ///     (a NullReferenceException under a loaded gate before the fix).
    /// </summary>
    [Fact]
    public async Task ADisposeDuringASend_IsNotConnected_NotANullReference()
    {
        using var game = new TonelabGameMock();
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dropped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new TonelabPipeClient(game.PipeName, Patient)
        {
            BeforeSendAsync = async token =>
            {
                connected.TrySetResult();
                await dropped.Task.WaitAsync(token);
            }
        };

        var pending = client.StatusAsync(Ct);
        try
        {
            await connected.Task.WaitAsync(Unbounded, Ct);
            client.Dispose();
        }
        finally
        {
            dropped.TrySetResult();
        }
        var status = await pending.WaitAsync(Unbounded, Ct);

        Assert.Equal(TonelabOutcome.NotConnected, status.Result.Outcome);
        Assert.False(client.IsConnected);
        Assert.Empty(game.Commands());
    }

    /// <summary>
    ///     A command queued behind one in flight when the client is disposed starts no connect: it
    ///     passed the disposed check before it waited its turn, so the turn itself must check again.
    /// </summary>
    [Fact]
    public async Task ACommandQueuedBehindADispose_NeverStartsAConnect()
    {
        using var game = new TonelabGameMock();
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new TonelabPipeClient(game.PipeName, Patient, new ManualTimeProvider())
        {
            BeforeSendAsync = async _ =>
            {
                connected.TrySetResult();
                await released.Task;
            }
        };

        var first = client.StatusAsync(Ct);
        await connected.Task.WaitAsync(Unbounded, Ct);
        var queued = client.RestoreAsync(Ct);
        Assert.Equal(1, client.ConnectAttempts);
        client.Dispose();
        released.TrySetResult();

        Assert.Equal(TonelabOutcome.NotConnected, (await first.WaitAsync(Unbounded, Ct)).Result.Outcome);
        Assert.Equal(TonelabOutcome.NotConnected, (await queued.WaitAsync(Unbounded, Ct)).Outcome);
        Assert.Equal(1, client.ConnectAttempts);
        Assert.False(client.IsConnected);
    }

    /// <summary>
    ///     A dispose while the reply is pending ends the read at once, and closing the pipe under that read
    ///     is NotConnected, not a Timeout: the injected clock never moves, so no deadline could have fired.
    /// </summary>
    [Fact]
    public async Task ADisposeDuringAPendingRead_IsNotConnected_NotATimeout()
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
        using var client = new TonelabPipeClient(game.PipeName, Patient, new ManualTimeProvider());

        var pending = client.StatusAsync(Ct);
        TonelabStatus status;
        try
        {
            await entered.Task.WaitAsync(Unbounded, Ct);
            client.Dispose();
            status = await pending.WaitAsync(Unbounded, Ct);
        }
        finally
        {
            reply.TrySetResult();
        }

        Assert.Equal(TonelabOutcome.NotConnected, status.Result.Outcome);
        Assert.False(client.IsConnected);
    }

    /// <summary>A dispose while no game answers the connect ends it at once instead of waiting out the connect deadline.</summary>
    [Fact]
    public async Task ADisposeDuringAPendingConnect_EndsItAsNotConnected()
    {
        using var identity = TonelabMockIdentity.Reserve();
        using var client = new TonelabPipeClient(TonelabProtocol.PipeNameFor(identity.ProcessId), Patient, new ManualTimeProvider());

        var pending = client.StatusAsync(Ct);
        Assert.Equal(1, client.ConnectAttempts);
        client.Dispose();

        Assert.Equal(TonelabOutcome.NotConnected, (await pending.WaitAsync(Unbounded, Ct)).Result.Outcome);
        Assert.False(client.IsConnected);
    }
}
