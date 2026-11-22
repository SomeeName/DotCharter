using DotCharter.Rendering.Vulkan;
using DotCharter.Rendering.Vulkan.Composed;
using Silk.NET.Vulkan;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     A failed <c>QueueSubmit</c> after the slot fence was reset leaves it unsignaled
///     forever (the next <c>WaitForFences</c> times out and the view goes blank), so the
///     composed renderers must recreate it signaled. No GPU: the decision is a pure policy,
///     and the call sites are linted.
/// </summary>
public sealed class FrameFencePolicyTests
{
    [Theory]
    [InlineData(Result.Success, false)]
    [InlineData(Result.Timeout, true)]
    [InlineData(Result.ErrorDeviceLost, true)]
    [InlineData(Result.ErrorOutOfHostMemory, true)]
    public void FailedSubmitRecreatesTheFenceSignaled(Result submit, bool expected)
        => Assert.Equal(expected, FrameFencePolicy.NeedsRecreateSignaled(submit));

    [Theory]
    [InlineData(FrameResultAction.Continue, false)]
    [InlineData(FrameResultAction.Skip, true)]
    [InlineData(FrameResultAction.DeviceLost, true)]
    [InlineData(FrameResultAction.Fatal, true)]
    public void ClassifiedFailureRecreatesTheFenceSignaled(FrameResultAction submit, bool expected)
        => Assert.Equal(expected, FrameFencePolicy.NeedsRecreateSignaled(submit));

    [Theory]
    [InlineData("Vulkan2D/Timeline2DComposedRenderer.cs")]
    [InlineData("Vulkan/VulkanRenderer.cs")]
    [InlineData("Vulkan2D/Timeline2DHeadlessRenderer.cs")]
    public void BothComposedRenderersRecoverTheFenceOnAFailedSubmit(string file)
    {
        var source = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Rendering", file));
        Assert.Contains("FrameFencePolicy.NeedsRecreateSignaled(", source, StringComparison.Ordinal);
        Assert.Contains("FrameFencePolicy.RecreateSignaled(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void The2DRendererLatchesDeviceLostAndStopsTheFrameLoop()
    {
        var source = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Rendering",
            "Vulkan2D", "Timeline2DComposedRenderer.cs"));
        Assert.Contains("FrameResultPolicy.IsTerminal(", source, StringComparison.Ordinal);
        Assert.Contains("_disposed || IsDeviceLost", source, StringComparison.Ordinal);
    }
}
