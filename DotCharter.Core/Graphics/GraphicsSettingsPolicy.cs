namespace DotCharter.Core.Graphics;

/// <summary>
///     Pure clamping rules for the 0.14.2 Settings → Graphics values, shared by
///     the settings UI, the startup config apply and the renderer so every
///     consumer agrees on the valid ranges (unit-tested in
///     <c>GraphicsSettingsPolicyTests</c>). Lives in Rendering (not Services)
///     because the renderer-side consumers cannot reference the service layer.
/// </summary>
public static class GraphicsSettingsPolicy
{
    /// <summary>Render scale bounds (percent of the panel size). Below 50%
    /// text becomes unreadable; above 200% the intermediate target cost
    /// outgrows any supersampling benefit.</summary>
    public const int MinRenderScalePercent = 50;

    public const int MaxRenderScalePercent = 200;
    public const int DefaultRenderScalePercent = 100;

    /// <summary>Upcoming-anchor preview lead-time bounds (ms).</summary>
    public const int MinAnchorPreviewLeadMs = 200;

    public const int MaxAnchorPreviewLeadMs = 3000;
    public const int DefaultAnchorPreviewLeadMs = 1000;

    /// <summary>
    ///     3D preview camera "fill": how much of the frame the neck occupies, as a
    ///     percent of the tuned default (100). Higher fills more (camera pulled
    ///     IN); lower fills less (camera pulled BACK). Surfaced to the user as a
    ///     "camera distance" slider — the inverse, so more distance = smaller
    ///     board (see <c>Fretboard3DEngine.CalculateCameraMvp</c> for the fill →
    ///     projection scale, and the settings view models for the distance
    ///     inversion). ±20% is enough to span "wastes the frame" to "too close"
    ///     on the widest and tallest boards without ever losing the neck.
    /// </summary>
    public const int MinCameraFillPercent = 80;

    public const int MaxCameraFillPercent = 120;
    public const int DefaultCameraFillPercent = 100;

    public static int ClampRenderScalePercent(int percent) => Math.Clamp(percent, MinRenderScalePercent, MaxRenderScalePercent);

    public static int ClampCameraFillPercent(int percent) => Math.Clamp(percent, MinCameraFillPercent, MaxCameraFillPercent);

    public static int ClampAnchorPreviewLeadMs(int ms) => Math.Clamp(ms, MinAnchorPreviewLeadMs, MaxAnchorPreviewLeadMs);

    /// <summary>
    ///     Max-fps semantics: any value ≤ 0 means "unlimited" (0 canonical);
    ///     positive values get a usability floor so a corrupt config can never
    ///     park the render loop below the idle trickle.
    /// </summary>
    public static int ClampMaxFps(int maxFps) => maxFps <= 0 ? 0 : Math.Max(MinCappedFps, maxFps);

    /// <summary>Floor for a positive fps cap. Deliberately above
    /// <see cref="MaxIdleFps" /> so a cap interval is always shorter than the
    /// slowest idle trickle interval — the fps cap can never starve the
    /// parked-loop safety net.</summary>
    public const int MinCappedFps = 15;

    /// <summary>Paused-loop trickle bounds (Hz). The 0.14 P6 idle gate caps
    /// the trickle at 10 Hz by design — Settings can only lower it.</summary>
    public const int MinIdleFps = 1;

    public const int MaxIdleFps = 10;
    public const int DefaultIdleFps = 10;

    public static int ClampIdleFps(int idleFps) => Math.Clamp(idleFps, MinIdleFps, MaxIdleFps);
}
