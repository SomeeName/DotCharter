namespace DotCharter.Tone.Audio.Dsp;

/// <summary>
///     The per-player seat constants of the native audio ABI (v28), mirrored once
///     so nothing else in the app has to hand-copy them.
///     <para>
///         These describe the NATIVE engine's capacity, not a policy: how many
///         independent live-input rigs exist and how DSP node ids encode which one
///         they belong to. How many seats are live is the input configuration's
///         decision (<c>LiveInputSeatLayout.FromInputConfig</c>), not this.
///     </para>
/// </summary>
public static class DspPlayers
{
    /// <summary>Mirrors <c>DCA_MAX_PLAYERS</c>. Two is the two-guitar
    /// requirement; the extra headroom costs one prepared (silent) chain and
    /// capture ring per unused seat and nothing at all on the audio thread,
    /// because the callback only walks the seats that are LIVE.</summary>
    /// <remarks>A literal, not a forward to
    /// <c>JuceAudioEngine.MaxPlayers</c>, because that is a static PROPERTY and
    /// this must stay <c>const</c>. Pinned to the wrapper's value by
    /// <c>Services/Audio/EngineAbiEnumMirrorTests</c>.</remarks>
    public const int MaxPlayers = 4;

    /// <summary>Mirrors <c>DCA_PLAYER_NODE_ID_STRIDE</c>. Seat <c>p</c>'s DSP node
    /// ids are <c>p * stride + local</c>, which is why the node-scoped half of the
    /// ABI needs no player argument. Seat 0's ids are 0, 1, 2, … as before v28.</summary>
    public const int NodeIdStride = 1_000_000;

    /// <summary>Which seat a DSP node id belongs to. Pure; a negative id is
    /// reported as seat 0 (there is no such node on any seat).</summary>
    public static int PlayerOfNodeId(int nodeId) => nodeId <= 0 ? 0 : nodeId / NodeIdStride;

    /// <summary>Whether a seat index names a rig the native engine actually
    /// hosts.</summary>
    public static bool IsValid(int player) => player is >= 0 and < MaxPlayers;
}
