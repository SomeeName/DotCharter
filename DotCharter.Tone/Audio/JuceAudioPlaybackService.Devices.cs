// Partial: audio-device enumeration and probing (ASIO + WASAPI) and the WASAPI share-mode labelling.
using DotCharter.Tone.Audio.Engine;
using JuceSharp;
using JuceDriver = JuceSharp.AudioDriver;

namespace DotCharter.Tone.Audio;

public sealed partial class JuceAudioPlaybackService
{
    public DeviceManager DeviceManager { get; private set; } = new();

    /// <summary>Remembers the last block size handed to
    /// <see cref="DeviceBufferFramesChanged" /> so the event fires once per real
    /// change. See <see cref="DeviceBufferReporter" /> for why it exists at all.</summary>
    private readonly DeviceBufferReporter _bufferReporter = new();

    /// <summary>
    ///     Publishes the block size the DEVICE settled on, whenever it differs
    ///     from the last one published. The three callers are the three moments a
    ///     device's block size can change: a full engine bring-up, an in-place
    ///     <c>Reconfigure</c>, and the user closing the driver's own ASIO control
    ///     panel.
    ///     <para>
    ///         <b><see cref="DeviceBufferFramesChanged" /> must be raised</b> — never
    ///         hidden behind a <c>#pragma warning disable CS0067</c> — because
    ///         <c>AudioSettingsViewModel</c> and the status bar both subscribe.
    ///         Otherwise the settings dropdown would show the REQUESTED size for the
    ///         life of the session, which is exactly what that view model's own
    ///         comment ("showing the requested value while the driver runs at a
    ///         different value is misleading") says must not happen.
    ///     </para>
    ///     Throw-free: a diagnostic must never cost the caller its device change.
    /// </summary>
    private void ReportDeviceBufferFrames(JuceAudioEngine? engine)
    {
        if (engine == null) return;
        int frames;
        try { frames = engine.BufferFrames; }
        catch { return; }
        if (!_bufferReporter.TryObserve(frames)) return;
        try { DeviceBufferFramesChanged?.Invoke(this, frames); }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Audio,
                $"DeviceBufferFramesChanged subscriber threw: {ex.Message}");
        }
    }

    /// <summary>
    ///     Puts the process on the class the "Process priority" row asks for —
    ///     the row's only consumer; see <see cref="ProcessPriorityPolicy" /> for
    ///     what it promised and how long it promised it for nothing.
    ///     <para>
    ///         Best-effort by design: a hardened / containerised host can refuse
    ///         the write, and losing a latency optimisation must never cost the
    ///         user their audio device.
    ///     </para>
    /// </summary>
    private void ApplyProcessPriority(bool enableHigh)
    {
        var desired = ProcessPriorityPolicy.DesiredFor(enableHigh);
        try
        {
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            if (!ProcessPriorityPolicy.ShouldApply(self.PriorityClass, desired)) return;
            self.PriorityClass = desired;
            _logger?.Info(AppLogCategory.Audio, $"Process priority set to {desired}.");
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Audio,
                $"Could not set the process priority to {desired}: {ex.Message}");
        }
    }

    public void EnsureAsioDevicesProbed(RigConfig config)
    {
        var e = _engine;
        if (e == null) return;
        var outs = e.EnumerateOutputDevicesLive()
            .Where(d => d.Driver == JuceDriver.Asio).Select(d => d.Name).ToArray();
        var ins = e.EnumerateInputDevicesLive()
            .Where(d => d.Driver == JuceDriver.Asio).Select(d => d.Name).ToArray();
        DeviceManager = DeviceManager with { AsioOutputDevices = outs, AsioInputDevices = ins };
    }

    public void EnsureWasapiDevicesProbed(RigConfig config)
    {
        var e = _engine;
        if (e == null) return;
        var outs = LabelWasapiModes(e.EnumerateOutputDevicesLive()
            .Where(d => d.Driver == JuceDriver.Wasapi));
        var ins = LabelWasapiModes(e.EnumerateInputDevicesLive()
            .Where(d => d.Driver == JuceDriver.Wasapi));
        DeviceManager = DeviceManager with { WasapiOutputDevices = outs, WasapiInputDevices = ins };
    }

    public bool TryOpenAsioControlPanel(IntPtr parentWindowHandle, out string? error)
    {
        var e = _engine;
        if (e == null) { error = "Audio engine not initialized."; return false; }
        var ok = e.TryShowAsioControlPanel(parentWindowHandle, out error);
        // The panel is MODAL, so by the time it returns the user may have pinned
        // the driver to a different block size — the case AudioSettingsViewModel's
        // buffer-row comment is written about. Publish whatever the device is on
        // now; the reporter drops it when nothing moved.
        ReportDeviceBufferFrames(e);
        return ok;
    }

    public List<string> GetWasapiOutputDevices()
        => LabelWasapiModes((_engine?.EnumerateOutputDevicesLive() ?? Array.Empty<JuceAudioDevice>())
            .Where(d => d.Driver == JuceDriver.Wasapi)).ToList();

    public List<string> GetAsioDevices()
        => DedupeDeviceNames((_engine?.EnumerateOutputDevicesLive() ?? Array.Empty<JuceAudioDevice>())
            .Where(d => d.Driver == JuceDriver.Asio).Select(d => d.Name)).ToList();

    public List<string> GetWasapiInputDevices()
        => LabelWasapiModes((_engine?.EnumerateInputDevicesLive() ?? Array.Empty<JuceAudioDevice>())
            .Where(d => d.Driver == JuceDriver.Wasapi)).ToList();

    public List<string> GetAsioInputDevices()
        => DedupeDeviceNames((_engine?.EnumerateInputDevicesLive() ?? Array.Empty<JuceAudioDevice>())
            .Where(d => d.Driver == JuceDriver.Asio).Select(d => d.Name)).ToList();

    public IReadOnlyList<Midi.MidiOutputDeviceInfo> GetMidiOutputDevices() => Midi.WinMMMidiOutput.EnumerateDevices();

    public int GetInputDeviceChannelCount(string driver, string deviceName) =>
        DeviceChannelCounts.Input(_engine, driver, deviceName);

    public int GetOutputDeviceChannelCount(string driver, string deviceName) =>
        DeviceChannelCounts.Output(_engine, driver, deviceName);

    public IReadOnlyList<string> GetOutputChannelNames(string driver, string deviceName) =>
        DeviceChannelCounts.Names(_engine, driver, deviceName, input: false);

    public IReadOnlyList<string> GetInputChannelNames(string driver, string deviceName) =>
        DeviceChannelCounts.Names(_engine, driver, deviceName, input: true);

    // Suffix used in user-facing WASAPI labels.
    private const string WasapiSharedSuffix = " (Shared)";

    private const string WasapiExclusiveSuffix = " (Exclusive)";

    private const string WasapiLowLatencySuffix = " (Low Latency)";

    // JUCE registers WASAPI as three AudioIODeviceTypes (Shared, Exclusive,
    // Low-Latency); each physical device appears once per type. We surface
    // all three modes to the user with disambiguating suffixes when more
    // than one mode is actually present for a given device. The bare name
    // (no suffix) always denotes Shared so saved configs keep
    // working.
    private static string[] LabelWasapiModes(IEnumerable<JuceAudioDevice> devices)
    {
        var byName = new Dictionary<string, List<JuceAudioDevice>>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var d in devices)
        {
            if (string.IsNullOrEmpty(d.Name)) continue;
            if (!byName.TryGetValue(d.Name, out var list))
            {
                list = [];
                byName[d.Name] = list;
                order.Add(d.Name);
            }
            list.Add(d);
        }

        var result = new List<string>(order.Count * 3);
        foreach (var name in order)
        {
            var group = byName[name];
            var hasShared = group.Any(d => WasapiModeSuffix(d.TypeName) == WasapiSharedSuffix);
            var hasExclusive = group.Any(d => WasapiModeSuffix(d.TypeName) == WasapiExclusiveSuffix);
            var hasLowLatency = group.Any(d => WasapiModeSuffix(d.TypeName) == WasapiLowLatencySuffix);

            var modeCount = (hasShared ? 1 : 0) + (hasExclusive ? 1 : 0) + (hasLowLatency ? 1 : 0);
            if (modeCount <= 1)
            {
                result.Add(name);
                continue;
            }
            if (hasShared) result.Add(name);
            if (hasExclusive) result.Add(name + WasapiExclusiveSuffix);
            if (hasLowLatency) result.Add(name + WasapiLowLatencySuffix);
        }
        return result.ToArray();
    }

    private static string WasapiModeSuffix(string typeName)
    {
        if (string.IsNullOrEmpty(typeName)) return WasapiSharedSuffix;
        if (typeName.IndexOf("Exclusive", StringComparison.OrdinalIgnoreCase) >= 0) return WasapiExclusiveSuffix;
        if (typeName.IndexOf("Low Latency", StringComparison.OrdinalIgnoreCase) >= 0) return WasapiLowLatencySuffix;
        return WasapiSharedSuffix;
    }

    // Inverse of LabelWasapiModes: turn a user-facing label back into the
    // bare device name + the JUCE AudioIODeviceType name that the native
    // engine needs to pick the right WASAPI mode.
    internal static (string DeviceName, string TypeName) ParseWasapiLabel(string label)
    {
        if (string.IsNullOrEmpty(label)) return (string.Empty, string.Empty);
        if (label.EndsWith(WasapiExclusiveSuffix, StringComparison.Ordinal))
            return (label.Substring(0, label.Length - WasapiExclusiveSuffix.Length), "Windows Audio (Exclusive Mode)");
        if (label.EndsWith(WasapiLowLatencySuffix, StringComparison.Ordinal))
            return (label.Substring(0, label.Length - WasapiLowLatencySuffix.Length), "Windows Audio (Low Latency Mode)");
        if (label.EndsWith(WasapiSharedSuffix, StringComparison.Ordinal))
            return (label.Substring(0, label.Length - WasapiSharedSuffix.Length), "Windows Audio");
        return (label, string.Empty);
    }

    // Used for ASIO lists (driver disambiguation isn't a thing there).
    private static string[] DedupeDeviceNames(IEnumerable<string> names)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var n in names)
        {
            if (string.IsNullOrEmpty(n)) continue;
            if (seen.Add(n)) result.Add(n);
        }
        return result.ToArray();
    }
}
