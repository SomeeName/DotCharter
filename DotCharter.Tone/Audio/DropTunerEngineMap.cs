namespace DotCharter.Tone.Audio;

/// <summary>
///     The drop tuner's quality mode, in its three representations: the settings
///     combo index, the persisted config string, and the native mode int.
///
///     <para>All three are the SAME ordering — 0 = Tight, 1 = Balanced, 2 = Smooth —
///     and the native int is a splice-lag FLOOR (10 / 20 / 35 ms). It is a floor,
///     not a ceiling: the shifter MEASURES the material's
///     composite period and raises the real ceiling to it, so the knob cannot
///     sit below a chord's period and force mismatched splices. What the
///     modes still choose is how much SMOOTHER than strictly necessary the splices
///     are on material that does not need a long lag: bigger = fewer, longer
///     splices and a little more sustain delay. Attack latency is unaffected — the
///     transient anchor pins that (~1.5 ms).</para>
///
///     <para><b>Why this type exists.</b> The mapping must not be written out by hand
///     in four places — the settings load, the settings save, the live push to the
///     running engine, and the engine's own startup read. They drift: a live push
///     reading <c>index == 1 ? 1 : 0</c> would push TIGHT when SMOOTH is chosen. The config
///     write is correct, so the setting appears to work — after an app restart. The
///     one mode you would restart the app to audition is the one you could not hear.
///     Keep every conversion here.</para>
/// </summary>
public static class DropTunerEngineMap
{
    public const int Tight = 0;
    public const int Balanced = 1;
    public const int Smooth = 2;

    /// <summary>The default when a config carries nothing, or something legacy
    /// ("rubberband", "timedomain" — both predate the CSVD rewrite).</summary>
    public const int Default = Balanced;

    /// <summary>Combo index / native mode → the persisted config string.</summary>
    public static string ToConfigString(int mode) => Clamp(mode) switch
    {
        Tight => "tight",
        Smooth => "smooth",
        _ => "balanced"
    };

    /// <summary>Persisted config string → combo index / native mode. Unknown and
    /// legacy values land on Balanced.</summary>
    public static int FromConfigString(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "tight" => Tight,
        "smooth" => Smooth,
        _ => Default
    };

    /// <summary>The value handed to <c>dca_engine_set_input_pitch_shift_mode</c>.
    /// It is the combo index — the scales are identical — but going through this
    /// method is what stops the two drifting again.</summary>
    public static int ToNativeMode(int comboIndex) => Clamp(comboIndex);

    private static int Clamp(int mode) => Math.Clamp(mode, Tight, Smooth);
}
