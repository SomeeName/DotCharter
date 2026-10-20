using System.ComponentModel;
using System.Windows.Input;
using DotCharter.Tone.Audio.Abstractions;

namespace DotCharter.Presentation.Contracts.Settings;

/// <summary>
///     Settings ▸ Audio: the output and capture devices, the buffer and rate,
///     the two manual timing trims, the guitar jack, the drop-tuner quality and
///     the streaming mirror.
///     <para>
///         <b>Two surfaces bind this, not one.</b> The editor's Audio tab shows
///         both device rows side by side; Play Mode's settings modal shows one
///         row per direction and swaps the list behind it. That is why the
///         per-direction predicates (<see cref="IsWasapiOutput" /> and its three
///         siblings) are on the contract at all — a compiled binding has no
///         <c>==</c>, so the modal cannot compare a driver string in markup.
///     </para>
/// </summary>
public interface IAudioSettingsScreen : INotifyPropertyChanged
{
    // ── the flow ─────────────────────────────────────────────────────────

    /// <summary>
    ///     Reads the persisted configuration and starts the device probe.
    ///     Cheap settings apply synchronously so the surface is never blank;
    ///     enumerating devices round-trips a USB driver and can take 200 ms+,
    ///     so it runs off the dispatcher and the lists fill in afterwards.
    /// </summary>
    void Open();

    /// <summary>
    ///     Whether the collapsed Advanced section (streaming mirror, MIDI out)
    ///     is open. The search opens it while a query keeps any row inside it —
    ///     a hit the user cannot see is not a hit.
    /// </summary>
    bool IsAdvancedOpen { get; set; }

    /// <summary>Row and group keys the current search filters OUT. Empty when there is no query.</summary>
    IReadOnlySet<string> HiddenKeys { get; }

    // ── the readout: what the rows below add up to, formatted ────────────

    /// <summary>The output device the selected driver plays through, or a "no device yet" line.</summary>
    string ReadoutDevice { get; }

    /// <summary>The sample rate, e.g. "48 kHz".</summary>
    string ReadoutRate { get; }

    /// <summary>The buffer, e.g. "256 samples".</summary>
    string ReadoutBuffer { get; }

    /// <summary>What that buffer costs in time, e.g. "5.3 ms".</summary>
    string ReadoutLatency { get; }

    // ── output ───────────────────────────────────────────────────────────

    IReadOnlyList<string> DriverOptions { get; }

    string SelectedDriver { get; set; }

    /// <summary>True while WASAPI is the output driver — the predicate the modal binds instead of comparing strings.</summary>
    bool IsWasapiOutput { get; }

    bool IsAsioOutput { get; }

    IReadOnlyList<string> WasapiDevices { get; }

    int SelectedWasapiOutputIndex { get; set; }

    IReadOnlyList<string> AsioDevices { get; }

    int SelectedAsioOutputIndex { get; set; }

    int AsioOutputChannelLeft { get; set; }

    int AsioOutputChannelRight { get; set; }

    // ── capture ──────────────────────────────────────────────────────────

    bool EnableInput { get; set; }

    string SelectedInputDriver { get; set; }

    bool IsWasapiInput { get; }

    bool IsAsioInput { get; }

    IReadOnlyList<string> WasapiInputDevicesList { get; }

    int SelectedWasapiInputIndex { get; set; }

    IReadOnlyList<string> AsioInputDevicesList { get; }

    int SelectedAsioInputIndex { get; set; }

    /// <summary>ASIO addresses capture channels by index; WASAPI does not, so the two index rows are hidden there.</summary>
    bool AreInputChannelIndexesUsed { get; }

    int AsioInputChannelRight { get; set; }

    bool AsioInputMono { get; set; }

    /// <summary>
    ///     The two open input channels are two GUITARS, one per seat — not one
    ///     stereo instrument. It is an explicit setting rather than an inference
    ///     from a second channel being open, because a second open channel is
    ///     just as likely to be a stereo pickup. Mono opens ONE channel, so this
    ///     cannot be true alongside it and is not persisted alongside it either:
    ///     storing the impossible pair resurrects it the moment Mono is
    ///     unticked, which reads as the setting changing by itself.
    /// </summary>
    bool InputChannelsAreSeparateGuitars { get; set; }

    /// <summary>
    ///     The guitar's jack as a PICK over the capture device's channels — the
    ///     same rows the rig footer and the first-run wizard show. It mirrors
    ///     the left input channel both ways: the row IS the box rather than a
    ///     second field, so Save keeps writing exactly what it wrote before.
    /// </summary>
    IReadOnlyList<InputJackOption> InputJackOptions { get; }

    int SelectedInputJackIndex { get; set; }

    // ── clock ────────────────────────────────────────────────────────────

    IReadOnlyList<int> SampleRateOptions { get; }

    int SampleRate { get; set; }

    IReadOnlyList<int> BufferSizeOptions { get; }

    int BufferSizeFrames { get; set; }

    /// <summary>What the chosen buffer and rate work out to in milliseconds.</summary>
    string BufferLatencyDescription { get; }

    /// <summary>What the RUNNING engine negotiated, which is not always what was asked for. Empty before the engine is up.</summary>
    string ActualBufferDescription { get; }

    /// <summary>
    ///     Follow whatever the driver negotiates instead of asking for a rate
    ///     and buffer. The persisted value is snapped to the nearest power of
    ///     two on the way out, so a driver that rounds its own answer up cannot
    ///     compound across runs (256 → 320 → 384 → …).
    /// </summary>
    bool AutoFollowDeviceSettings { get; set; }

    /// <summary>The inverse, for the rows auto-follow disables.</summary>
    bool IsManualAudioConfig { get; }

    // ── timing ───────────────────────────────────────────────────────────

    /// <summary>
    ///     An EXTRA manual trim on top of the device latency, which is already
    ///     compensated automatically on every position report. Its correct
    ///     default is 0: a non-zero value is a deliberate taste or display-lag
    ///     adjustment, never a latency measurement.
    /// </summary>
    double AudioDelayMs { get; set; }

    /// <summary>
    ///     What the device currently reports as its output latency, shown and
    ///     nothing more. It deliberately does not write
    ///     <see cref="AudioDelayMs" />: the playback service already subtracts
    ///     the device latency from every position report, so copying the same
    ///     number in makes the total 2 × L and pushes the playhead a full output
    ///     latency BEHIND the audio — the opposite of what the button is for.
    /// </summary>
    double MeasuredAudioDelayMs { get; }

    ICommand MeasureLatencyCommand { get; }

    /// <summary>Opens the full latency / A-V calibration flow, which is what actually dials a trim in.</summary>
    ICommand OpenLatencyWizardCommand { get; }

    /// <summary>Refine the Live Play judging offset from each run's residual bias. Off by default — it changes how hits are judged.</summary>
    bool AutoCalibrateTiming { get; set; }

    /// <summary>Tee every run's dry capture to the profile's takes folder. Off by default: it writes audio to disk on every run.</summary>
    bool TakeRecorderEnabled { get; set; }

    // ── engine ───────────────────────────────────────────────────────────

    bool EnableHighProcessPriority { get; set; }

    bool OutOfProcessVst { get; set; }

    /// <summary>Quality/latency for the drop-tuner, one engine rather than a choice of engines: 0 Tight, 1 Balanced, 2 Smooth.</summary>
    IReadOnlyList<string> DropTunerEngineOptions { get; }

    int SelectedDropTunerEngineIndex { get; set; }

    /// <summary>True while either direction is on ASIO — what gates the control-panel row.</summary>
    bool IsAsioActive { get; }

    /// <summary>Opens the driver's own control panel, parented to the main window when there is one.</summary>
    ICommand OpenAsioControlPanelCommand { get; }

    /// <summary>Why the panel refused, or null. A driver without one is the ordinary case.</summary>
    string? AsioControlPanelError { get; }

    // ── extras ───────────────────────────────────────────────────────────

    bool EnableStreamingMirror { get; set; }

    IReadOnlyList<string> StreamingMirrorDevices { get; }

    int SelectedStreamingMirrorIndex { get; set; }

    /// <summary>
    ///     Row 0 is the MIDI Mapper. The selection resolves against the list
    ///     this screen BUILT, never a fresh enumeration — those are two
    ///     different lists whenever a device was plugged or unplugged in
    ///     between, and an empty list is not a selection at all.
    /// </summary>
    IReadOnlyList<string> MidiOutputDevices { get; }

    int SelectedMidiOutputIndex { get; set; }
}
