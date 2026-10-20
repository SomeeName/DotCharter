namespace DotCharter.Tone.Infrastructure.Configuration;

/// <summary>
///     Where the tone rig and the audio host read and write <see cref="RigConfig" />.
///     DotCharter's <c>IConfigurationService</c> extends this over its
///     <c>AppConfig</c>; the standalone rig implements it over its own file.
/// </summary>
public interface IRigConfigService
{
    /// <summary>The shared, cached config. Treat it as read-only; write through
    /// <see cref="UpdateRigConfig" />.</summary>
    RigConfig LoadRigConfig();

    /// <summary>
    ///     Applies <paramref name="mutate" /> under the config's lock and persists
    ///     it. Use this, never load + mutate + save, whenever the mutation touches
    ///     a collection (<c>UserTonePresets</c>): without the lock a concurrent
    ///     serialize throws "Collection was modified" and the save is dropped.
    /// </summary>
    void UpdateRigConfig(Action<RigConfig> mutate);
}
