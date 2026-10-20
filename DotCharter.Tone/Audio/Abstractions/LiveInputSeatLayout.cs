using DotCharter.Tone.Audio.Dsp;

namespace DotCharter.Tone.Audio.Abstractions;

/// <summary>
///     The whole per-player live-input routing as ONE value: how many seats the
///     engine runs, which OPEN input channel each seat plays through, and how
///     loud each one monitors.
///     <para>
///         <b>Why one value rather than three setters.</b> The seat count and the
///         seat channels only make sense together — a count of 2 with seat 1 still
///         on its default channel is a second player listening to the first
///         player's jack — and they must not be written one primitive at a
///         time from one caller (a run-scoped rig applier), whose
///         exit hard-codes "one seat, channel 0". That is exactly how two guitars
///         end up heard only hard-panned outside that run: nothing else in
///         the app may say how many seats there are.
///     </para>
///     <para>
///         Immutable, pure and free of any engine. The engine push lives in
///         <c>JuceAudioPlaybackService.PlayerRouting</c>, which is the single
///         place <c>dca_engine_set_player_count</c> is reached from.
///     </para>
/// </summary>
public sealed class LiveInputSeatLayout
{
    private readonly int[] _channels;
    private readonly float[] _monitorGains;

    /// <summary>
    ///     One seat on its default channel at unity — the pre-v28 signal path
    ///     byte-for-byte, and what an engine starts on. A single seat fed from a
    ///     two-channel device is the STEREO-PICKUP reading: the native callback
    ///     puts input channels 0 and 1 on the chain's L and R.
    /// </summary>
    public static LiveInputSeatLayout Single { get; } = new(1, [0]);

    /// <param name="playerCount">Seats, clamped to
    /// <see cref="DspPlayers.MaxPlayers" />.</param>
    /// <param name="channels">Each seat's channel INDEX INTO THE OPEN SET, not
    /// into the interface's jacks: JUCE hands the callback the enabled channels
    /// compacted in ascending hardware order, so a rig on ASIO inputs 3 and 4
    /// still reads 0 and 1 here. A short list leaves the remaining seats on
    /// <see cref="PlayerInputRoutingState.DefaultChannelFor" />; <c>-1</c> means
    /// "mix every open channel to mono".</param>
    /// <param name="monitorGains">Each seat's monitor trim. A short list leaves
    /// the rest at <see cref="PlayerInputRoutingState.DefaultMonitorGain" />.</param>
    public LiveInputSeatLayout(int playerCount, IReadOnlyList<int>? channels = null,
        IReadOnlyList<float>? monitorGains = null)
    {
        PlayerCount = Math.Clamp(playerCount, 1, DspPlayers.MaxPlayers);
        _channels = new int[DspPlayers.MaxPlayers];
        _monitorGains = new float[DspPlayers.MaxPlayers];
        for (var p = 0; p < DspPlayers.MaxPlayers; p++)
        {
            _channels[p] = channels != null && p < channels.Count
                ? channels[p]
                : PlayerInputRoutingState.DefaultChannelFor(p);
            var gain = monitorGains != null && p < monitorGains.Count
                ? monitorGains[p]
                : PlayerInputRoutingState.DefaultMonitorGain;
            _monitorGains[p] = float.IsFinite(gain) && gain >= 0f
                ? gain
                : PlayerInputRoutingState.DefaultMonitorGain;
        }
    }

    /// <summary>How many independent live-input rigs the engine runs.</summary>
    public int PlayerCount { get; }

    /// <summary>Every seat's channel, seat order, always
    /// <see cref="DspPlayers.MaxPlayers" /> long — seats past
    /// <see cref="PlayerCount" /> carry their defaults so switching layouts can
    /// never leave a stale channel behind on a seat that comes back.</summary>
    public IReadOnlyList<int> Channels => _channels;

    /// <summary>Every seat's monitor trim, seat order, same length rule.</summary>
    public IReadOnlyList<float> MonitorGains => _monitorGains;

    /// <summary>
    ///     How many hardware input channels the engine OPENS for this
    ///     configuration — the exact expression
    ///     <c>JuceAudioPlaybackService.ApplyAudioConfigurationCore</c> puts in
    ///     <c>JuceEngineConfig.InputChannels</c>, so the two can never disagree.
    ///     <para>
    ///         <b>Zero when live input is switched off</b>, which is the shipped
    ///         default (<see cref="RigConfig.EnableInput" /> has no initialiser).
    ///         A caller that derived this itself from <c>AsioInputMono</c> alone
    ///         answered ONE with no device input open at all, and offered a
    ///         "Channel 1" that is not open — a capability the app cannot honour
    ///         looking live, on every machine that has never plugged a guitar in.
    ///     </para>
    /// </summary>
    public static int OpenInputChannels(RigConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.EnableInput ? config.AsioInputMono ? 1 : 2 : 0;
    }

    /// <summary>
    ///     The seat layout the INPUT CONFIGURATION asks for — the resting value
    ///     the engine holds whenever no override is in force.
    ///     <para>
    ///         <b>Two open channels are not the same claim as two guitars.</b>
    ///         Unticking Mono has always meant "my pickup is stereo, give the
    ///         chain a real L and R", which is the case the native
    ///         <c>players == 1</c> branch was written for; inferring "two players"
    ///         from it would silently collapse that user's stereo image to a
    ///         centred mono. The app cannot tell the two apart from the signal, so
    ///         it is an EXPLICIT setting
    ///         (<see cref="RigConfig.InputChannelsAreSeparateGuitars" />) and only
    ///         that setting raises the seat count.
    ///     </para>
    /// </summary>
    public static LiveInputSeatLayout FromInputConfig(RigConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        // No live input at all ⇒ nothing is open, so there is nothing to split.
        if (!config.EnableInput) return Single;
        // ONE channel is open (the shipped default). Mirrors the InputChannels
        // JuceAudioPlaybackService.ApplyAudioConfigurationCore asks the device
        // for; a second seat would read a channel that does not exist.
        if (config.AsioInputMono) return Single;
        // Two channels open, but they are one instrument. Engine stays on the
        // v27 path and fills the chain's L/R from them.
        if (!config.InputChannelsAreSeparateGuitars) return Single;

        // Two guitars, one per channel: each seat is mono from its own jack and
        // centred through its own rig.
        //
        // The channel INDEXES are ASIO-only, because the input channel MASK is:
        // ApplyAudioConfigurationCore builds one only when the engine driver
        // (RigConfig.AudioDriver — ASIO is single-driver-exclusive, so it is the
        // input driver too) is ASIO. Without a mask the device opens channels
        // 0..N-1 in order, so seat p reads p. With one, JUCE compacts the
        // enabled bits ascending — which swaps the seats when the user typed the
        // HIGHER jack in the "left" box.
        var asio = string.Equals(config.AudioDriver, "ASIO", StringComparison.OrdinalIgnoreCase);
        var firstSeatChannel = asio && config.AsioInputChannelLeft > config.AsioInputChannelRight ? 1 : 0;
        return new LiveInputSeatLayout(2, [firstSeatChannel, 1 - firstSeatChannel]);
    }
}
