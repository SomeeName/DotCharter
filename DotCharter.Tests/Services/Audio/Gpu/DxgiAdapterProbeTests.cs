using DotCharter.Services.Audio.StemSeparation.Gpu;
using JuceSharp.StemSeparation.Onnx;

namespace DotCharter.Tests.Services.Audio.Gpu;

/// <summary>
///     The app's own DXGI list (with LUIDs and displays) must index the adapters exactly as
///     JuceSharp's list and ONNX Runtime's DirectML provider do — <c>EnumAdapters1</c>
///     order — or a chosen device id would land on another card. Read-only: it creates
///     no device.
/// </summary>
public sealed class DxgiAdapterProbeTests
{
    [Fact]
    public void TheProbe_ListsTheSameAdaptersInTheSameOrder_AsJuceSharp()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DXGI is Windows-only.");

        var probed = DxgiAdapterProbe.Enumerate();
        var juce = OnnxGpuAdapters.Enumerate();

        Assert.NotEmpty(probed); // WARP is always there.
        Assert.Equal(Enumerable.Range(0, probed.Count), probed.Select(a => a.DeviceId));
        Assert.Equal(juce.Select(a => (a.DeviceId, a.Name, a.DedicatedVideoMemoryBytes, a.IsSoftware)),
            probed.Select(a => (a.DeviceId, a.Name, a.DedicatedVideoMemoryBytes, a.IsSoftware)));
        Assert.All(probed, a => Assert.NotEqual(0UL, a.Luid));
        Assert.All(probed.Where(a => a.IsSoftware), a => Assert.False(a.DrivesDisplay));
    }
}
