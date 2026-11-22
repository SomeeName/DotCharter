using static DotCharter.Presentation.Screens.Settings.AudioSettingsRules;

namespace DotCharter.Presentation.Screens.Settings;

/// <summary>What the Audio tab's footer counts: the tracked properties by value, the picked devices by NAME as one entry.</summary>
internal sealed class AudioSettingsTracking(AudioSettingsViewModel owner)
{
    /// <summary>
    ///     The settings counted by value. Device ROWS are absent on purpose: the
    ///     name is what persists, and each index lands on its value when the
    ///     worker's probe returns, which is not an edit.
    /// </summary>
    internal static readonly string[] Tracked =
    [
        nameof(AudioSettingsViewModel.SelectedDriver), nameof(AudioSettingsViewModel.AsioOutputChannelLeft),
        nameof(AudioSettingsViewModel.AsioOutputChannelRight), nameof(AudioSettingsViewModel.EnableInput),
        nameof(AudioSettingsViewModel.SelectedInputDriver), nameof(AudioSettingsViewModel.AsioInputChannelLeft),
        nameof(AudioSettingsViewModel.AsioInputChannelRight), nameof(AudioSettingsViewModel.AsioInputMono),
        nameof(AudioSettingsViewModel.InputChannelsAreSeparateGuitars), nameof(AudioSettingsViewModel.SelectedDropTunerEngineIndex),
        nameof(AudioSettingsViewModel.AutoFollowDeviceSettings), nameof(AudioSettingsViewModel.SampleRate),
        nameof(AudioSettingsViewModel.BufferSizeFrames), nameof(AudioSettingsViewModel.AudioDelayMs),
        nameof(AudioSettingsViewModel.AutoCalibrateTiming), nameof(AudioSettingsViewModel.TakeRecorderEnabled),
        nameof(AudioSettingsViewModel.EnableHighProcessPriority), nameof(AudioSettingsViewModel.OutOfProcessVst),
        nameof(AudioSettingsViewModel.EnableStreamingMirror), nameof(AudioSettingsViewModel.MidiDelayMs)
    ];

    /// <summary>The picked devices as one setting: Save persists the names, and the probe that lands the rows rebaselines it.</summary>
    internal const string DeviceSelectionKey = "SelectedDevices";

    private static readonly string[] DeviceRows =
    [
        nameof(AudioSettingsViewModel.SelectedWasapiOutputIndex), nameof(AudioSettingsViewModel.SelectedAsioOutputIndex),
        nameof(AudioSettingsViewModel.SelectedWasapiInputIndex), nameof(AudioSettingsViewModel.SelectedAsioInputIndex),
        nameof(AudioSettingsViewModel.SelectedStreamingMirrorIndex), nameof(AudioSettingsViewModel.SelectedMidiOutputIndex)
    ];

    private readonly SettingsChangeSet _changes = new();
    private bool _tracking;

    public IReadOnlyList<string> Differences() => _changes.Differences();

    /// <summary>Starts tracking on first use and takes every current value as the baseline.</summary>
    public void Rebaseline()
    {
        if (!_tracking) Track();
        _changes.Rebaseline();
    }

    /// <summary>The probe put each device row on its stored device: that is the baseline, not an edit. False before tracking starts.</summary>
    public bool RebaselineDevices()
    {
        if (!_tracking) return false;
        _changes.Rebaseline(DeviceSelectionKey);
        return true;
    }

    /// <summary>Whether a change of <paramref name="property" /> can move the count.</summary>
    public bool Counts(string property) => _tracking && (Tracked.Contains(property) || DeviceRows.Contains(property));

    private void Track()
    {
        _tracking = true;
        foreach (var name in Tracked)
        {
            var property = typeof(AudioSettingsViewModel).GetProperty(name)!;
            _changes.Track(name, () => property.GetValue(owner));
        }

        _changes.Track(DeviceSelectionKey, DeviceSelection);
    }

    private string DeviceSelection() => string.Join("\n",
        PickName(owner.WasapiDevices, owner.SelectedWasapiOutputIndex), PickName(owner.AsioDevices, owner.SelectedAsioOutputIndex),
        PickName(owner.WasapiInputDevicesList, owner.SelectedWasapiInputIndex),
        PickName(owner.AsioInputDevicesList, owner.SelectedAsioInputIndex),
        PickName(owner.StreamingMirrorDevices, owner.SelectedStreamingMirrorIndex),
        PickName(owner.MidiOutputDevices, owner.SelectedMidiOutputIndex));
}
