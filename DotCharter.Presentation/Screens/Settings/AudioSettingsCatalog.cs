using DotCharter.Localization;

namespace DotCharter.Presentation.Screens.Settings;

/// <summary>
///     What Settings ▸ Audio declares to the cross-tab search: five groups, the
///     last two behind the Advanced disclosure. Each row key is the
///     <c>ConverterParameter</c> the view hides that row by.
/// </summary>
public static class AudioSettingsCatalog
{
    public const string Prefix = "audio.";

    /// <summary>Streaming mirror and MIDI out sit behind the Advanced disclosure.</summary>
    public const int FirstAdvancedGroup = 3;

    public static string GroupKey(int index) => Prefix + "group." + index;

    public static IReadOnlyList<SettingsSearchGroup> Groups(ILocalizationService loc)
    {
        SettingsSearchRow Row(string key, string label, string description) =>
            new(Prefix + key, loc[label], loc[description]);

        SettingsSearchGroup Group(string title, params SettingsSearchRow[] rows) =>
            new(loc[title], loc[title + "_Subtitle"], rows);

        return
        [
            Group("Settings_OutputRouting",
                Row("driver", "Settings_AudioDriver", "Settings_Tip_AudioDriver"),
                Row("device", "Settings_Device", "Settings_Tip_Device"),
                Row("outputChannels", "Settings_OutChannels", "Settings_Tip_OutChannels")),
            Group("Settings_InstrumentInput",
                Row("enableInput", "Settings_EnableLiveInput_RowLabel", "Settings_Tip_EnableLiveInput"),
                Row("inputDriver", "Settings_InputDriver", "Settings_Tip_InputDriver"),
                Row("inputDevice", "Settings_InputDevice", "Settings_Tip_InputDevice"),
                Row("inputChannels", "Settings_InChannels", "Settings_Tip_AsioInChannels"),
                Row("dropTuner", "DropTuner_Engine", "DropTuner_Engine_Tip")),
            Group("Settings_CalibrationSync",
                Row("autoFollow", "Settings_AutoFollowDevice", "Settings_AutoFollowDeviceDescription"),
                Row("sampleRate", "Settings_SampleRate", "Settings_Tip_SampleRate"),
                Row("bufferSize", "Settings_BufferSize", "Settings_Tip_BufferSize"),
                Row("asioPanel", "Settings_AsioDriver", "Settings_AsioControlPanelHint"),
                Row("audioDelay", "Settings_AudioDelay", "Settings_AudioDelayHelp"),
                Row("autoCalibrate", "Settings_AutoCalibrateTiming", "Settings_AutoCalibrateTimingDescription"),
                Row("calibration", "Settings_Calibration", "Settings_CalibrateLatencyHelp"),
                Row("takeRecorder", "Settings_TakeRecorder", "Settings_TakeRecorderDescription"),
                Row("highPriority", "Settings_HighPriority", "Settings_HighPriorityDescription"),
                Row("vstSandbox", "Settings_VstSandbox", "Settings_VstSandboxDescription")),
            Group("Settings_StreamingMirror",
                Row("mirror", "Settings_StreamingMirrorToggle", "Settings_StreamingMirrorTip"),
                Row("mirrorDevice", "Settings_StreamingMirrorDevice", "Settings_Tip_StreamingMirrorDevice")),
            Group("Settings_MidiOutput",
                Row("midiDevice", "Settings_MidiDevice", "Settings_Tip_MidiDevice"))
        ];
    }
}
