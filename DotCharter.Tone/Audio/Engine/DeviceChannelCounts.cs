using JuceSharp;
using JuceDriver = JuceSharp.AudioDriver;

namespace DotCharter.Tone.Audio.Engine;

/// <summary>
///     How many channels a named device exposes, read off the engine's LIVE
///     enumeration (a USB round trip — call it off the UI thread). Feeds the
///     guitar-jack and output-pair pickers and the engine config's
///     past-the-device fallbacks; 0 means "not known" (no engine, no such device,
///     an enumeration that threw) and every caller treats it as such.
/// </summary>
internal static class DeviceChannelCounts
{
    public static int Input(JuceAudioEngine? engine, string driver, string deviceName) =>
        Count(engine, driver, deviceName, input: true);

    public static int Output(JuceAudioEngine? engine, string driver, string deviceName) =>
        Count(engine, driver, deviceName, input: false);

    /// <summary>The OPEN device's channel names when <paramref name="deviceName" /> is
    /// that device; empty otherwise, or when the engine is down or the call throws.</summary>
    public static IReadOnlyList<string> Names(JuceAudioEngine? engine, string driver, string deviceName, bool input)
    {
        if (engine == null || string.IsNullOrWhiteSpace(deviceName)) return [];
        var isAsio = string.Equals(driver, "ASIO", StringComparison.OrdinalIgnoreCase);
        var wanted = isAsio ? deviceName : JuceAudioPlaybackService.ParseWasapiLabel(deviceName).DeviceName;
        try
        {
            return engine.GetChannelNames(input, wanted);
        }
        catch
        {
            return [];
        }
    }

    private static int Count(JuceAudioEngine? engine, string driver, string deviceName, bool input)
    {
        if (engine == null || string.IsNullOrWhiteSpace(deviceName)) return 0;
        var isAsio = string.Equals(driver, "ASIO", StringComparison.OrdinalIgnoreCase);
        var wanted = isAsio ? deviceName : JuceAudioPlaybackService.ParseWasapiLabel(deviceName).DeviceName;
        try
        {
            return (input ? engine.EnumerateInputDevicesLive() : engine.EnumerateOutputDevicesLive())
                .Where(d => d.Driver == (isAsio ? JuceDriver.Asio : JuceDriver.Wasapi))
                .Where(d => string.Equals(d.Name, wanted, StringComparison.OrdinalIgnoreCase))
                .Select(d => input ? d.MaxInputChannels : d.MaxOutputChannels)
                .DefaultIfEmpty(0)
                .Max();
        }
        catch
        {
            return 0;
        }
    }
}
