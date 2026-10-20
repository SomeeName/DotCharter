using System.Numerics;
using Avalonia.Media;

namespace DotCharter.Rendering.Data;

/// <summary>
///     Pure colour mapping for the 0.14.2 crowd-driven stage background:
///     room energy (0..1, the CrowdMeter feed — neutral ≈ 0.4 outside Live
///     Play) → a sky gradient top colour plus an amber horizon glow whose
///     intensity rises with the crowd. Unit-tested in
///     <c>CrowdBackgroundPaletteTests</c>; the engine only turns these into
///     vertex-coloured quads.
/// </summary>
public static class CrowdBackgroundPalette
{
    /// <summary>Resting room energy used when no Live Play session feeds the
    /// meter (matches <c>CrowdMeter</c>'s default neutral).</summary>
    public const float NeutralEnergy = 0.4f;

    public static (Color Top, Color Horizon) Compute(float energy)
    {
        var e = Math.Clamp(energy, 0f, 1f);

        // Cold, dim indigo for a dead room → rich warm purple for a cheering
        // one; the horizon glow brightens and warms with the crowd.
        var top = Color.FromArgb(255,
            Lerp(10, 58, e),
            Lerp(12, 24, e),
            Lerp(30, 96, e));

        var horizon = Color.FromArgb(
            Lerp(28, 120, e),
            255,
            Lerp(120, 185, e),
            40);

        return (top, horizon);
    }

    /// <summary>
    ///     A world point projected through the view's MVP into the background
    ///     pass's ortho space (x right, y DOWN from the frame top, both in pixels —
    ///     the Vulkan clip-space y already points down, so no flip). The engine
    ///     projects the far centre of the highway with it: where the stage
    ///     lights' haze dips, and its row is their horizon. Null when the point
    ///     is behind the camera, which a sane camera never produces but a
    ///     degenerate matrix can.
    /// </summary>
    public static Vector2? ProjectToScreen(Matrix4x4 mvp, Vector3 farPoint, int width, int height)
    {
        var clip = Vector4.Transform(new Vector4(farPoint, 1f), mvp);
        if (clip.W <= 1e-5f || width <= 0 || height <= 0) return null;
        var ndcX = clip.X / clip.W;
        var ndcY = clip.Y / clip.W;
        return new Vector2((ndcX + 1f) * 0.5f * width, (ndcY + 1f) * 0.5f * height);
    }

    private static byte Lerp(byte from, byte to, float t) => (byte)(from + (to - from) * t);
}
