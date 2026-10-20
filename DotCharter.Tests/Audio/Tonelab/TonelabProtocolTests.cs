using DotCharter.Tone.Audio.Tonelab;

namespace DotCharter.Tests.Audio.Tonelab;

/// <summary>The tonelab wire format (draft v0), pure: command lines out, typed results in.</summary>
public sealed class TonelabProtocolTests
{
    [Fact]
    public void Commands_AreTheDraftsLines()
    {
        Assert.Equal("status", TonelabProtocol.Status());
        Assert.Equal(@"tone apply-file C:\Users\A B\tone 1.json", TonelabProtocol.ApplyFile(@"C:\Users\A B\tone 1.json"));
        Assert.Equal("tone knob Amp Amp_X_Gain 62.5", TonelabProtocol.Knob("Amp", "Amp_X_Gain", 62.5f));
        Assert.Equal("tone restore", TonelabProtocol.Restore());
        Assert.Equal("mix mute on", TonelabProtocol.MixMute(true));
        Assert.Equal("mix mute off", TonelabProtocol.MixMute(false));
        Assert.Equal("window hide", TonelabProtocol.Window(false));
        Assert.Equal("window show", TonelabProtocol.Window(true));
        Assert.Equal("quit", TonelabProtocol.Quit());
        Assert.Equal("tonelab-4242", TonelabProtocol.PipeNameFor(4242));
        Assert.Equal(@"Local\tonelab-4242", TonelabProtocol.SharedMemoryNameFor(4242));
    }

    [Theory]
    [InlineData("a\nquit")]
    [InlineData("a\rb")]
    public void ALineBreakInAnArgument_IsNeverSent(string path) =>
        Assert.Throws<ArgumentException>(() => TonelabProtocol.ApplyFile(path));

    [Fact]
    public void ASpaceInAKnobKey_IsNeverSent() =>
        Assert.Throws<ArgumentException>(() => TonelabProtocol.Knob("Amp", "Amp X", 1f));

    [Fact]
    public void Ok_CarriesItsPayload_AndChecksItsVerb()
    {
        var applied = TonelabProtocol.Parse("ok applied my_tone", "applied");
        Assert.Equal(TonelabOutcome.Ok, applied.Outcome);
        Assert.Equal("my_tone", applied.Detail);

        Assert.Equal(TonelabOutcome.Malformed, TonelabProtocol.Parse("ok restored", "applied").Outcome);
        Assert.Equal(TonelabOutcome.Malformed, TonelabProtocol.Parse("ok", "restored").Outcome);
        Assert.True(TonelabProtocol.Parse("ok\r").IsOk);
    }

    [Fact]
    public void Err_IsTyped_UnknownGearByItsOwnOutcome()
    {
        var gear = TonelabProtocol.Parse("err unknown-gear Amp_Nope", "applied");
        Assert.Equal(TonelabOutcome.UnknownGear, gear.Outcome);
        Assert.Equal("unknown-gear", gear.ErrorCode);
        Assert.Equal("Amp_Nope", gear.Detail);

        var other = TonelabProtocol.Parse("err busy loading profile");
        Assert.Equal(TonelabOutcome.Rejected, other.Outcome);
        Assert.Equal("busy", other.ErrorCode);
        Assert.Equal("loading profile", other.Detail);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("OK")]
    [InlineData("yes applied x")]
    [InlineData("err")]
    public void AnythingElse_IsMalformed(string line) =>
        Assert.Equal(TonelabOutcome.Malformed, TonelabProtocol.Parse(line).Outcome);

    [Fact]
    public void NoLine_MeansThePipeClosed() =>
        Assert.Equal(TonelabOutcome.NotConnected, TonelabProtocol.Parse(null).Outcome);

    [Fact]
    public void Status_IsV1_AsTheGameSendsIt()
    {
        var ready = TonelabProtocol.ParseStatus("ok ready v1 48000 128 0");
        Assert.Equal((TonelabGameState.Ready, 1, 48000, 128, 0), (ready.State, ready.Version, ready.SampleRate, ready.BlockFrames, ready.BlockOffset));
        Assert.Equal(3, TonelabProtocol.ParseStatus("ok ready v1 48000 128 3").BlockOffset);
        Assert.Equal(-1, TonelabProtocol.ParseStatus("ok ready v1 48000 128 -1").BlockOffset);   // not measured yet
        Assert.Equal(TonelabGameState.Starting, TonelabProtocol.ParseStatus("ok starting v1").State);
        Assert.Equal(TonelabGameState.Starting, TonelabProtocol.ParseStatus("ok starting").State);   // M2, before the version
        Assert.Equal(TonelabOutcome.Rejected, TonelabProtocol.ParseStatus("err no-instance").Result.Outcome);
    }

    [Theory]
    [InlineData("ok ready v2 48000 128 0")]
    [InlineData("ok starting v2")]
    public void Status_AnotherVersion_IsRefusedByName(string line)
    {
        var v2 = TonelabProtocol.ParseStatus(line);
        Assert.Equal(TonelabOutcome.UnsupportedVersion, v2.Result.Outcome);
        Assert.Equal("v2", v2.Result.Detail);
        Assert.Equal(TonelabGameState.Unknown, v2.State);
    }

    [Theory]
    [InlineData("ok ready 48000 256")]   // draft v0: no version
    [InlineData("ok ready v1 48000 128")]
    [InlineData("ok ready v1 48000 128 0 extra")]
    [InlineData("ok ready v1 0 128 0")]
    [InlineData("ok ready v1 48000 fast 0")]
    [InlineData("ok ready v1 48000 128 -2")]
    [InlineData("ok ready vx 48000 128 0")]
    [InlineData("ok starting now")]
    public void Status_AnyOtherShape_IsMalformed(string line) =>
        Assert.Equal(TonelabOutcome.Malformed, TonelabProtocol.ParseStatus(line).Result.Outcome);

    [Fact]
    public void Perf_CapsTheMainLoop()
    {
        Assert.Equal("perf hidden_fps 5", TonelabProtocol.Perf(true, 5));
        Assert.Equal("perf visible_fps 0", TonelabProtocol.Perf(false, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => TonelabProtocol.Perf(true, -1));
    }

    [Fact]
    public void TheGamesOwnTimeout_IsATimeout() =>
        Assert.Equal(TonelabOutcome.Timeout, TonelabProtocol.Parse("err timeout tone tick").Outcome);

    [Fact]
    public void Dump_NamesTheFileTheGameWrites() =>
        Assert.Equal(@"tone dump C:\Users\A B\dump.json", TonelabProtocol.Dump(@"C:\Users\A B\dump.json"));

    [Theory]
    [InlineData("tonelab-1234", 1234)]
    [InlineData("tonelab-", null)]
    [InlineData("tonelab-12a", null)]
    [InlineData("tonelab--1", null)]
    [InlineData("other-1234", null)]
    public void APipeName_NamesItsProcess(string name, int? pid) => Assert.Equal(pid, TonelabProtocol.ProcessIdOf(name));
}
