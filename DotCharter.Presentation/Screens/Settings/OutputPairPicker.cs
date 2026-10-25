using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Localization;
using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Tone.Diagnostics;

namespace DotCharter.Presentation.Screens.Settings;

/// <summary>
///     The master mix's pair as a pick over the playback device's outputs, for
///     <see cref="AudioSettingsViewModel" /> and the first-run wizard. The row IS
///     the two output channels: <paramref name="read" /> and <paramref name="write" />
///     mirror it both ways.
/// </summary>
internal sealed partial class OutputPairPicker(
    IAudioDeviceControl devices,
    IUiDispatcher ui,
    ILocalizationService localization,
    Func<(int Left, int Right)> read,
    Action<int, int> write) : ObservableObject
{
    private bool _rebuilding;
    private IReadOnlyList<string> _names = [];
    private int _probeGeneration;
    private string? _probeKey;

    [ObservableProperty] private int _selectedIndex = -1;

    /// <summary>The playback device's output count behind <see cref="Options" />; 0 = not known yet.</summary>
    public int DeviceChannelCount { get; private set; }

    public ObservableCollection<OutputPairOption> Options { get; } = [];

    /// <summary>The latest playback device's output-count read; tests await it.</summary>
    public Task Probe { get; private set; } = Task.CompletedTask;

    /// <summary>Rebuilds the rows for a device with that many outputs (0 = unknown, the generic list),
    /// labelled with the driver's names for them when it has any.</summary>
    public void Refresh(int deviceOutputCount, IReadOnlyList<string>? channelNames = null)
    {
        _rebuilding = true;
        try
        {
            DeviceChannelCount = deviceOutputCount;
            _names = channelNames ?? [];
            // An unknown count forgets which device was read, so the next probe asks again.
            if (deviceOutputCount <= 0) _probeKey = null;
            var (left, right) = read();
            Options.Clear();
            foreach (var o in OutputChannelPairs.PairOptions(localization, deviceOutputCount, left, right, _names))
                Options.Add(o);
            SelectedIndex = IndexOf(left, right);
        }
        finally
        {
            _rebuilding = false;
        }

        OnPropertyChanged(nameof(DeviceChannelCount));
    }

    // Rebuilt, not just re-selected: the two channels are written one after the other, and a pair that
    // existed for one write only (the new left beside the old right) must not stay in the list.
    public void Sync()
    {
        if (!_rebuilding) Refresh(DeviceChannelCount, _names);
    }

    /// <summary>Reads the device's output count off the UI thread, unless that device was read already.</summary>
    public void ProbeDevice(string driver, string name)
    {
        var key = driver + "|" + name;
        if (key == _probeKey) return;
        _probeKey = key;
        // A probe for a device the user has since left must not overwrite the rows of the current one.
        var generation = ++_probeGeneration;
        Probe = Task.Run(() =>
        {
            int count;
            IReadOnlyList<string> names;
            try
            {
                count = devices.GetOutputDeviceChannelCount(driver, name);
                names = devices.GetOutputChannelNames(driver, name);
            }
            catch
            {
                (count, names) = (0, []);
            }

            ui.PostNormal(() =>
            {
                if (generation != _probeGeneration) return;
                if (count > 0) Refresh(count, names);
                else _probeKey = null;
            });
        });
    }

    private int IndexOf(int left, int right)
    {
        for (var i = 0; i < Options.Count; i++)
        {
            if (Options[i].Left == left && Options[i].Right == right)
                return i;
        }

        return -1;
    }

    partial void OnSelectedIndexChanged(int value)
    {
        if (_rebuilding || value < 0 || value >= Options.Count) return;
        var pair = Options[value];
        // Writing the two channels one after the other must not re-sync the row to the half-written pair.
        _rebuilding = true;
        try
        {
            write(pair.Left, pair.Right);
        }
        finally
        {
            _rebuilding = false;
        }
    }
}
