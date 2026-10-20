namespace DotCharter.Rendering.Vulkan;

/// <summary>
///     Builds a fixed-size set of native resources <b>all-or-nothing</b>: if any
///     element's creation throws, every element built so far is destroyed before
///     the exception propagates.
///     <para>
///         <b>The failure this exists to prevent.</b>
///         <c>Composed/VulkanComposedImagePool.Recreate</c> emptied <c>_slots</c>,
///         then filled a LOCAL array by calling <c>CreateSlot</c> N times, and
///         published it only after the loop. <c>CreateSlot</c> throws on a failed
///         image create, a failed shared-handle export or a failed memory
///         import — so a throw on slot 1 or 2 left the earlier slots holding a
///         live <c>VkImage</c>, an imported <c>DeviceMemory</c> and an
///         <c>ID3D11Texture2D</c> COM reference in a local nothing else could
///         reach. <c>DestroySlots</c> and <c>Dispose</c> both read the field,
///         which was already empty, so those handles leaked for the process
///         lifetime — and because the memory is a DEDICATED import, each one
///         pinned its D3D11 texture too.
///     </para>
///     <para>
///         What made it matter is the retry: the throw propagates up to
///         <c>Preview3DRenderDriver</c>'s per-frame catch, which retries up to
///         120 consecutive times. So a sustained VRAM-exhaustion condition —
///         exactly the condition that makes <c>CreateSlot</c> throw in the first
///         place — leaked a fresh batch per retry and drove itself further into
///         the wall.
///     </para>
///     <para>
///         Generic and pure so the invariant is unit-testable without a device
///         (<c>Rendering/NativeResourceSetTests</c>). Not on a per-frame path —
///         the delegates' allocation is irrelevant here.
///     </para>
/// </summary>
public static class NativeResourceSet
{
    /// <summary>
    ///     Creates <paramref name="count" /> resources via
    ///     <paramref name="create" />. On success returns them all; on any
    ///     failure destroys what was built (in reverse order) via
    ///     <paramref name="destroy" /> and rethrows, having produced nothing.
    /// </summary>
    public static T[] BuildAllOrRollback<T>(int count, Func<int, T> create, Action<T> destroy)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentNullException.ThrowIfNull(create);
        ArgumentNullException.ThrowIfNull(destroy);

        var built = new T[count];
        var made = 0;
        try
        {
            for (; made < count; made++) built[made] = create(made);
        }
        catch
        {
            // Reverse order, and a destroy that throws must not mask the
            // ORIGINAL failure — that is the one worth diagnosing, and losing
            // it turns a clear "out of video memory" into a confusing teardown
            // error. Nor may one bad handle strand the rest of the batch.
            for (var i = made - 1; i >= 0; i--)
            {
                try
                {
                    destroy(built[i]);
                }
                catch
                {
                    // Intentionally swallowed; see above.
                }
            }

            throw;
        }

        return built;
    }
}
