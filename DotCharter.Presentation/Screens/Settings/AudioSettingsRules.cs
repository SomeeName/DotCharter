using System.Globalization;
using DotCharter.Services.Infrastructure.Configuration;

namespace DotCharter.Presentation.Screens.Settings;

/// <summary>
///     The pure half of Settings ▸ Audio: the offered formats, how a stored value
///     snaps onto them, how a saved device name resolves back to a row, and
///     which saves need the engine rebuilt.
/// </summary>
public static class AudioSettingsRules
{
    public static readonly int[] SampleRateChoices = [44100, 48000, 88200, 96000, 176400, 192000];

    /// <summary>Powers of two plus the 3× series (96/192/384/768) that Focusrite, RME and Steinberg drivers run natively.</summary>
    public static readonly int[] BufferSizeChoices = [32, 64, 96, 128, 192, 256, 384, 512, 768, 1024, 2048];

    public static int NormalizeSampleRate(int sampleRate) =>
        SampleRateChoices.Contains(sampleRate) ? sampleRate : 44100;

    public static int NormalizeBufferSize(int bufferSize)
    {
        if (bufferSize <= 0) return new AppConfig().AudioBufferFrames;
        var best = BufferSizeChoices[0];
        foreach (var choice in BufferSizeChoices)
        {
            if (Math.Abs(bufferSize - choice) < Math.Abs(bufferSize - best))
                best = choice;
        }

        return best;
    }

    public static double FramesToMilliseconds(int frames, int sampleRate) =>
        Math.Clamp(frames, 16, 8192) * 1000.0 / (sampleRate > 0 ? sampleRate : 44100);

    /// <summary>
    ///     The row a saved device selects: its NAME first (stable across
    ///     reorders), then the stored index, clamped so the dropdown never shows
    ///     a blank header while the engine quietly uses another device. -1 is
    ///     "Default".
    /// </summary>
    public static int ResolveIndex(IReadOnlyList<string> items, string? savedName, int savedIndex)
    {
        if (!string.IsNullOrWhiteSpace(savedName))
        {
            for (var i = 0; i < items.Count; i++)
            {
                if (string.Equals(items[i], savedName, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
        }

        if (items.Count == 0) return -1;
        if (savedIndex < 0) return savedIndex;
        return savedIndex < items.Count ? savedIndex : 0;
    }

    public static string RateLabel(int sampleRate) =>
        (sampleRate / 1000.0).ToString("0.#", CultureInfo.CurrentCulture) + " kHz";

    public static string LatencyLabel(int frames, int sampleRate) =>
        FramesToMilliseconds(frames, sampleRate).ToString("0.0", CultureInfo.CurrentCulture) + " ms";

    public static string PickName(IReadOnlyList<string> items, int index) =>
        index >= 0 && index < items.Count ? items[index] : string.Empty;

    /// <summary>The fields <see cref="HasRoutingChanged" /> compares, copied out of a live config.</summary>
    public static AppConfig CloneRoutingFields(AppConfig src) => new()
    {
        AudioDriver = src.AudioDriver,
        WasapiOutputDevice = src.WasapiOutputDevice,
        AsioOutputDevice = src.AsioOutputDevice,
        WasapiOutputDeviceName = src.WasapiOutputDeviceName,
        AsioOutputDeviceName = src.AsioOutputDeviceName,
        WasapiInputDeviceName = src.WasapiInputDeviceName,
        AsioInputDeviceName = src.AsioInputDeviceName,
        StreamingMirrorDeviceName = src.StreamingMirrorDeviceName,
        AsioOutputChannelLeft = src.AsioOutputChannelLeft,
        AsioOutputChannelRight = src.AsioOutputChannelRight,
        EnableInput = src.EnableInput,
        InputDriver = src.InputDriver,
        WasapiInputDevice = src.WasapiInputDevice,
        AsioInputDevice = src.AsioInputDevice,
        AsioInputChannelLeft = src.AsioInputChannelLeft,
        AsioInputChannelRight = src.AsioInputChannelRight,
        AsioInputMono = src.AsioInputMono,
        InputChannelsAreSeparateGuitars = src.InputChannelsAreSeparateGuitars,
        EnableHighProcessPriority = src.EnableHighProcessPriority,
        OutOfProcessVst = src.OutOfProcessVst,
        AudioBufferFrames = src.AudioBufferFrames,
        AudioSampleRate = src.AudioSampleRate,
        EnableStreamingMirror = src.EnableStreamingMirror,
        StreamingMirrorDevice = src.StreamingMirrorDevice,
        MidiOutputDevice = src.MidiOutputDevice,
        AutoFollowDeviceAudioSettings = src.AutoFollowDeviceAudioSettings
    };

    /// <summary>
    ///     Whether a save changed anything only <c>ApplyAudioConfiguration</c>
    ///     applies. The device NAMES are what the engine opens on, so a new device
    ///     at the same row number counts; the MIDI port is opened from that path
    ///     alone; the seat layout decides one live rig or two. The delay trims are
    ///     deliberately absent: they are pushed live and need no device change.
    /// </summary>
    public static bool HasRoutingChanged(AppConfig oldCfg, AppConfig newCfg) =>
        oldCfg.AudioDriver != newCfg.AudioDriver
        || oldCfg.WasapiOutputDevice != newCfg.WasapiOutputDevice
        || oldCfg.AsioOutputDevice != newCfg.AsioOutputDevice
        || !DeviceNameEquals(oldCfg.WasapiOutputDeviceName, newCfg.WasapiOutputDeviceName)
        || !DeviceNameEquals(oldCfg.AsioOutputDeviceName, newCfg.AsioOutputDeviceName)
        || !DeviceNameEquals(oldCfg.WasapiInputDeviceName, newCfg.WasapiInputDeviceName)
        || !DeviceNameEquals(oldCfg.AsioInputDeviceName, newCfg.AsioInputDeviceName)
        || !DeviceNameEquals(oldCfg.StreamingMirrorDeviceName, newCfg.StreamingMirrorDeviceName)
        || oldCfg.MidiOutputDevice != newCfg.MidiOutputDevice
        || oldCfg.AsioOutputChannelLeft != newCfg.AsioOutputChannelLeft
        || oldCfg.AsioOutputChannelRight != newCfg.AsioOutputChannelRight
        || oldCfg.EnableInput != newCfg.EnableInput
        || oldCfg.InputDriver != newCfg.InputDriver
        || oldCfg.WasapiInputDevice != newCfg.WasapiInputDevice
        || oldCfg.AsioInputDevice != newCfg.AsioInputDevice
        || oldCfg.AsioInputChannelLeft != newCfg.AsioInputChannelLeft
        || oldCfg.AsioInputChannelRight != newCfg.AsioInputChannelRight
        || oldCfg.AsioInputMono != newCfg.AsioInputMono
        || oldCfg.InputChannelsAreSeparateGuitars != newCfg.InputChannelsAreSeparateGuitars
        || oldCfg.EnableHighProcessPriority != newCfg.EnableHighProcessPriority
        || oldCfg.OutOfProcessVst != newCfg.OutOfProcessVst
        || oldCfg.AudioBufferFrames != newCfg.AudioBufferFrames
        || oldCfg.AudioSampleRate != newCfg.AudioSampleRate
        || oldCfg.EnableStreamingMirror != newCfg.EnableStreamingMirror
        || oldCfg.StreamingMirrorDevice != newCfg.StreamingMirrorDevice
        || oldCfg.AutoFollowDeviceAudioSettings != newCfg.AutoFollowDeviceAudioSettings;

    /// <summary>Ordinal, case-insensitive, with null and "" both "no device recorded" — how the engine matches.</summary>
    public static bool DeviceNameEquals(string? a, string? b) =>
        string.Equals(a ?? string.Empty, b ?? string.Empty, StringComparison.OrdinalIgnoreCase);
}
