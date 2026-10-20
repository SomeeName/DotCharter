using DotCharter.Tone.Audio.Midi;

namespace DotCharter.Tone.Audio.Abstractions;

/// <summary>
///     The audio device: which drivers and devices exist, what the open one
///     runs at (sample rate, buffer, latency), the latency offsets the user
///     calibrates, applying a new configuration — and whether the device is
///     still there. One of the focused seams <see cref="IAudioPlaybackService" />
///     is made of (docs/agents/services/audio-engine.md, "Focused interfaces for
///     view models").
///     <para>
///         <b>Health lives here, not in an interface of its own.</b> The draft
///         plan had a separate <c>IPlaybackHealth</c>; every caller that reads
///         the device-lost state also reads the device's rate and buffer
///         (<c>StatusBarViewModel</c>, <c>DiagnosticsViewModel</c>), so the split
///         bought nothing but a second constructor parameter.
///     </para>
///     <para>
///         <b>Threading.</b> <see cref="DeviceLost" />,
///         <see cref="DeviceReconnected" /> and
///         <see cref="DeviceBufferFramesChanged" /> may arrive on any thread (the
///         device watchdog's timer, an engine rebuild) — marshal before touching
///         UI state. <see cref="ApplyAudioConfiguration" />,
///         the two probes and <see cref="TryOpenAsioControlPanel" /> may rebuild or
///         stop the engine and are slow — never call them on a hot path.
///     </para>
/// </summary>
public interface IAudioDeviceControl
{
    double AudioDelayMs { get; set; }
    double EstimatedOutputLatencyMs { get; }
    double MeasuredOutputLatencyMs { get; }
    int ActiveSampleRate { get; }
    int ActualBufferFrames { get; }
    double MidiOffsetMs { get; set; }
    bool IsDeviceLost { get; }

    /// <summary>
    ///     The audio callback's load, measured INSIDE the callback by the native
    ///     engine (<c>dca_engine_get_callback_load</c>, ABI v46): what the rig's
    ///     CPU readout shows. False when no engine is running, or the
    ///     implementation has no callback to measure — the readout then says so
    ///     instead of printing a number nothing computed. Two atomic loads; safe
    ///     to poll from the UI thread.
    /// </summary>
    bool TryGetCallbackLoad(out AudioCallbackLoad load)
    {
        load = default;
        return false;
    }

    event EventHandler<int>? DeviceBufferFramesChanged;
    event EventHandler? DeviceLost;
    event EventHandler<AudioDeviceReconnectedEventArgs>? DeviceReconnected;

    void ApplyAudioConfiguration(RigConfig config);
    void EnsureAsioDevicesProbed(RigConfig config);
    void EnsureWasapiDevicesProbed(RigConfig config);

    /// <summary>
    /// Stops the engine, opens the native ASIO control panel for the
    /// configured device, then restarts the engine.
    /// </summary>
    bool TryOpenAsioControlPanel(IntPtr parentWindowHandle, out string? error);

    List<string> GetWasapiOutputDevices();
    List<string> GetAsioDevices();
    List<string> GetWasapiInputDevices();
    List<string> GetAsioInputDevices();

    /// <summary>
    ///     How many input JACKS the named capture device exposes
    ///     (<c>MaxInputChannels</c> from the live enumeration), or 0 when the
    ///     engine is down or the device is not found. Feeds the guitar-input jack
    ///     pickers (<see cref="GuitarInputChannels.JackOptions" />); a 0 makes
    ///     them fall back to a generic list rather than to nothing.
    /// </summary>
    int GetInputDeviceChannelCount(string driver, string deviceName);

    /// <summary>
    ///     The MIDI output ports, row 0 the MIDI Mapper at device index -1. A live
    ///     winmm enumeration, so call it off the UI thread; on the seam so a test
    ///     never round-trips the machine's real MIDI drivers.
    /// </summary>
    IReadOnlyList<MidiOutputDeviceInfo> GetMidiOutputDevices();
}
