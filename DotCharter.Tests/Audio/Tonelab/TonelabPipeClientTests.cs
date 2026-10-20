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

    private static readonly TonelabTimeouts Quick = new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

    /// <summary>
    ///     A hidden game serves its pipe from a 10 frames/s main loop, so an answer can take well over a
    ///     second. The DEFAULT timeouts must wait for it: the first live run gave up after 1 s on a game
    ///     that did answer, and the abandoned exchange left the game's pipe unusable.
    /// </summary>
    [Fact]
    public async Task TheDefaultTimeouts_WaitForASlowHiddenGame()
    {
        using var game = new TonelabGameMock();
        game.Responder = line =>
        {
            Thread.Sleep(1500);
            return line == "status" ? "ok ready v1 48000 128 -1" : "ok";
        };
        using var client = new TonelabPipeClient(game.PipeName);

        var status = await client.StatusAsync(Ct);

        Assert.Equal(TonelabOutcome.Ok, status.Result.Outcome);
        Assert.Equal(TonelabGameState.Ready, status.State);
        Assert.Equal(-1, status.BlockOffset);
    }

    [Fact]
    public async Task EveryCommand_SendsItsLine_AndReadsItsReply()
    {
        using var game = new TonelabGameMock();
        using var client = new TonelabPipeClient(game.PipeName, Quick);
        var file = Path.Combine(Path.GetTempPath(), $"tonelab-client-{game.ProcessId}.json");
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
        Assert.Equal(1, game.Connections);
    }

    [Fact]
    public async Task AnErrReply_IsATypedResult_NotAnException()
    {
        using var game = new TonelabGameMock { Responder = _ => "err unknown-gear Amp_Nope" };
        using var client = new TonelabPipeClient(game.PipeName, Quick);

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
        using var client = new TonelabPipeClient(game.PipeName, Quick);

        Assert.Equal(TonelabOutcome.Malformed, (await client.RestoreAsync(Ct)).Outcome);
        Assert.Equal(TonelabOutcome.Ok, (await client.RestoreAsync(Ct)).Outcome);
    }

    [Fact]
    public async Task AGameThatNeverAnswers_TimesOut_AndTheNextCommandReconnects()
    {
        var answer = false;
        using var game = new TonelabGameMock();
        game.Responder = _ => answer ? "ok" : null;
        using var client = new TonelabPipeClient(game.PipeName,
            new TonelabTimeouts(TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(5)));

        var timedOut = await client.MixMuteAsync(true, Ct);
        Assert.Equal(TonelabOutcome.Timeout, timedOut.Outcome);
        Assert.False(client.IsConnected);   // a late reply must never be read as the next command's

        answer = true;
        Assert.True((await client.MixMuteAsync(true, Ct)).IsOk);
        Assert.Equal(2, game.Connections);
    }

    [Fact]
    public async Task NoGame_IsNotConnected()
    {
        using var client = new TonelabPipeClient(TonelabProtocol.PipeNameFor(1_999_999_001),
            new TonelabTimeouts(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)));

        Assert.Equal(TonelabOutcome.NotConnected, (await client.RestoreAsync(Ct)).Outcome);
        Assert.Equal(TonelabOutcome.NotConnected, (await client.StatusAsync(Ct)).Result.Outcome);
    }

    [Fact]
    public async Task AGameThatDies_IsNotConnected_AndOneThatComesBack_IsReconnected()
    {
        var pid = 1_999_999_002;
        using var client = new TonelabPipeClient(TonelabProtocol.PipeNameFor(pid),
            new TonelabTimeouts(TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)));

        using (var game = new TonelabGameMock(pid))
            Assert.True((await client.MixMuteAsync(true, Ct)).IsOk);

        Assert.Equal(TonelabOutcome.NotConnected, (await client.MixMuteAsync(true, Ct)).Outcome);

        using var again = new TonelabGameMock(pid);
        Assert.True((await client.MixMuteAsync(true, Ct)).IsOk);
        Assert.Equal(1, again.Connections);
    }

    [Fact]
    public async Task Cancellation_Throws_AndDropsTheConnection()
    {
        using var game = new TonelabGameMock { Responder = _ => null };
        using var client = new TonelabPipeClient(game.PipeName, Quick);
        using var cts = new CancellationTokenSource();

        var pending = client.RestoreAsync(cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(client.IsConnected);
    }
}
