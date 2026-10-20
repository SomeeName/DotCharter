using DotCharter.Core.Graphics;

namespace DotCharter.UI.Controls;

/// <summary>
///     Process-wide pacing knobs for the 3D preview render loop (Settings →
///     Graphics, 0.14.2): the max-fps cap and the paused-loop trickle rate.
///     Mirrors the <c>UiScaleController</c> static-with-setter channel so the
///     settings view model and the render thread stay decoupled — the render
///     thread reads the volatile fields once per loop iteration, the UI thread
///     writes them on load/save/live-preview. Clamping lives in the shared
///     <see cref="GraphicsSettingsPolicy" />.
/// </summary>
public static class Preview3DPacing
{
    private static volatile int _maxFps; // 0 = unlimited
    private static volatile int _idleFps = GraphicsSettingsPolicy.DefaultIdleFps;

    /// <summary>Render-loop fps cap; 0 = unlimited. Positive values are
    /// floored at <see cref="GraphicsSettingsPolicy.MinCappedFps" />.</summary>
    public static int MaxFps
    {
        get => _maxFps;
        set => _maxFps = GraphicsSettingsPolicy.ClampMaxFps(value);
    }

    /// <summary>Paused-loop trickle rate in Hz (1..10).</summary>
    public static int IdleFps
    {
        get => _idleFps;
        set => _idleFps = GraphicsSettingsPolicy.ClampIdleFps(value);
    }
}
