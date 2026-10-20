namespace DotCharter.Tone.Audio.Abstractions;

/// <summary>
///     The song clock: what is loaded, where the playhead is, play / pause /
///     seek, and the clicks that are scheduled against the same render clock.
///     One of the focused seams <see cref="IAudioPlaybackService" /> is made of
///     (docs/agents/services/audio-engine.md, "Focused interfaces for view
///     models"); resolve it instead of the whole service when this is all a
///     caller needs.
///     <para>
///         <b>Threading.</b> <see cref="PositionChanged" /> fires on a thread-pool
///         timer thread at up to 250 Hz — coalesce before touching UI state.
///         <see cref="Play" />, <see cref="Pause" />, <see cref="Stop" /> and
///         <see cref="SeekTo" /> reach the engine's apply gate (short holds; see
///         <c>PlaybackGateHoldGuardTests</c>). Schedule audio against
///         <see cref="RawPlayheadMs" />, never the compensated
///         <see cref="CurrentTimeMs" />.
///     </para>
/// </summary>
public interface IPlaybackTransport
{
    double CurrentTimeMs { get; }
    double PlaybackSpeed { get; set; }
    bool IsPlaying { get; }

    event EventHandler<double>? PositionChanged;

    Task LoadAudioAsync(string filePath);

    /// <summary>
    /// The file the master transport currently has open — the path passed to
    /// <see cref="LoadAudioAsync"/>, or the <c>displayPath</c> of
    /// <see cref="LoadAudioFromMemoryAsync"/> (which the PSARC pipeline sets to
    /// the cached WAV it read the bytes from, so it is a real file either way).
    /// Empty when nothing is loaded.
    /// <para>Exists so a caller that must TEMPORARILY borrow the transport for
    /// some other audio (Live Play take listen-back) can put the song back
    /// afterwards instead of leaving the session without its audio.</para>
    /// </summary>
    string CurrentAudioPath => "";

    /// <summary>
    /// Open audio directly from an in-memory blob. Used by the PSARC pipeline
    /// so decoded WEMs don't have to be materialised under
    /// <c>%LocalAppData%\DotCharter\PsarcAudioCache</c> just to feed JUCE
    /// (avoids SSD write amplification on every song open). The
    /// <paramref name="formatHint"/> ("wav", "ogg", …) biases JUCE's format
    /// detector; pass null to probe. <paramref name="displayPath"/> is a
    /// human-readable label used for logging only.
    /// <para>The implementation does NOT copy <paramref name="bytes"/>: the
    /// caller must leave them unchanged until the returned task completes, and
    /// nothing holds them after that (the engine keeps its own copy only until
    /// the song is decoded into RAM).</para>
    /// </summary>
    Task LoadAudioFromMemoryAsync(ReadOnlyMemory<byte> bytes, string formatHint, string? displayPath = null);
    double GetTotalAudioLengthMs();
    void FreeAudio();

    /// <summary>
    ///     Monotonic counter bumped by every deliberate <see cref="SeekTo"/>.
    ///     Lets a tick-driven consumer distinguish a real transport seek from
    ///     its own stalled tick loop when it observes a large playhead jump
    ///     (Live Play stall-vs-seek discrimination). Default 0 for
    ///     implementations that never seek.
    /// </summary>
    long SeekGeneration => 0;

    void Play();
    void Pause();
    void Stop();
    void SeekTo(double timeMs);

    /// <summary>Fires a metronome click NOW — it starts at the next audio block
    /// and is therefore heard one output latency from now. Correct for a count-in
    /// or a "preview the click" button, where "now" is what the caller means.
    /// For a click that has to land ON a beat of the song, use
    /// <see cref="TryScheduleClick"/>.</summary>
    void PlayMetronomeTick(bool isMeasure);

    /// <summary>Fires a clap NOW. Same caveat as <see cref="PlayMetronomeTick"/>.</summary>
    void PlayClap();

    /// <summary>
    /// The RAW (uncompensated) transport position in chart ms: the song time the
    /// engine is currently WRITING INTO the output buffer. It therefore runs one
    /// output latency AHEAD of <see cref="CurrentTimeMs"/> / the
    /// <see cref="PositionChanged"/> argument, which report what the user is
    /// HEARING right now (raw minus <see cref="IAudioDeviceControl.EstimatedOutputLatencyMs"/> +
    /// <see cref="IAudioDeviceControl.AudioDelayMs"/>). Everything after this point in the song has
    /// not been rendered yet — which is exactly what makes it schedulable.
    /// Refreshed by the same position tick that raises <see cref="PositionChanged"/>.
    /// </summary>
    double RawPlayheadMs { get; }

    /// <summary>
    /// Schedules a click (0 = beat tick, 1 = downbeat/measure, 2 = clap) so that it
    /// is rendered into the SAME output samples as the song's own audio at chart
    /// time <paramref name="transportMs"/> — sample-accurate, no block-boundary
    /// jitter, and with NO output-latency bias in either direction.
    ///
    /// <para>Callers must schedule AHEAD: only times that are still in front of
    /// <see cref="RawPlayheadMs"/> have yet to be rendered. A time that has already
    /// been rendered degrades to "as soon as possible" (no worse than
    /// <see cref="PlayClap"/>), and a badly stale one is dropped.</para>
    ///
    /// <para>Returns false when scheduling is unavailable (no engine/song, or the
    /// device has not rendered a block yet); callers may fall back to the
    /// immediate path.</para>
    /// </summary>
    bool TryScheduleClick(int clickType, double transportMs);

    /// <summary>Drops every scheduled-but-not-yet-sounded click. Called
    /// internally on seek/pause/stop/speed change; callers only need it if they
    /// scheduled clicks for a position they are about to abandon.</summary>
    void ClearScheduledClicks();

    /// <summary>
    ///     Schedules a single calibration clap a short, fixed lead ahead of the
    ///     OUTPUT render clock and reports the Stopwatch-axis host tick at which
    ///     that clap becomes AUDIBLE (the render-clock host tick of the scheduled
    ///     frame, plus the device's output latency).
    ///     <para>Unlike <see cref="PlayClap"/> — whose sound leaves the DAC one
    ///     output latency AFTER the call, while the caller only knows the call
    ///     instant — the returned tick is the real emit time. A loopback round trip
    ///     (emit → detected onset) or a tap reference measured against it therefore
    ///     carries no output-latency bias, and a WRONG latency estimate cannot skew
    ///     it either way. This is the sample-accurate path the latency wizard uses
    ///     for both the judging-offset loopback click and the audio metronome.</para>
    ///     <para>Returns false when the render clock is unavailable (no engine, or
    ///     the device has not rendered its first block); callers fall back to
    ///     <see cref="PlayClap"/> and "now" (the pre-v0.19 behaviour).</para>
    /// </summary>
    bool TryScheduleCalibrationClap(out long audibleHostTicks);
}
