namespace DotCharter.Tone.Audio.Abstractions;

/// <summary>
///     The per-player live-input routing (ABI v28) as STATE, so it can survive an
///     engine replace.
///     <para>
///         <b>Why this exists:</b> the player count and the per-seat input
///         channels are engine state that is NOT part of <c>JuceEngineConfig</c>,
///         so nothing restored them when the engine was rebuilt. A device change,
///         the NAM 48 kHz request or a watchdog recovery mid-song therefore reset
///         <c>playerCount_</c> to 1 and every seat to its default channel, and
///         the per-seat routing silently ended while the UI carried on saying it
///         was live.
///     </para>
///     <para>
///         <b>Seat p's default channel is p</b>, which is what the native engine
///         initialises <c>inputCaptureChannel_</c> to. That is what makes
///         <see cref="IsDefault" /> meaningful: a solo device change re-asserts
///         NOTHING, so the ordinary path costs no P/Invokes and an older native
///         DLL without the v28 exports is never poked at all.
///     </para>
///     Not thread-safe by construction; the writes are single ints and the
///     service serialises them the same way it serialises the engine.
/// </summary>
public sealed class PlayerInputRoutingState
{
    private readonly int[] _channels;
    private readonly float[] _monitorGains;
    private readonly float[] _pitchShiftSemis;
    private readonly int[] _pitchShiftModes;

    public PlayerInputRoutingState(int seats)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(seats, 1);
        _channels = new int[seats];
        _monitorGains = new float[seats];
        _pitchShiftSemis = new float[seats];
        _pitchShiftModes = new int[seats];
        ResetToDefaults();
    }

    /// <summary>The channel a seat reads when nothing has routed it — the native
    /// engine's own initialisation, seat p on channel p.</summary>
    public static int DefaultChannelFor(int player) => Math.Max(0, player);

    /// <summary>Unity — the per-seat monitor trim a fresh engine starts on.</summary>
    public const float DefaultMonitorGain = 1.0f;

    /// <summary>Bypassed — the drop-tune shift a fresh engine's every seat
    /// starts on.</summary>
    public const float DefaultPitchShiftSemitones = 0f;

    /// <summary>Balanced (a 20 ms splice-lag ceiling) — the drop-tune mode a
    /// fresh engine's every seat starts on. Same value as
    /// <c>DropTunerEngineMap.Default</c>; that type is the mapping and this is
    /// the engine's own starting point, and they must agree.</summary>
    public const int DefaultPitchShiftMode = 1;

    /// <summary>How many independent live-input rigs the engine should run.</summary>
    public int PlayerCount { get; private set; } = 1;

    /// <summary>
    ///     The layout the INPUT CONFIGURATION asks for — the resting value, and
    ///     the one <see cref="ClearOverride" /> hands back to.
    ///     <para>
    ///         This is the layer that makes two guitars audible outside an
    ///         override. Before it, the seat count had exactly one writer
    ///         (a since-removed run-scoped applier) whose exit hard-coded ONE seat, so the
    ///         engine's centred per-seat monitoring only ever engaged inside an
    ///         armed run — tuning, jamming and building tones all fell back to
    ///         the stereo fill, i.e. one guitar hard left and the other hard
    ///         right.
    ///     </para>
    /// </summary>
    public LiveInputSeatLayout Baseline { get; private set; } = LiveInputSeatLayout.Single;

    /// <summary>True while an override's layout is standing on top of
    /// <see cref="Baseline" />. A configuration apply arriving mid-run (a device
    /// change, a watchdog recovery) updates the baseline WITHOUT disturbing the
    /// run.</summary>
    public bool HasOverride { get; private set; }

    /// <summary>Each seat's hardware input channel, in seat order.</summary>
    public IReadOnlyList<int> Channels => _channels;

    /// <summary>
    ///     Each seat's monitor trim, in seat order.
    ///     <para>
    ///         The third mutating member of <see cref="IPlayerInputRouting" /> and
    ///         engine state exactly like the other two — it is written straight at
    ///         <c>dca_engine_set_player_monitor_gain</c> and appears in no config,
    ///         so a device change / the NAM 48 kHz request / a watchdog recovery
    ///         would put a balanced two-guitar monitor mix back to unity with
    ///         nothing on screen changing. It was left out when this type was
    ///         written, which made the invariant this type EXISTS for cover two
    ///         thirds of its own surface.
    ///     </para>
    /// </summary>
    public IReadOnlyList<float> MonitorGains => _monitorGains;

    /// <summary>
    ///     Each seat's drop-tune shift in semitones, in seat order (ABI v31).
    ///     <para>
    ///         The FOURTH piece of per-seat engine state that appears in no
    ///         config, and it arrived already knowing why it belongs here: the
    ///         count, the channels and the monitor trims each shipped MISSING
    ///         from this replay, and each one meant a device change / the NAM
    ///         48 kHz request / a watchdog recovery silently undid what the
    ///         player had set, with nothing on screen changing. A dropped drop
    ///         tune is the loudest of the four — the guitar comes back a whole
    ///         step out of tune with the song, mid-run.
    ///     </para>
    /// </summary>
    public IReadOnlyList<float> PitchShiftSemitones => _pitchShiftSemis;

    /// <summary>Each seat's drop-tune splice-lag mode, in seat order (ABI v31).
    /// Same 0/1/2 scale as <c>DropTunerEngineMap</c>.</summary>
    public IReadOnlyList<int> PitchShiftModes => _pitchShiftModes;

    /// <summary>True while nothing has been routed away from the engine's own
    /// defaults, so re-asserting would be a no-op.</summary>
    public bool IsDefault
    {
        get
        {
            if (PlayerCount != 1) return false;
            for (var p = 0; p < _channels.Length; p++)
            {
                if (_channels[p] != DefaultChannelFor(p))
                    return false;
            }

            for (var p = 0; p < _monitorGains.Length; p++)
            {
                // Exact compare on purpose: the only way a slot leaves unity is
                // RecordMonitorGain, and "did anyone touch this" is the question.
                if (_monitorGains[p] != DefaultMonitorGain)
                    return false;
            }

            for (var p = 0; p < _pitchShiftSemis.Length; p++)
            {
                if (_pitchShiftSemis[p] != DefaultPitchShiftSemitones)
                    return false;
            }

            for (var p = 0; p < _pitchShiftModes.Length; p++)
            {
                if (_pitchShiftModes[p] != DefaultPitchShiftMode)
                    return false;
            }

            return true;
        }
    }

    public void RecordPlayerCount(int count) => PlayerCount = Math.Clamp(count, 1, _channels.Length);

    public void RecordChannel(int player, int channel)
    {
        if (player < 0 || player >= _channels.Length) return;
        _channels[player] = channel;
    }

    /// <summary>Records a seat's monitor trim. Out-of-range seats and non-finite
    /// gains are ignored rather than throwing — this is reached from override
    /// paths, and arming one must never take the audio engine down.</summary>
    public void RecordMonitorGain(int player, float linearGain)
    {
        if (player < 0 || player >= _monitorGains.Length) return;
        if (!float.IsFinite(linearGain) || linearGain < 0f) return;
        _monitorGains[player] = linearGain;
    }

    /// <summary>Records a seat's drop-tune shift. Out-of-range seats and
    /// non-finite values are ignored rather than throwing, like the trim.</summary>
    public void RecordPitchShiftSemitones(int player, float semitones)
    {
        if (player < 0 || player >= _pitchShiftSemis.Length) return;
        if (!float.IsFinite(semitones)) return;
        _pitchShiftSemis[player] = semitones;
    }

    /// <summary>Records a seat's drop-tune splice-lag mode, clamped to the
    /// engine's 0..2 — the native export REJECTS anything else, so recording an
    /// out-of-range value would replay a write the engine refuses.</summary>
    public void RecordPitchShiftMode(int player, int mode)
    {
        if (player < 0 || player >= _pitchShiftModes.Length) return;
        _pitchShiftModes[player] = Math.Clamp(mode, 0, 2);
    }

    public void ResetToDefaults()
    {
        PlayerCount = 1;
        for (var p = 0; p < _channels.Length; p++) _channels[p] = DefaultChannelFor(p);
        for (var p = 0; p < _monitorGains.Length; p++) _monitorGains[p] = DefaultMonitorGain;
        for (var p = 0; p < _pitchShiftSemis.Length; p++) _pitchShiftSemis[p] = DefaultPitchShiftSemitones;
        for (var p = 0; p < _pitchShiftModes.Length; p++) _pitchShiftModes[p] = DefaultPitchShiftMode;
    }

    // ── The two layers ────────────────────────────────────────────────────────
    // Effective = override, when one is armed; otherwise baseline.
    // Adopting is what writes the fields above, so the engine-rebuild replay
    // keeps reading exactly one set of values and knows nothing about layers.

    /// <summary>Records the layout the input configuration asks for. It becomes
    /// the effective layout immediately unless an override is in force — a
    /// device change mid-override must not end its routing.</summary>
    public void AdoptBaseline(LiveInputSeatLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        Baseline = layout;
        if (!HasOverride) Adopt(layout);
    }

    /// <summary>Puts an override's layout on top of the baseline.</summary>
    public void AdoptOverride(LiveInputSeatLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        HasOverride = true;
        Adopt(layout);
    }

    /// <summary>Ends the override and returns to the baseline — which is NOT
    /// necessarily one seat. A user who told the app they have two guitars on two
    /// channels keeps them after the run ends, exactly as they had them before
    /// it.</summary>
    public void ClearOverride()
    {
        HasOverride = false;
        Adopt(Baseline);
    }

    private void Adopt(LiveInputSeatLayout layout)
    {
        RecordPlayerCount(layout.PlayerCount);
        for (var p = 0; p < _channels.Length && p < layout.Channels.Count; p++)
            RecordChannel(p, layout.Channels[p]);
        for (var p = 0; p < _monitorGains.Length && p < layout.MonitorGains.Count; p++)
            RecordMonitorGain(p, layout.MonitorGains[p]);
    }
}
