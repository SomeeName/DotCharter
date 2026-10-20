using DotCharter.Tone.Audio.Dsp;

namespace DotCharter.Tests.Services.Audio.Dsp;

public sealed class DspMeterSourceTests
{
    private sealed class MeterNative : FakeDspNativeInterop, IDspMeterNativeInterop
    {
        public bool Enabled, Reject, MissingEntryPoint;
        public int Reads;
        public readonly List<(nint Engine, int Player, bool Enabled)> Enables = [];
        public DspNodeMeter Reading = new(.8f, .4f, 1.5f, .7f);
        public bool SetMetering(bool enabled)
        {
            if (MissingEntryPoint) throw new EntryPointNotFoundException();
            Enables.Add((EngineHandle, Player, enabled));
            if (Reject) return false;
            Enabled = enabled;
            return true;
        }
        public bool GetMetering() => Enabled;
        public bool TryGetNodeMeter(int nodeId, out DspNodeMeter reading)
        { Reads++; reading = Reading; return !Reject; }
    }
    [Fact]
    public void TwoHostsHoldIndependentIdempotentLeases_AndFinalReleaseDisables()
    {
        var native = new MeterNative { Player = 2 };
        var chain = new JuceDspChainService(native);
        chain.LoadCatalog();
        var node = chain.AddNode("cab")!;
        var a = chain.AcquireMetering(); var b = chain.AcquireMetering();
        Assert.True(chain.IsMeteringActive);
        Assert.Single(native.Enables);
        Assert.Equal(2, native.Enables[0].Player);
        a.Dispose(); a.Dispose();
        Assert.True(chain.TryGetNodeMeter(node.NodeId, out var meter));
        Assert.Equal(1.5f, meter.OutputPeak); // Never clamp valid over-unity peaks.
        b.Dispose();
        Assert.False(chain.IsMeteringActive);
        Assert.False(chain.TryGetNodeMeter(node.NodeId, out _));
        Assert.Equal(2, native.Enables.Count);
        Assert.False(native.Enables[1].Enabled);
    }
    [Fact]
    public void LeaseAcquiredBeforeAttachReplaysOnRebuild_AndRejectsStaleOrForeignIds()
    {
        var native = new MeterNative { EngineHandle = 0, Player = 1 };
        var chain = new JuceDspChainService(native); chain.LoadCatalog();
        using var lease = chain.AcquireMetering();
        Assert.False(chain.IsMeteringActive);
        chain.AttachEngine(123);
        var old = chain.AddNode("cab")!;
        Assert.True(chain.TryGetNodeMeter(old.NodeId, out _));
        chain.AttachEngine(456);
        Assert.True(chain.IsMeteringActive);
        Assert.Equal((456, 1, true), native.Enables.Last());
        Assert.False(chain.TryGetNodeMeter(old.NodeId, out _));
        Assert.False(chain.TryGetNodeMeter(9999, out _));
        Assert.Equal(1, native.Reads);
        chain.AttachEngine(0);
        Assert.False(chain.IsMeteringActive);
    }
    [Fact]
    public void MissingNativeEntryPointDoesNotThrowOrClaimActive()
    {
        var chain = new JuceDspChainService(new MeterNative { MissingEntryPoint = true });
        using var lease = chain.AcquireMetering();
        Assert.False(chain.IsMeteringActive);
        Assert.False(chain.TryGetNodeMeter(1, out _));
    }
    [Fact]
    public void UnsupportedOrRejectedMeteringIsUnavailable_AndBadReadNeverLeaksValues()
    {
        var unsupported = new JuceDspChainService(new FakeDspNativeInterop());
        using var unused = unsupported.AcquireMetering();
        Assert.False(unsupported.IsMeteringActive);
        var native = new MeterNative { Reject = true };
        var chain = new JuceDspChainService(native); chain.LoadCatalog();
        var node = chain.AddNode("cab")!;
        using var first = chain.AcquireMetering();
        Assert.False(chain.IsMeteringActive);
        Assert.False(chain.TryGetNodeMeter(node.NodeId, out _));
        native.Reject = false;
        using var second = chain.AcquireMetering();
        Assert.True(chain.IsMeteringActive);
        native.Reading = new(float.NaN, 0, 0, 0);
        Assert.False(chain.TryGetNodeMeter(node.NodeId, out var invalid));
        Assert.Equal(default, invalid);
        native.Reading = new(1, .5f, 2, 1);
        native.Reject = true;
        Assert.False(chain.TryGetNodeMeter(node.NodeId, out var rejected));
        Assert.Equal(default, rejected);
    }
}
