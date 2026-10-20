using DotCharter.Services.Audio.StemSeparation.Gpu;
using JuceSharp.StemSeparation.Onnx;

namespace DotCharter.Tests.Services.Audio.Gpu;

/// <summary>
///     Where a separation runs: DirectML on the stored or fastest GPU, else the CPU with
///     the reason the status line prints. Pure — the adapter list is injected.
/// </summary>
public sealed class StemExecutionDecisionTests
{
    private static readonly StemGpuAdapter Igpu = new(0, "Intel(R) Iris(R) Xe Graphics", 128UL << 20, false);
    private static readonly StemGpuAdapter Dgpu = new(1, "NVIDIA GeForce RTX 4060 Laptop GPU", 8UL << 30, false);
    private static readonly StemGpuAdapter Warp = new(2, "Microsoft Basic Render Driver", 0, true);

    [Theory]
    [InlineData("Auto")]
    [InlineData("DirectML")]
    [InlineData("Cuda")] // what versions with the CUDA build stored as their automatic choice
    [InlineData(null)]
    [InlineData("")]
    public void AGpuPreference_RunsOnDirectMl_OnTheFastestAdapter_NotAdapterZero(string? preference)
    {
        var plan = StemExecutionDecision.Decide(preference, directMlInRuntime: true, [Igpu, Dgpu, Warp], "");

        Assert.Equal(OnnxExecutionProvider.DirectML, plan.Provider);
        Assert.Equal(1, plan.DeviceId);
        Assert.Equal(Dgpu, plan.Adapter);
        Assert.Equal(StemExecutionReason.DirectMl, plan.Reason);
    }

    [Fact]
    public void TheUsersCpuChoice_IsHonoured_EvenWithAGpu()
    {
        var plan = StemExecutionDecision.Decide("cpu", true, [Dgpu], "");
        Assert.Equal(OnnxExecutionProvider.Cpu, plan.Provider);
        Assert.Equal(StemExecutionReason.UserChoseCpu, plan.Reason);
    }

    [Fact]
    public void ARuntimeWithoutDirectMl_RunsOnTheCpu_AndSaysWhy()
    {
        var plan = StemExecutionDecision.Decide("Auto", directMlInRuntime: false, [Dgpu], "");
        Assert.Equal(OnnxExecutionProvider.Cpu, plan.Provider);
        Assert.Equal(StemExecutionReason.NoDirectMlInRuntime, plan.Reason);
        Assert.Null(plan.Adapter);
    }

    [Fact]
    public void NoGpuThatCanRunTheModel_RunsOnTheCpu_AndSaysWhy()
    {
        var plan = StemExecutionDecision.Decide("DirectML", true, [Igpu, Warp], "");
        Assert.Equal(OnnxExecutionProvider.Cpu, plan.Provider);
        Assert.Equal(StemExecutionReason.NoUsableGpu, plan.Reason);
    }

    [Fact]
    public void AGpuRequestThatLandedOnTheCpu_ReportsWhy_AndNothingElseDoes()
    {
        Assert.Equal("DirectML: out of video memory", StemExecutionDecision.CpuFallbackReason(
            OnnxExecutionProvider.DirectML, OnnxExecutionProvider.Cpu, "DirectML: out of video memory"));
        Assert.Equal("the GPU provider did not attach", StemExecutionDecision.CpuFallbackReason(
            OnnxExecutionProvider.DirectML, OnnxExecutionProvider.Cpu, null));
        Assert.Null(StemExecutionDecision.CpuFallbackReason(
            OnnxExecutionProvider.DirectML, OnnxExecutionProvider.DirectML, null));
        Assert.Null(StemExecutionDecision.CpuFallbackReason(
            OnnxExecutionProvider.Cpu, OnnxExecutionProvider.Cpu, "irrelevant"));
    }

    [Fact]
    public void TheStatusLine_NamesTheGpuOrWhyThereIsNone()
    {
        Assert.Equal("DirectML on 'NVIDIA GeForce RTX 4060 Laptop GPU' (adapter 1, 8192 MB)",
            StemExecutionDecision.Describe(StemExecutionDecision.Decide("Auto", true, [Igpu, Dgpu], "")));
        Assert.Contains("no DirectX 12 GPU with 3 GB",
            StemExecutionDecision.Describe(StemExecutionDecision.Decide("Auto", true, [Igpu], "")), StringComparison.Ordinal);
    }

    [Fact]
    public void TheStoredGpu_IsTheDeviceRequested()
    {
        var second = new StemGpuAdapter(3, "AMD Radeon RX 7800 XT", 16UL << 30, false);
        var plan = StemExecutionDecision.Decide("Auto", true, [Dgpu, second], StemGpuRanking.KeyFor(Dgpu));
        Assert.Equal(1, plan.DeviceId);
        Assert.Equal(Dgpu, plan.Adapter);
    }

    /// <summary>#463: every run on the owner's machine bound DirectML device 0, the 5070 Ti that drives the monitors.</summary>
    [Fact]
    public void OnTheOwnersMachine_DirectMlGetsTheIdle3080_AndThePlanSaysWhy()
    {
        var display = new StemGpuAdapter(0, "NVIDIA GeForce RTX 5070 Ti", 16UL << 30, false, DrivesDisplay: true);
        var idle = new StemGpuAdapter(1, "NVIDIA GeForce RTX 3080", 10UL << 30, false);

        var plan = StemExecutionDecision.Decide("Auto", true, [display, idle, Warp], "");

        Assert.Equal(OnnxExecutionProvider.DirectML, plan.Provider);
        Assert.Equal(1, plan.DeviceId);
        Assert.Contains("drives no display", plan.GpuChoiceReason, StringComparison.Ordinal);
        Assert.Equal(0, StemExecutionDecision.Decide("Auto", true, [display, idle], StemGpuRanking.KeyFor(display)).DeviceId);
    }
}
