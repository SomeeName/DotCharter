namespace DotCharter.Rendering.Vulkan;

/// <summary>
///     Bit set carried to the highway shaders through the push-constant
///     <c>uEffects</c> field (see <c>Shaders/highway.vert</c>/<c>.frag</c> —
///     keep the GLSL constants in sync). Computed once per frame by
///     <c>Fretboard3DEngine.RenderFrame</c> from the snapshot's effect toggles;
///     the screen-space ortho passes (crowd background, vignette) always run
///     with <see cref="None" />.
/// </summary>
[Flags]
public enum HighwayEffects : uint
{
    None = 0,

    /// <summary>Plan 5.6: directional key + fill lighting from the streamed
    /// vertex normals, surface modes only.</summary>
    Lighting = 1u << 0,

    /// <summary>Plan 5.7: time-driven light band + sparkle along the string
    /// cores (vertex mode 12). Suppressed while Live Play runs.</summary>
    StringShimmer = 1u << 1
}
