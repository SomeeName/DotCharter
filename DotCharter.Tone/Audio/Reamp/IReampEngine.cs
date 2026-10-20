namespace DotCharter.Tone.Audio.Reamp;

/// <summary>
///     What the DI recorder and the re-amp transport need from the audio engine
///     (ABI v37). Implemented by <c>JuceAudioPlaybackService</c> and reached with
///     an <c>as</c> cast from <c>IAudioPlaybackService</c>, the way
///     <c>IRigMidiInputSource</c> is — so the null transport and every test fake
///     simply do not have it, and the rig shows re-amping disabled with its
///     reason instead of drawing controls nothing honours.
///     <para>
///         <b>Threading.</b> Every member may be called from any non-audio
///         thread; none throws for "no engine right now" — it answers 0 / false.
///         <see cref="LoadTake" /> resamples (a few hundred ms for a long take at
///         a foreign rate) and belongs off the UI thread.
///     </para>
/// </summary>
public interface IReampEngine
{
    /// <summary>True while an engine is up to capture from and inject into.</summary>
    bool IsReampAvailable { get; }

    /// <summary>The engine's sample rate (0 with no engine) — the rate the
    /// capture ring is recorded at.</summary>
    int ReampSampleRate { get; }

    /// <summary>Total samples one seat's capture ring has ever captured, i.e. the
    /// absolute stream position of the NEXT sample. False with no engine or
    /// before the first input block.</summary>
    bool TryGetCaptureTotal(int player, out ulong total);

    /// <summary>Cursorless positional read of one seat's capture ring
    /// (<c>dca_engine_read_input_capture_from_for_player</c>): up to
    /// <c>dest.Length</c> samples from absolute position <paramref name="from" />;
    /// <paramref name="first" /> is where the copy really began.</summary>
    int ReadCaptureFrom(int player, ulong from, Span<float> dest, out ulong first);

    /// <summary>
    ///     Loads a mono take (at its OWN rate — the engine side resamples it to
    ///     the device rate) into the seat's input, PAUSED at 0. From the next
    ///     block the seat's live input is muted and the take is what its rig,
    ///     tuner and meter hear. The take is REMEMBERED and replayed onto a
    ///     rebuilt engine (device change, NAM 48 kHz, watchdog) at the position
    ///     it had reached. False with no engine or a refused take.
    /// </summary>
    bool LoadTake(int player, float[] mono, int sampleRate, bool loop);

    /// <summary>Whether a take is loaded for the seat — REMEMBERED, so it stays
    /// true across an engine rebuild until the replay either restores the take or
    /// gives it up. A status read that fails while this is true is transient.</summary>
    bool HasTake(int player);

    /// <summary>Removes the take; the seat hears its guitar again.</summary>
    void ClearTake(int player);

    /// <summary>Plays or pauses the loaded take.</summary>
    bool SetTakePlaying(int player, bool playing);

    /// <summary>Loop (wrap at the end) or one-shot (stop and rewind at the end).</summary>
    bool SetTakeLoop(int player, bool loop);

    /// <summary>Moves the play head, in seconds.</summary>
    bool SeekTake(int player, double seconds);

    /// <summary>The loaded take's transport; false when no take is loaded.</summary>
    bool TryGetTakeStatus(int player, out ReampTransportStatus status);
}

/// <summary>A loaded take's transport, in seconds at the engine rate.</summary>
public readonly record struct ReampTransportStatus(
    double PositionSeconds, double LengthSeconds, bool IsPlaying, bool IsLooping);
