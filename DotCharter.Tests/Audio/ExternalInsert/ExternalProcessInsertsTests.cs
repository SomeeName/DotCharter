using DotCharter.Tone.Audio.ExternalInsert;
using JuceSharp;

namespace DotCharter.Tests.Audio.ExternalInsert;

/// <summary>
///     The managed half of the external process insert (ABI v47) on a FAKE engine:
///     what an open records, the device-rate request a rate refusal triggers, and the
///     rebuild replay (re-open on a replaced engine, leave a reconfigured one alone,
///     never request a rate under the caller's lock). The native exchange itself is
///     JuceSharp's AbiV47Tests and the RT gate.
/// </summary>
public sealed class ExternalProcessInsertsTests
{
    private const string Name = "Local\\some-block";

    private sealed class FakeEngine : IExternalInsertEngine
    {
        public int DeviceRate { get; set; } = 44100;
        public int RingRate { get; set; } = 48000;
        public bool BlockExists { get; set; } = true;
        public ExternalProcessInsertState[] State { get; } = new ExternalProcessInsertState[4];
        public List<string> Calls { get; } = [];

        public ExternalProcessInsertOpenResult Open(int player, string mappingName, string? magic)
        {
            Calls.Add($"open {player} {mappingName} {magic}");
            if (!BlockExists) return ExternalProcessInsertOpenResult.NotFound;
            if (DeviceRate != RingRate) return ExternalProcessInsertOpenResult.SampleRateMismatch;
            State[player] = ExternalProcessInsertState.Active;
            return ExternalProcessInsertOpenResult.Opened;
        }

        public bool Close(int player)
        {
            Calls.Add($"close {player}");
            State[player] = ExternalProcessInsertState.Closed;
            return true;
        }

        public bool TryGetStats(int player, out ExternalProcessInsertStats stats)
        {
            stats = new ExternalProcessInsertStats { State = State[player], SampleRate = RingRate, LatencySamples = 384 };
            return true;
        }
    }

    [Fact]
    public void AnOpenAtTheRingsRate_IsRecorded_AndCloseForgetsIt()
    {
        var engine = new FakeEngine { DeviceRate = 48000 };
        var requests = new List<int>();
        var inserts = new ExternalProcessInserts(null, () => engine, r => { requests.Add(r); return false; });

        Assert.Equal(ExternalProcessInsertOpenResult.Opened, inserts.Open(0, Name, "MAGIC", 48000));
        Assert.True(inserts.IsOpen(0));
        Assert.False(inserts.IsOpen(1));
        Assert.Empty(requests); // already at the rate: no device request
        Assert.Equal(384, inserts.ReadStats(0)!.Value.LatencySamples);

        inserts.Close(0);
        Assert.False(inserts.IsOpen(0));
        Assert.Null(inserts.ReadStats(0));
        Assert.Equal("close 0", engine.Calls[^1]);
    }

    [Fact]
    public void ARateRefusal_RequestsTheRingsRate_AndOpensAgain()
    {
        var engine = new FakeEngine();
        var inserts = new ExternalProcessInserts(null, () => engine, r => { engine.DeviceRate = r; return true; });

        Assert.Equal(ExternalProcessInsertOpenResult.Opened, inserts.Open(0, Name, null, 48000));
        Assert.Equal(48000, engine.DeviceRate);
        Assert.Equal(2, engine.Calls.Count(c => c.StartsWith("open", StringComparison.Ordinal)));
    }

    [Fact]
    public void ADeviceThatCannotRunAtTheRate_LeavesTheOpenRefused_WithTheReason()
    {
        var engine = new FakeEngine();
        var inserts = new ExternalProcessInserts(null, () => engine, _ => false);

        Assert.Equal(ExternalProcessInsertOpenResult.SampleRateMismatch, inserts.Open(0, Name, null, 48000));
        Assert.False(inserts.IsOpen(0));
        Assert.Equal(ExternalProcessInsertOpenResult.SampleRateMismatch, inserts.LastOpenResult(0));
    }

    [Fact]
    public void TheReplay_ReopensOnAReplacedEngine_AndLeavesAReconfiguredOneAlone()
    {
        var first = new FakeEngine { DeviceRate = 48000 };
        var inserts = new ExternalProcessInserts(null, () => first, _ => true);
        Assert.Equal(ExternalProcessInsertOpenResult.Opened, inserts.Open(1, Name, "MAGIC", 48000));

        // A reconfigure: the same engine still has its session — no second open.
        var opensBefore = first.Calls.Count;
        inserts.ReplayTo(first);
        Assert.Equal(opensBefore, first.Calls.Count);

        // A replaced engine starts with nothing open: the replay opens seat 1 again.
        var second = new FakeEngine { DeviceRate = 48000 };
        inserts.ReplayTo(second);
        Assert.Equal(["open 1 Local\\some-block MAGIC"], second.Calls);
        Assert.Equal(ExternalProcessInsertState.Active, second.State[1]);
    }

    [Fact]
    public void TheReplay_NeverRequestsARateInline_AndForgetsABlockThatIsGone()
    {
        var engine = new FakeEngine { DeviceRate = 48000 };
        var inlineRequests = 0;
        var replaying = false;
        using var requested = new ManualResetEventSlim();
        var inserts = new ExternalProcessInserts(null, () => engine, r =>
        {
            if (replaying) inlineRequests++;
            engine.DeviceRate = r;
            requested.Set();
            return true;
        });
        Assert.Equal(ExternalProcessInsertOpenResult.Opened, inserts.Open(0, Name, null, 48000));

        // The rebuilt engine came up at 44.1 kHz: the replay is refused for the rate and
        // must defer the request (it runs under the service's engine lock).
        var rebuilt = new FakeEngine { DeviceRate = 44100 };
        engine = rebuilt;
        replaying = true;
        inserts.ReplayTo(rebuilt);
        replaying = false;
        Assert.Equal(0, inlineRequests);
        Assert.True(requested.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken), "the deferred rate request never ran");
        Assert.True(SpinWait.SpinUntil(() => rebuilt.State[0] == ExternalProcessInsertState.Active, TimeSpan.FromSeconds(5)),
            "the seat was not re-opened at the ring's rate");

        // The other process went away: the replay forgets the seat instead of retrying forever.
        var third = new FakeEngine { DeviceRate = 48000, BlockExists = false };
        inserts.ReplayTo(third);
        Assert.False(inserts.IsOpen(0));
        Assert.Equal(ExternalProcessInsertOpenResult.NotFound, inserts.LastOpenResult(0));
    }
}
