namespace DotCharter.Tone.Audio.Vst;

/// <summary>
/// Persisted entry for one slot in a guitar VST3 effect chain. The combination
/// of <see cref="Id"/> + <see cref="PluginPath"/> lets the host diff a desired
/// chain against the live chain so unchanged plugins can be reused without
/// reloading.
/// </summary>
public sealed class VstChainEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string PluginPath { get; set; } = string.Empty;
    public bool Bypass { get; set; }

    /// <summary>
    ///     Which SEAT's input chain this slot belongs to (ABI v28). The
    ///     native engine hosts one VST3 input chain per player, so a slot has to
    ///     say which guitar it processes.
    ///     <para>
    ///         <b>0 is the default and is the single-player chain</b>, byte-for-byte
    ///         what every pre-v28 slot did — which is also what makes this safe to
    ///         add to a persisted type: a config written before this property
    ///         existed deserializes every slot onto seat 0, i.e. unchanged.
    ///     </para>
    /// </summary>
    public int Player { get; set; }
}
