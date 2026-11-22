using DotCharter.Tone.Audio.Dsp;

namespace DotCharter.Tests.Services.Audio.Dsp;

/// <summary>
///     A node meter read that keeps failing logs one warning per node per failure episode, not one per poll.
/// </summary>
public sealed class DspMeterFailureLogTests
{
    private sealed class ThrowingMeterNative : FakeDspNativeInterop, IDspMeterNativeInterop
    {
        public bool Throw = true;
        public bool SetMetering(bool enabled) => true;
        public bool GetMetering() => true;

        public bool TryGetNodeMeter(int nodeId, out DspNodeMeter reading)
        {
            if (Throw) throw new InvalidOperationException("native meter failed");
            reading = new DspNodeMeter(.5f, .2f, .5f, .2f);
            return true;
        }
    }

    [Fact]
    public void RepeatedFailureLogsOncePerEpisode_AndAgainAfterRecovery()
    {
        var native = new ThrowingMeterNative();
        var logger = new FakeAppLogger();
        var chain = new JuceDspChainService(native, logger);
        chain.LoadCatalog();
        var node = chain.AddNode("cab")!;
        using var lease = chain.AcquireMetering();

        for (var i = 0; i < 1000; i++) Assert.False(chain.TryGetNodeMeter(node.NodeId, out _));
        Assert.Single(logger.Snapshot(), e => e.Level == "WARN" && e.Message.Contains("meter read failed"));

        native.Throw = false;
        Assert.True(chain.TryGetNodeMeter(node.NodeId, out _));
        Assert.Single(logger.Snapshot(), e => e.Level == "INFO" && e.Message.Contains("recovered"));
        native.Throw = true;
        for (var i = 0; i < 10; i++) Assert.False(chain.TryGetNodeMeter(node.NodeId, out _));
        Assert.Equal(2, logger.Snapshot().Count(e => e.Level == "WARN" && e.Message.Contains("meter read failed")));
    }
}
