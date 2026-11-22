using DotCharter.Services.Infrastructure.Configuration;
using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Tone.Audio.Midi;
using DotCharter.Tone.Diagnostics;

namespace DotCharter.Presentation.Screens.Settings;

/// <summary>What one device enumeration found, read together off the UI thread.</summary>
internal sealed record AudioDeviceList(
    List<string> WasapiOutputs,
    List<string> AsioOutputs,
    List<string> WasapiInputs,
    List<string> AsioInputs,
    IReadOnlyList<MidiOutputDeviceInfo> Midi,
    int Jacks,
    IReadOnlyList<string> JackNames);

/// <summary>
///     The device enumeration <see cref="AudioSettingsViewModel.Open" /> runs on the pool, cut loose on Dispose.
/// </summary>
internal sealed class AudioDeviceProbe(IAudioDeviceControl devices, IUiDispatcher ui)
{
    private readonly CuttableProbe<AudioDeviceList> _probe = new(ui);

    /// <summary>Starts a read whose rows go to <paramref name="land" /> on the UI thread; a read still running is dropped.</summary>
    public Task Start(AppConfig config, Action<AudioDeviceList> land) =>
        _probe.Start((devices, config), static args => Read(args.devices, args.config), land);

    /// <summary>Ends every read with its owner: a queued one never runs, a running one lands nowhere.</summary>
    public void Cancel() => _probe.Cancel();

    private static AudioDeviceList Read(IAudioDeviceControl devices, AppConfig config)
    {
        devices.EnsureWasapiDevicesProbed(config);
        devices.EnsureAsioDevicesProbed(config);
        var wasapiOuts = devices.GetWasapiOutputDevices();
        var asioOuts = devices.GetAsioDevices();
        var wasapiIns = devices.GetWasapiInputDevices();
        var asioIns = devices.GetAsioInputDevices();
        var midi = devices.GetMidiOutputDevices();
        // The SELECTED input driver's jack count: asking a driver the session will never open is a wasted round trip.
        var inputIsAsio = string.Equals(config.InputDriver, "ASIO", StringComparison.OrdinalIgnoreCase);
        var (jacks, jackNames) = InputJackPicker.ReadChannels(devices,
            inputIsAsio ? "ASIO" : "WASAPI", inputIsAsio ? config.AsioInputDeviceName : config.WasapiInputDeviceName);
        return new AudioDeviceList(wasapiOuts, asioOuts, wasapiIns, asioIns, midi, jacks, jackNames);
    }
}
