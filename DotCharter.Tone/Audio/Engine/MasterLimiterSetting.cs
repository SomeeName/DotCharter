namespace DotCharter.Tone.Audio.Engine;

/// <summary>
///     What the master-bus look-ahead limiter (ABI v34) should be set to, derived
///     from <see cref="RigConfig" />.
///     <para>
///         <b>The engine ships it OFF.</b> It was default-ON in ABI v34 and became
///         default-OFF in v35, for the engine's own "everything is OFF at engine
///         start" rule: the stage sits on the LIVE MONITOR path, so default-ON
///         cost every guitarist ~1 ms of latency on their own playing and moved
///         every consumer's click alignment. The app therefore has to OPT IN, and
///         it does — the engine's output is the sum of the song bus, every seat's
///         monitor mix, the click bus and both instrument legs, nothing else
///         bounds that sum, and the streaming mirror carries FLOAT, so samples
///         past ±1.0 survive into OBS / Discord's encoder and wrap there.
///     </para>
///     <para>
///         Pure, so <c>MasterLimiterSettingTests</c> pins the whole derivation
///         without a device. The clamps mirror the native ones ([−24, 0] dBFS,
///         [1, 1000] ms) rather than trusting them, because a NON-FINITE ceiling
///         is REJECTED by the wrapper and not clamped — every comparison a clamp
///         is built from is false for a NaN, and a NaN ceiling is permanent
///         silence — so a corrupt config value would throw out of the replay
///         instead of landing on something audible.
///     </para>
/// </summary>
/// <param name="Enabled">False passes the master bus through unlimited, exactly
/// as the pre-v34 engine did.</param>
/// <param name="CeilingDb">The ceiling in dBFS, already clamped to [−24, 0].</param>
/// <param name="ReleaseMs">The release in milliseconds, already clamped to
/// [1, 1000].</param>
public readonly record struct MasterLimiterSetting(bool Enabled, float CeilingDb, float ReleaseMs)
{
    /// <summary>The shipped defaults: on, at the engine's own −0.3 dBFS / 80 ms.
    /// This is also what a service that has never seen an <see cref="RigConfig" />
    /// replays, so an engine built before the first
    /// <c>ApplyAudioConfiguration</c> is still bounded.</summary>
    public static readonly MasterLimiterSetting Default = new(true, -0.3f, 80f);

    /// <summary>Lowest ceiling the native stage accepts (dBFS).</summary>
    public const float MinCeilingDb = -24f;

    /// <summary>Highest ceiling the native stage accepts (dBFS).</summary>
    public const float MaxCeilingDb = 0f;

    /// <summary>What the user's configuration asks for.</summary>
    public static MasterLimiterSetting FromConfig(RigConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var ceiling = config.MasterLimiterCeilingDb;
        // NaN / ±infinity is a REJECT native-side, not a clamp, so it never
        // reaches the engine: fall back to the shipped ceiling instead.
        if (!float.IsFinite(ceiling)) ceiling = Default.CeilingDb;

        return new MasterLimiterSetting(
            config.EnableMasterLimiter,
            Math.Clamp(ceiling, MinCeilingDb, MaxCeilingDb),
            Default.ReleaseMs);
    }
}
