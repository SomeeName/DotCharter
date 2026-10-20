using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI;

public sealed class RigNodeMetersTests
{
    private sealed class Source : IDspMeterSource
    {
        public int Owners, LastNode;
        public bool Ready = true, Readable = true;
        public DspNodeMeter Reading = new(.5f, .2f, 2f, 1f);
        public bool IsMeteringActive => Ready && Owners > 0;
        public IDisposable AcquireMetering() { Owners++; return new Lease(this); }
        public bool TryGetNodeMeter(int nodeId, out DspNodeMeter meter)
        { LastNode = nodeId; meter = Reading; return Readable; }
        private sealed class Lease(Source source) : IDisposable
        {
            private bool _disposed;
            public void Dispose() { if (!_disposed) { _disposed = true; source.Owners--; } }
        }
    }

    [Fact]
    public void TwoVisibleHostsShareLease_AndSeatChangesReleaseTheOldSource()
    {
        var first = new Source(); var second = new Source();
        var source = first; int? node = 7;
        using var vm = new RigNodeMetersViewModel(() => source, () => node);
        var a = new object(); var b = new object();
        vm.Poll(); Assert.Equal(0, first.Owners);
        vm.SetSurfaceLive(a, true); vm.SetSurfaceLive(a, true); vm.SetSurfaceLive(b, true);
        Assert.Equal(1, first.Owners);
        Assert.Equal(7, first.LastNode);
        Assert.Equal(-6.0206, vm.InputDb, 4);
        Assert.Equal(6.0206, vm.OutputDb, 4); // Positive dBFS is not silently clipped.
        vm.SetSurfaceLive(a, false); Assert.Equal(1, first.Owners);
        source = second; node = 12; vm.Poll();
        Assert.Equal(0, first.Owners); Assert.Equal(1, second.Owners);
        Assert.Equal(12, second.LastNode);
        vm.SetSurfaceLive(b, false);
        Assert.Equal(0, second.Owners); Assert.False(vm.HasReading);
        Assert.Equal(double.NegativeInfinity, vm.OutputDb);
    }

    [Fact]
    public void RebuildSilenceInvalidAndMissingReadingsDoNotLeaveStaleLevels()
    {
        var source = new Source { Ready = false }; int? node = 1;
        using var vm = new RigNodeMetersViewModel(() => source, () => node);
        vm.SetSurfaceLive(this, true);
        Assert.False(vm.HasReading);
        source.Ready = true; vm.Poll(); Assert.True(vm.HasReading);
        source.Reading = default; vm.Poll();
        Assert.True(vm.HasReading); Assert.Equal(double.NegativeInfinity, vm.InputDb);
        source.Reading = new(float.NaN, 0, 1, 0); vm.Poll(); Assert.False(vm.HasReading);
        source.Reading = new(1, .5f, 1, .5f); vm.Poll(); Assert.Equal(0, vm.InputDb);
        node = null; vm.Poll(); Assert.False(vm.HasReading);
        node = 1; source.Readable = false; vm.Poll(); Assert.False(vm.HasReading);
        vm.Dispose(); vm.SetSurfaceLive(this, true); vm.Poll();
        Assert.Equal(0, source.Owners);
    }
}
