using DotCharter.UI.Controls.Timeline2D;

namespace DotCharter.Tests.UI;

/// <summary>
///     A declined 2D frame start is either back-pressure (retry next tick) or a
///     lost device (stop and show the error surface). Both used to arrive as the
///     same <c>false</c>, so a lost device was retried on every compositor tick
///     for the rest of the session behind a frozen, error-free timeline.
/// </summary>
public sealed class VulkanTimelineBackendFrameStartTests
{
    [Fact]
    public void AnAcquiredFrameIsBegun() =>
        Assert.Equal(VulkanTimelineBackend.FrameStart.Begun,
            VulkanTimelineBackend.ClassifyFrameStart(begun: true, deviceLost: false));

    [Fact]
    public void ADeclinedFrameOnALiveDeviceIsBackPressure() =>
        Assert.Equal(VulkanTimelineBackend.FrameStart.BackPressure,
            VulkanTimelineBackend.ClassifyFrameStart(begun: false, deviceLost: false));

    [Fact]
    public void ADeclinedFrameOnALostDeviceIsTerminal() =>
        Assert.Equal(VulkanTimelineBackend.FrameStart.DeviceLost,
            VulkanTimelineBackend.ClassifyFrameStart(begun: false, deviceLost: true));

    [Fact]
    public void DeviceLossWinsEvenIfAFrameWasAcquired() =>
        Assert.Equal(VulkanTimelineBackend.FrameStart.DeviceLost,
            VulkanTimelineBackend.ClassifyFrameStart(begun: true, deviceLost: true));

    [Theory]
    [InlineData(Silk.NET.Vulkan.Result.Success, (int)VulkanTimelineBackend.FrameStart.Begun)]
    [InlineData(Silk.NET.Vulkan.Result.Timeout, (int)VulkanTimelineBackend.FrameStart.BackPressure)]
    [InlineData(Silk.NET.Vulkan.Result.NotReady, (int)VulkanTimelineBackend.FrameStart.BackPressure)]
    [InlineData(Silk.NET.Vulkan.Result.ErrorOutOfDeviceMemory, (int)VulkanTimelineBackend.FrameStart.BackPressure)]
    [InlineData(Silk.NET.Vulkan.Result.ErrorDeviceLost, (int)VulkanTimelineBackend.FrameStart.DeviceLost)]
    public void FrameWaitResultsReachTheExpectedBackendState(Silk.NET.Vulkan.Result result,
        int expected) =>
        Assert.Equal((VulkanTimelineBackend.FrameStart)expected, VulkanTimelineBackend.ClassifyFrameStart(
            result == Silk.NET.Vulkan.Result.Success,
            DotCharter.Rendering.Vulkan.FrameResultPolicy.IsTerminal(result)));

    [Fact]
    public void DeviceLossReportsOneErrorAndDeclinesEveryLaterFrame()
    {
        var backend = new VulkanTimelineBackend(null!);
        var errors = new List<string>();
        Assert.False(backend.AcceptFrameStart(false, false, errors.Add));
        Assert.Empty(errors);
        Assert.True(backend.AcceptFrameStart(true, false, errors.Add));
        for (var i = 0; i < 5; i++)
            Assert.False(backend.AcceptFrameStart(false, true, errors.Add));
        Assert.False(backend.AcceptFrameStart(true, false, errors.Add));
        Assert.Contains("device was lost", Assert.Single(errors), StringComparison.Ordinal);
    }

    [Fact]
    public void BeginFrameUsesTheErrorTransitionAndFailDetachesTheBackend()
    {
        var source = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(),
            "DotCharter.UI", "Controls", "Timeline2D", "VulkanTimelineBackend.cs"));
        Assert.Contains("AcceptFrameStart(renderer.TryBeginFrame(), renderer.IsDeviceLost, _onFailure)", source,
            StringComparison.Ordinal);
        Assert.Contains("_onFailure = Fail;", source, StringComparison.Ordinal);
        var failAt = source.IndexOf("private void Fail(", StringComparison.Ordinal);
        var fail = source[failAt..];
        Assert.Contains("Detach();", fail, StringComparison.Ordinal);
        Assert.Contains("_owner.OnComposedBackendFailed(this, showErrorSurface: !headless)", fail,
            StringComparison.Ordinal);
    }
}
