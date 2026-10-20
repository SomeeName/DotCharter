namespace DotCharter.Tone.Audio.Abstractions;

/// <summary>
///     The guitar input as data: the dry capture ring (streaming and windowed
///     reads), its sample rate and stream clock, and the drop-tune pedal that
///     sits in front of it. What the tuner, the guided tuning check, Live Play's
///     note detection, the latency wizard, onboarding and the tone matcher all
///     read. One of the focused seams <see cref="IAudioPlaybackService" /> is
///     made of (docs/agents/services/audio-engine.md, "Focused interfaces for
///     view models"); PLAYER 1's input — the per-seat taps are
///     <see cref="IPlayerInputRouting" />.
///     <para>
///         <b>Threading.</b> The reads copy out of a native ring, take no
///         managed lock and never throw (0 / false when there is no engine), so
///         any thread may poll them. <see cref="ReadInputCapture(float[])" /> is
///         SINGLE-consumer — it advances a read cursor — so there is one
///         streaming reader at a time (live note detection, or the latency
///         wizard while it is open); everything else reads
///         <see cref="ReadInputCaptureWindow" />.
///     </para>
/// </summary>
public interface ILiveInputCapture
{
    /// <summary>Digital drop-tune pedal: semitone shift applied to the live
    /// input before capture/detection, the VST chain and monitoring
    /// (negative = drop). 0 = bypass with no added latency.</summary>
    double InputPitchShiftSemitones { get; set; }

    /// <summary>Drop-tune pedal engine: 0 = Rubber Band frequency-domain live
    /// shifter (default; smoothest sustain, ~50 ms latency), 1 = time-domain
    /// OLA delay-line shifter (~20 ms latency, preserves pick attacks).
    /// Applied glitch-safely on the audio thread at the next callback.</summary>
    int InputPitchShiftMode { get; set; }

    /// <summary>
    /// Drains the engine's raw (dry, pre-gain, pre-VST) input capture ring
    /// into <paramref name="destination"/>. Returns the number of samples
    /// written (0 when the engine is not running). Feed this to live note
    /// detection / tuner style consumers; poll fast enough to outrun the
    /// ~0.7 s ring.
    /// </summary>
    int ReadInputCapture(float[] destination);

    /// <summary>Like <see cref="ReadInputCapture(float[])"/> but also reports
    /// the absolute capture-stream position (total samples ever captured
    /// before <c>destination[0]</c>) so consumers can re-anchor after a
    /// ring-overrun skip.</summary>
    int ReadInputCapture(float[] destination, out ulong streamPosition);

    /// <summary>Atomically samples {total captured input samples, host ticks
    /// on the <see cref="System.Diagnostics.Stopwatch"/> timebase at the end
    /// of the same audio block}. Maps capture-stream positions to wall clock
    /// sample-accurately; false until the first input block arrives.</summary>
    bool TryGetInputCaptureSync(out ulong capturedSamples, out long hostTicks);

    /// <summary>
    /// Non-destructive read of the LATEST <paramref name="destination"/>.Length
    /// captured input samples (a fresh snapshot every call, no read cursor).
    /// Unlike the streaming <see cref="ReadInputCapture(float[])"/> — which is
    /// single-consumer — any number of windowed readers can coexist with the
    /// streaming consumer, so tuner-style UIs use THIS and never starve live
    /// note detection (or get starved by it).
    /// </summary>
    int ReadInputCaptureWindow(float[] destination);

    /// <summary>Device sample rate of the input capture stream (0 when no engine).</summary>
    int InputSampleRate { get; }
}
