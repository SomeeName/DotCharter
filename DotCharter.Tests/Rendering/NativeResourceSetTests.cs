using DotCharter.Rendering.Vulkan;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     A native resource set must be built all-or-nothing.
///     <para>
///         <c>VulkanComposedImagePool.Recreate</c> empties <c>_slots</c>, then
///         builds N slots into a LOCAL array and publishes it only after the loop.
///         <c>CreateSlot</c> throws on a failed image create, a failed
///         shared-handle export or a failed memory import — so a throw part-way
///         used to strand every earlier slot's <c>VkImage</c>, imported
///         <c>DeviceMemory</c> and <c>ID3D11Texture2D</c> in a local that neither
///         <c>DestroySlots</c> nor <c>Dispose</c> could reach.
///     </para>
///     <para>
///         The amplifier is the retry: the throw reaches
///         <c>Preview3DRenderDriver</c>'s per-frame catch, which retries up to 120
///         consecutive times — so one sustained VRAM shortage leaked a fresh batch
///         per retry, feeding the very condition that caused it.
///     </para>
/// </summary>
public sealed class NativeResourceSetTests
{
    [Fact]
    public void AllCreatedResourcesAreReturnedOnSuccess()
    {
        var destroyed = new List<int>();

        var built = NativeResourceSet.BuildAllOrRollback(3, i => i, destroyed.Add);

        Assert.Equal([0, 1, 2], built);
        Assert.Empty(destroyed);
    }

    /// <summary>The whole point: a mid-way failure frees what it already made.</summary>
    [Fact]
    public void AFailurePartWayThroughDestroysEverythingBuiltSoFar()
    {
        var destroyed = new List<int>();

        Assert.Throws<InvalidOperationException>(() => NativeResourceSet.BuildAllOrRollback(
            3,
            i => i == 2 ? throw new InvalidOperationException("out of video memory") : i,
            destroyed.Add));

        // Reverse order — resources are torn down in the opposite order to
        // construction, the same as every other teardown path.
        Assert.Equal([1, 0], destroyed);
    }

    /// <summary>
    ///     The first element failing must free nothing and still propagate — the
    ///     rollback may not invent a destroy for a resource that never existed.
    /// </summary>
    [Fact]
    public void AFailureOnTheFirstResourceDestroysNothing()
    {
        var destroyed = new List<int>();

        Assert.Throws<InvalidOperationException>(() => NativeResourceSet.BuildAllOrRollback<int>(
            3, _ => throw new InvalidOperationException(), destroyed.Add));

        Assert.Empty(destroyed);
    }

    /// <summary>
    ///     The ORIGINAL failure is the one worth diagnosing. A destroy that throws
    ///     during rollback must not replace "out of video memory" with a confusing
    ///     teardown error — nor strand the rest of the batch.
    /// </summary>
    [Fact]
    public void ADestroyThatThrowsNeitherMasksTheOriginalFailureNorStrandsTheRest()
    {
        var destroyed = new List<int>();

        var ex = Assert.Throws<InvalidOperationException>(() => NativeResourceSet.BuildAllOrRollback(
            3,
            i => i == 2 ? throw new InvalidOperationException("out of video memory") : i,
            i =>
            {
                destroyed.Add(i);
                if (i == 1) throw new ArgumentException("teardown blew up");
            }));

        Assert.Equal("out of video memory", ex.Message);
        Assert.Equal([1, 0], destroyed);
    }

    [Fact]
    public void ZeroResourcesIsAnEmptySetNotAFailure()
    {
        var destroyed = new List<int>();

        var built = NativeResourceSet.BuildAllOrRollback(0, i => i, destroyed.Add);

        Assert.Empty(built);
        Assert.Empty(destroyed);
    }

    /// <summary>
    ///     The pool must actually USE the helper — a rollback nobody calls is the
    ///     leak with extra steps.
    /// </summary>
    [Fact]
    public void TheComposedImagePoolBuildsItsSlotsThroughTheRollbackHelper()
    {
        var source = File.ReadAllText(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.Rendering", "Vulkan", "Composed", "VulkanComposedImagePool.cs"));

        Assert.Contains("NativeResourceSet.BuildAllOrRollback(", source, StringComparison.Ordinal);
        // …and rolls back through the SAME per-slot teardown a live pool uses, so
        // the two cannot drift apart.
        Assert.Contains("private void DestroySlot(Slot slot)", source, StringComparison.Ordinal);
    }
}
