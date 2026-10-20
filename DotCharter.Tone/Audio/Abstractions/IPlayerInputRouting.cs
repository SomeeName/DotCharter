namespace DotCharter.Tone.Audio.Abstractions;

/// <summary>
///     The per-player live-input surface of the audio engine (ABI v28): how many
///     seats are live, which hardware input each one plays through, how loud each
///     one monitors, and each seat's own capture tap for live judging.
///     <para>
///         Kept OFF <see cref="IAudioPlaybackService" /> on purpose. That
///         interface is what the whole app depends on, and per-seat routing is
///         a narrow capability few callers use — a caller asks for it
///         explicitly (and gets <c>null</c> from an implementation that has no
///         engine) rather than every fake in the suite growing five members it
///         does not care about.
///     </para>
///     <para>
///         <b>The RESTING seat layout is not on this interface.</b> It comes from
///         the input configuration, is recorded by
///         <c>JuceAudioPlaybackService.SetLiveInputBaseline</c> from
///         <c>ApplyAudioConfigurationCore</c>, and is what
///         <see cref="RestoreLiveInputBaseline" /> hands back to — so the seat
///         count keeps exactly one owner even though two things want to change
///         it. See <c>LiveInputSeatLayout.FromInputConfig</c>.
///     </para>
///     <para>
///         Every member is throw-free and a no-op when no engine is up: an
///         override must never be able to take the audio engine down.
///     </para>
/// </summary>
public interface IPlayerInputRouting
{
    /// <summary>
    ///     Puts a RUN's seat layout on top of the one the input configuration
    ///     asks for: N live rigs, each seat on its own open channel at its own
    ///     monitor trim. A seat's tone rig AND its capture ring both read the
    ///     channel named here, so a player's sound and a player's judging can
    ///     never listen to different guitars.
    ///     <para>
    ///         <b>The layout is the unit, and this is the only way to raise the
    ///         seat count.</b> The three primitives it replaced
    ///         (<c>SetLiveInputPlayerCount</c> / <c>SetPlayerInputChannel</c> /
    ///         <c>SetPlayerMonitorGain</c>) let a caller leave a count and its
    ///         channels disagreeing, and made "how many seats are there" a
    ///         question with two independent answers — a run-scoped applier's, and
    ///         the input configuration's, which had no writer at all.
    ///     </para>
    /// </summary>
    void SetLiveInputOverride(LiveInputSeatLayout layout);

    /// <summary>
    ///     Ends the override and returns to the layout the input CONFIGURATION
    ///     asks for. That is not necessarily one seat: a user with two guitars on
    ///     two open channels keeps them after a run ends, exactly as they had
    ///     them before it. Idempotent, so every run-exit path can call it.
    /// </summary>
    void RestoreLiveInputBaseline();

    /// <summary>
    ///     One seat's drop-tune pedal, in semitones (negative = drop, 0 = true
    ///     bypass) — ABI v31.
    ///     <para>
    ///         Until v31 the engine held ONE shifter and ran it on the raw device
    ///         input BEFORE the per-player split, so a couch with two guitars in
    ///         different tunings could not use it at all: dropping one dropped
    ///         BOTH, and the seat that did not ask for it was detuned with nothing
    ///         on screen saying so.
    ///     </para>
    ///     <para>
    ///         Seat 0 is <see cref="ILiveInputCapture.InputPitchShiftSemitones" />
    ///         — the same relationship the capture channel has. The two are ONE
    ///         piece of engine state, so writing either moves the other's readout.
    ///     </para>
    /// </summary>
    void SetPlayerInputPitchShift(int player, double semitones);

    /// <summary>One seat's drop-tune splice-lag mode (0 Tight / 1 Balanced /
    /// 2 Smooth — <c>DropTunerEngineMap</c>). Seat 0 is
    /// <see cref="ILiveInputCapture.InputPitchShiftMode" />.</summary>
    void SetPlayerInputPitchShiftMode(int player, int mode);

    /// <summary>
    ///     Whether the engine behind this implementation actually has the v31
    ///     per-seat drop-tune entry points.
    ///     <para>
    ///         A degraded or older native DLL has the ABI-version check refuse the
    ///         engine outright, so in practice this answers "is there an engine at
    ///         all" — but a surface that offers a per-seat drop tune must ASK
    ///         rather than assume, and ship visibly disabled with its reason when
    ///         the answer is no. Same rule that gates the parametric EQ.
    ///     </para>
    /// </summary>
    bool SupportsPerPlayerDropTune { get; }

    /// <summary>Drains one seat's live-input capture ring — the tap live note
    /// detection judges that player from. Each seat has its own cursor, so two
    /// detectors never steal each other's samples. Returns 0 when no engine is
    /// up.</summary>
    int ReadPlayerInputCaptureStream(int player, Span<float> destination, out ulong streamPosition);

    /// <summary>
    ///     One seat's latest capture WINDOW — non-destructive, no cursor, always
    ///     the most recent <c>destination.Length</c> samples that seat captured.
    ///     This is <see cref="ILiveInputCapture.ReadInputCaptureWindow" /> for
    ///     a seat, and it is what a TUNER, a level meter or an analyser reads.
    ///     <para>
    ///         <b>Not the streaming read above.</b> The window and the stream have
    ///         SEPARATE cursors in the engine precisely so a tuner and a judge can
    ///         watch one seat at once; draining the stream to feed a needle would
    ///         steal the samples that seat is being scored from.
    ///     </para>
    ///     <para>
    ///         Returns 0 when no engine is up, when the seat is out of range, or
    ///         before that seat's ring has filled — so a caller that requires a
    ///         full window gets an honest "nothing here yet" rather than a
    ///         zero-padded one.
    ///     </para>
    /// </summary>
    int ReadPlayerInputCaptureWindow(int player, float[] destination);

    /// <summary>
    ///     How many live-input rigs the engine is CURRENTLY running — the count
    ///     of whichever layout is in force, the run's
    ///     <see cref="SetLiveInputOverride" /> or the input configuration's
    ///     baseline underneath it.
    ///     <para>
    ///         A seat at or past this number has no signal in it: its capture ring
    ///         is never written, because the audio callback only walks the live
    ///         seats. Surfaces that offer a seat picker read this so a picker
    ///         cannot present a permanently-silent seat as one that is merely
    ///         quiet.
    ///     </para>
    /// </summary>
    int LiveInputPlayerCount { get; }

    /// <summary>
    ///     One seat's {captured samples ↔ host ticks} clock pair, on the same
    ///     Stopwatch (QPC) timebase the global
    ///     <see cref="ILiveInputCapture.TryGetInputCaptureSync" /> reports.
    ///     <para>
    ///         <b>This is the member that made judging a second seat possible</b>,
    ///         and it is not interchangeable with the global one: each seat's ring
    ///         has its OWN cursor, so mapping seat 1's detection event through seat
    ///         0's sync pair dates every attack by the difference between two
    ///         independent drains — which is a timing error that lands straight in
    ///         the tier the player is paid at. Returns false when no engine is up,
    ///         when the seat is out of range, or before the first captured block.
    ///     </para>
    /// </summary>
    bool TryGetPlayerInputCaptureSync(int player, out ulong capturedSamples, out long hostTicks);

    /// <summary>Per-seat post-rig peak meter; resets on read.</summary>
    float GetAndResetPlayerInputPeak(int player);
}
