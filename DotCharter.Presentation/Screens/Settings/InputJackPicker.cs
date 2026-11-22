using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Localization;
using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Tone.Diagnostics;

namespace DotCharter.Presentation.Screens.Settings;

/// <summary>
///     The guitar's jack as a pick over the capture device's inputs, for
///     <see cref="AudioSettingsViewModel" />, the rig footer and the first-run
///     wizard. The row IS the left input channel: <paramref name="read" /> gives
///     both channels, and a picked row goes to <paramref name="pick" />.
/// </summary>
internal sealed partial class InputJackPicker(
    IAudioDeviceControl devices,
    IUiDispatcher ui,
    ILocalizationService localization,
    Func<(int Left, int Right)> read,
    Action<int> pick) : ObservableObject
{
    private readonly CuttableProbe<(int Count, IReadOnlyList<string> Names)> _probe = new(ui);
    private bool _rebuilding;
    private IReadOnlyList<string> _names = [];

    [ObservableProperty] private int _selectedIndex = -1;

    /// <summary>The capture device's input count behind <see cref="Options" />; 0 = not known yet.</summary>
    public int JackCount { get; private set; }

    public ObservableCollection<InputJackOption> Options { get; } = [];

    /// <summary>The latest capture device's jack-count read; tests await it.</summary>
    public Task Probe { get; private set; } = Task.CompletedTask;

    /// <summary>Rebuilds the rows for a device with that many inputs (0 = unknown, the generic list),
    /// labelled with the driver's names for them when it has any; a read still running is dropped.</summary>
    public void Refresh(int deviceJackCount, IReadOnlyList<string>? channelNames = null)
    {
        _probe.Drop();
        Rebuild(deviceJackCount, channelNames);
    }

    private void Rebuild(int deviceJackCount, IReadOnlyList<string>? channelNames)
    {
        _rebuilding = true;
        try
        {
            JackCount = deviceJackCount;
            _names = channelNames ?? [];
            var (left, right) = read();
            Options.Clear();
            foreach (var o in GuitarInputChannels.JackOptions(localization, deviceJackCount, _names, left, right))
                Options.Add(o);
            SelectedIndex = IndexOf(left);
        }
        finally
        {
            _rebuilding = false;
        }

        OnPropertyChanged(nameof(JackCount));
    }

    /// <summary>Re-selects the row of a jack written elsewhere, rebuilding when the list lacks it.</summary>
    public void Sync(int jack)
    {
        if (_rebuilding) return;
        if (IndexOf(jack) < 0) Rebuild(JackCount, _names);
        else SelectedIndex = IndexOf(jack);
    }

    /// <summary>
    ///     Reads the picked device of <paramref name="driver" /> off the UI thread; a failed read leaves the rows
    ///     alone, and every read started before this one is dropped, even when no device is picked.
    /// </summary>
    public void ProbeDevice(string driver, IReadOnlyList<string> candidates, int index)
    {
        _probe.Drop();
        if (index < 0 || index >= candidates.Count) return;
        Probe = _probe.Start((devices, driver, name: candidates[index]),
            static args => ReadChannels(args.devices, args.driver, args.name), Land);
    }

    /// <summary>Ends the read in flight with its owner: it lands nowhere, and a queued one never runs.</summary>
    public void Cancel() => _probe.Cancel();

    private void Land((int Count, IReadOnlyList<string> Names) read)
    {
        if (read.Count > 0) Rebuild(read.Count, read.Names);
    }

    /// <summary>The device's jack count and the driver's names for them, read together; a failed read is (0, no names).</summary>
    public (int Count, IReadOnlyList<string> Names) ReadChannels(string driver, string name) => ReadChannels(devices, driver, name);

    /// <summary>The same read through <paramref name="devices" /> alone, for a worker that must hold no picker.</summary>
    public static (int Count, IReadOnlyList<string> Names) ReadChannels(IAudioDeviceControl devices, string driver, string name)
    {
        try
        {
            return (devices.GetInputDeviceChannelCount(driver, name), devices.GetInputChannelNames(driver, name));
        }
        catch
        {
            return (0, []);
        }
    }

    private int IndexOf(int jack)
    {
        for (var i = 0; i < Options.Count; i++)
        {
            if (Options[i].Jack == jack)
                return i;
        }

        return -1;
    }

    partial void OnSelectedIndexChanged(int value)
    {
        if (_rebuilding || value < 0 || value >= Options.Count) return;
        pick(Options[value].Jack);
    }
}
