namespace DotCharter.Rendering.Data;

/// <summary>
/// Process-wide channel carrying the gameplay tone-HUD state into the render
/// snapshot, mirroring the <c>ChartTheme</c> / <c>Preview3DPacing</c>
/// static-channel pattern (a lower layer can't reference the UI, so the UI
/// pushes here and the snapshot builder pulls).
///
/// <para>Two independent inputs:</para>
/// <list type="bullet">
///   <item><see cref="Enabled"/> — the <c>AppConfig.ShowGameplayToneHud</c>
///     toggle, mirrored at startup and on flip. False hides the HUD tone label.</item>
///   <item>The RIG override (<see cref="SetRig"/> / <see cref="Rig"/>) — while
///     the in-game-tone coordinator is driving the rig, the name of the
///     ACTUALLY-applied preset. This is what makes tone switching observable:
///     if the coordinator fires, the HUD shows it. Null when the coordinator is
///     idle, so the HUD falls back to the arrangement's own tone (base tone +
///     ToneChanges) resolved in the snapshot.</item>
/// </list>
///
/// Written from the coordinator's apply-worker thread, read from the render
/// thread; the override is a single immutable record swapped atomically.
/// </summary>
public static class GameplayToneHud
{
    /// <summary>Master on/off for the gameplay tone label (config toggle).</summary>
    public static volatile bool Enabled = true;

    private static volatile RigTone? _rig;

    /// <summary>The preset the in-game-tone coordinator most recently applied.</summary>
    public sealed record RigTone(string Name, bool Approximated);

    /// <summary>The live rig override, or null when the coordinator is idle.</summary>
    public static RigTone? Rig => _rig;

    /// <summary>Record the actually-applied preset (blank ⇒ clears the override).</summary>
    public static void SetRig(string? name, bool approximated) =>
        _rig = string.IsNullOrWhiteSpace(name) ? null : new RigTone(name!.Trim(), approximated);

    /// <summary>Drop the rig override so the HUD falls back to the arrangement tone.</summary>
    public static void ClearRig() => _rig = null;
}
