// RsInputGate: the input gate put in front of every converted song tone.
namespace DotCharter.Tone.Audio.Dsp.RsToneConversion;

/// <summary>
///     A converted song tone is built for a gated input: a high-gain chain's
///     small-signal gain amplifies an idle input's hiss and hum to the level of
///     the notes (measured on a high-gain song tone: a -90 dBFS idle input
///     came out at -26 dBFS, louder than the played phrase). So every song tone
///     gets a gate in front.
///     <para>
///         It is the <c>gate</c> module's <c>autoFloor</c> mode: it measures the
///         input's noise floor whenever the input sits still and keeps its
///         threshold 6 dB over it, with <c>thresholdDb</c> as the ceiling. It goes
///         FIRST, ahead of every pre-pedal. It is part of the PLAYBACK path
///         (<c>RsTonePlaybackCoordinator</c>), not of the tone's conversion: it
///         belongs to the input, not to the tone's gear.
///     </para>
/// </summary>
public static class RsInputGate
{
    /// <summary>The gate module's highest ratio: below the threshold it is a
    /// hard gate, not a gentle expander.</summary>
    public const float Ratio = 50f;

    /// <summary>Opening time, ms: fast enough to keep a pick attack whole.</summary>
    public const float AttackMs = 1f;

    /// <summary>Closing time, ms: short, so the buzz stops as soon as the note
    /// has fallen under the threshold.</summary>
    public const float ReleaseMs = 10f;

    /// <summary>The highest threshold, dBFS: a stationary floor louder than this
    /// is treated as the signal, not as noise, so a quiet held note is never
    /// mistaken for a noise floor and gated.</summary>
    public const float CeilingDb = -50f;

    /// <summary>The gate node.</summary>
    public static DcRigModule Module() => new(DcRigTargetCatalog.Gate, new Dictionary<string, float>
    {
        ["thresholdDb"] = CeilingDb,
        ["ratio"] = Ratio,
        ["attackMs"] = AttackMs,
        ["holdMs"] = 0f,
        ["releaseMs"] = ReleaseMs,
        ["autoFloor"] = 1f
    });

    /// <summary>True when <paramref name="module" /> is this input gate.</summary>
    public static bool IsInputGate(DcRigModule module)
        => module.ModuleTypeId == DcRigTargetCatalog.Gate
           && module.Params.TryGetValue("autoFloor", out var auto) && auto >= 0.5f;

    /// <summary><paramref name="preset" /> with the input gate in front, unless it
    /// already starts with one.</summary>
    public static DcRigPreset Prepend(DcRigPreset preset)
    {
        if (preset.Modules.Count > 0 && IsInputGate(preset.Modules[0])) return preset;
        return preset with { Modules = [Module(), .. preset.Modules] };
    }
}
