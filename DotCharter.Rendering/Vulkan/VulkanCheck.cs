using Silk.NET.Vulkan;

namespace DotCharter.Rendering.Vulkan;

/// <summary>
///     Consumes a Vulkan CREATION/ALLOCATION result and throws with the call
///     site named. Distinct from <see cref="FrameResultPolicy" />, which
///     classifies per-FRAME results the loop is allowed to survive: nothing
///     below survives a failed create.
///     <para>
///         Forty of these results were dropped on the floor. A failed
///         <c>AllocateDescriptorSets</c> returns a NULL set that was
///         immediately written by <c>UpdateDescriptorSets</c> and later bound —
///         a validation error and a probable fault, surfaced to the user as
///         nothing at all. A failed <c>MapMemory</c> leaves a null pointer that
///         the very next frame's <c>Buffer.MemoryCopy</c> writes through. The 2D
///         path (<c>Timeline2DFrameResources</c>,
///         <c>Timeline2DComposedRenderer</c>) already did this correctly; this
///         is that same style, factored out.
///     </para>
/// </summary>
public static class VulkanCheck
{
    /// <summary>Throws <see cref="InvalidOperationException" /> unless
    /// <paramref name="result" /> is <see cref="Result.Success" />.</summary>
    public static void Ok(Result result, string site)
    {
        if (result != Result.Success)
            throw new InvalidOperationException($"Vulkan: {site} failed ({result}).");
    }
}
