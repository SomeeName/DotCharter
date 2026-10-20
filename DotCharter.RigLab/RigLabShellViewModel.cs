using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.RigLab;

public enum RigLabTab
{
    Rig,
    Plugins,
    Audio,
    Ai
}

/// <summary>
///     The window's view model: which of the four tabs is showing, and the
///     four things they show. The rig is the default and the reason the app
///     exists; Plugins, Audio and AI are setup.
/// </summary>
public sealed partial class RigLabShellViewModel(
    AmpFxRigViewModel rig, VstRackViewModel plugins, AudioSettingsViewModel audio, AiSettingsViewModel ai)
    : ObservableObject
{
    public AmpFxRigViewModel Rig { get; } = rig;
    public VstRackViewModel Plugins { get; } = plugins;
    public AudioSettingsViewModel Audio { get; } = audio;
    public AiSettingsViewModel Ai { get; } = ai;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRigTab), nameof(IsPluginsTab), nameof(IsAudioTab), nameof(IsAiTab))]
    private RigLabTab _tab = RigLabTab.Rig;

    public bool IsRigTab
    {
        get => Tab == RigLabTab.Rig;
        set { if (value) Tab = RigLabTab.Rig; }
    }

    public bool IsPluginsTab
    {
        get => Tab == RigLabTab.Plugins;
        set { if (value) Tab = RigLabTab.Plugins; }
    }

    public bool IsAudioTab
    {
        get => Tab == RigLabTab.Audio;
        set { if (value) Tab = RigLabTab.Audio; }
    }

    public bool IsAiTab
    {
        get => Tab == RigLabTab.Ai;
        set { if (value) Tab = RigLabTab.Ai; }
    }

    partial void OnTabChanged(RigLabTab value)
    {
        // The device lists are a live enumeration: refresh on the way in, so an
        // interface plugged in since launch shows up.
        if (value == RigLabTab.Audio) _ = Audio.RefreshDevicesAsync();
    }
}
