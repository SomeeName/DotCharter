using DotCharter.Rendering.Vulkan;
using Silk.NET.Vulkan;

namespace DotCharter.Tests.Rendering;

/// <summary>Injects failures into the completion/rollback path used by native texture,
/// frame-resource and composed-slot builders. Native call wiring is checked separately
/// because allocating Vulkan handles and D3D textures requires a real device.</summary>
public sealed class VulkanPartialBuildCleanupTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void AFailedTextureStepReleasesPartialObjectsAndStaging(int failAt)
    {
        var live = new List<int>();
        var temporary = new List<int>();
        var failure = new InvalidOperationException("allocation failed");
        var thrown = Assert.Throws<InvalidOperationException>(() => NativeResourceSet.CompleteOrRollback(() =>
        {
            for (var step = 0; step < 6; step++)
            {
                if (step == failAt) throw failure;
                if (step < 2) temporary.Add(step);
                else live.Add(step);
            }
        }, live.Clear, temporary.Clear));

        Assert.Same(failure, thrown);
        Assert.Empty(live);
        Assert.Empty(temporary);
    }

    [Fact]
    public void SuccessfulUploadsReleaseStagingAndKeepTheirTexture()
    {
        var texture = new List<int>();
        var staging = new List<int>();
        NativeResourceSet.CompleteOrRollback(() =>
        {
            staging.Add(1);
            texture.Add(2);
        }, texture.Clear, staging.Clear);

        Assert.Equal([2], texture);
        Assert.Empty(staging);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void FailedFrameConstructionReleasesEveryEarlierResource(int failAt)
    {
        var live = new List<int>();
        Assert.Throws<InvalidOperationException>(() => NativeResourceSet.CompleteOrRollback(() =>
        {
            for (var step = 0; step < 5; step++)
            {
                if (step == failAt) throw new InvalidOperationException("frame allocation failed");
                live.Add(step);
            }
        }, live.Clear));
        Assert.Empty(live);
    }

    [Theory]
    [InlineData(Result.ErrorDeviceLost)]
    [InlineData(Result.ErrorOutOfDeviceMemory)]
    public void FailedBindingReleasesTheTextureImageAndMemory(Result result)
    {
        var live = new List<string> { "D3D texture", "VkImage", "DeviceMemory" };
        Assert.Throws<InvalidOperationException>(() => NativeResourceSet.CompleteOrRollback(
            () => VulkanCheck.Ok(result, "bind"), live.Clear));
        Assert.Empty(live);
    }

    [Fact]
    public void NativeBuildersUseTheTestedRollbackWithTheirRealDestructors()
    {
        var texture = MethodBody(Path.Combine("Vulkan", "VulkanResourceManager.cs"),
            "public VulkanTexture LoadTexture(");
        Assert.Contains("NativeResourceSet.CompleteOrRollback(", texture, StringComparison.Ordinal);
        Assert.Contains("}, tex.Dispose, () =>", texture, StringComparison.Ordinal);
        Assert.Contains("DestroyBuffer(_context.Device, stagingBuffer", texture, StringComparison.Ordinal);
        Assert.Contains("FreeMemory(_context.Device, stagingBufferMemory", texture, StringComparison.Ordinal);

        var frame = MethodBody(Path.Combine("Vulkan2D", "Timeline2DFrameResources.cs"),
            "public Timeline2DFrameResources(VulkanContext context)");
        Assert.Contains("NativeResourceSet.CompleteOrRollback(", frame, StringComparison.Ordinal);
        Assert.Contains("CreateVertexBuffers(", frame, StringComparison.Ordinal);
        Assert.Contains("}, Dispose)", frame, StringComparison.Ordinal);

        var slot = MethodBody(Path.Combine("Vulkan", "Composed", "VulkanComposedImagePool.cs"),
            "private Slot CreateSlot(");
        Assert.Contains("NativeResourceSet.CompleteOrRollback(", slot, StringComparison.Ordinal);
        Assert.Contains("() => VulkanCheck.Ok(bindResult,", slot, StringComparison.Ordinal);
        Assert.Contains("() => DestroySlot(slot)", slot, StringComparison.Ordinal);
    }

    /// <summary>Returns the brace-balanced body that follows
    /// <paramref name="signature" /> in a file of <c>DotCharter.Rendering</c>.</summary>
    private static string MethodBody(string relativePath, string signature)
    {
        var source = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Rendering", relativePath));
        var at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"{relativePath}: '{signature}' not found; the test needs updating.");

        var open = source.IndexOf('{', at);
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[(open + 1)..i];
        }

        throw new InvalidOperationException($"{relativePath}: unbalanced braces after '{signature}'.");
    }
}
