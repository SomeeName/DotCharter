namespace DotCharter.Services.Audio.Coordination;

/// <summary>
///     Where <see cref="RsTonePlaybackCoordinator" /> publishes the preset it
///     actually applied, so the gameplay HUD's tone label shows the live rig.
///     The UI implements it over the renderer's process-wide tone-HUD channel
///     (<c>DotCharter.Rendering.Data.GameplayToneHud</c>), which Services may not
///     reference. Called from the coordinator's apply worker, not the UI thread.
/// </summary>
public interface IGameplayToneHud
{
    /// <summary>Records the applied preset; a blank name clears the override.</summary>
    void SetRig(string? name, bool approximated);

    /// <summary>Drops the override so the HUD falls back to the arrangement's own tone.</summary>
    void ClearRig();
}
