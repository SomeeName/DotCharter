using CommunityToolkit.Mvvm.Input;
using DotCharter.UI.ViewModels.Editor.Inspector;

namespace DotCharter.UI.ViewModels.Editor;

/// <summary>What the song plays from (inspector design D's "Playback source").</summary>
public enum StemPlaybackSource
{
    /// <summary>The separated stems; the original mix strip is muted.</summary>
    Stems,

    /// <summary>The untouched original mix; the stems are muted.</summary>
    OriginalMix,

    /// <summary>The song has no separated stems yet.</summary>
    NoStems
}

/// <summary>One entry of the Playback source choice.</summary>
public sealed record StemPlaybackChoice(StemPlaybackSource Source, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
///     Design D's Playback source and its Reset levels question. The source is a VIEW of the mutes the mixer already
///     keeps: Stems mutes the original strip (and opens every stem when none is audible), Original mix opens the
///     original strip, which silences the stems through the strips' own exclusion. Both go through the strips' mute
///     path, so the project keeps <see cref="ChartProject.OriginalMixMuted" /> and each <see cref="Stem.Muted" />, and an
///     engine rebuild replays them from the strips (<c>ApplyToEngine</c>).
/// </summary>
public sealed partial class StemMixerViewModel
{
    private bool _deferProjectChanged;
    private bool _projectChangePending;

    /// <summary>"Reset 5 stem levels to 0 dB? Mute and solo stay." under the Levels card; a project switch withdraws it.</summary>
    public InspectorPendingAction ResetLevelsConfirm { get; }

    /// <summary>The choices the song has: Stems and Original mix with stems, No stems without.</summary>
    public IReadOnlyList<StemPlaybackChoice> PlaybackChoices => HasStems
        ? [Choice(StemPlaybackSource.Stems), Choice(StemPlaybackSource.OriginalMix)]
        : [Choice(StemPlaybackSource.NoStems)];

    /// <summary>What plays now, read from the strips.</summary>
    public StemPlaybackSource PlaybackSource
    {
        get
        {
            if (!HasStems) return StemPlaybackSource.NoStems;
            return Tracks.FirstOrDefault(t => t.IsMasterMix) is { IsMuted: false } ? StemPlaybackSource.OriginalMix : StemPlaybackSource.Stems;
        }
        set => ChoosePlayback(value);
    }

    /// <summary>The bound entry of <see cref="PlaybackChoices" />.</summary>
    public StemPlaybackChoice? PlaybackChoice
    {
        get => PlaybackChoices.FirstOrDefault(c => c.Source == PlaybackSource);
        set
        {
            if (value != null) PlaybackSource = value.Source;
        }
    }

    /// <summary>The original mix plays: the stem faders apply again on Stems.</summary>
    public bool PlaysOriginalMix => PlaybackSource == StemPlaybackSource.OriginalMix;

    private StemPlaybackChoice Choice(StemPlaybackSource source) => new(source, source switch
    {
        StemPlaybackSource.Stems => _localization["Mixer_Playback_Stems"],
        StemPlaybackSource.OriginalMix => _localization["Mixer_Playback_Original"],
        _ => _localization["Mixer_Playback_NoStems"]
    });

    private void ChoosePlayback(StemPlaybackSource source)
    {
        var master = Tracks.FirstOrDefault(t => t.IsMasterMix);
        if (!HasStems || master == null || source == PlaybackSource) return;
        if (source == StemPlaybackSource.OriginalMix)
        {
            master.IsMuted = false;
        }
        else if (source == StemPlaybackSource.Stems)
        {
            master.IsMuted = true;
            if (Tracks.All(t => t.IsMasterMix || t.IsMuted))
            {
                foreach (var track in Tracks.Where(t => !t.IsMasterMix)) track.IsMuted = false;
            }
        }

        NotifyPlayback();
    }

    private void NotifyPlayback()
    {
        OnPropertyChanged(nameof(PlaybackSource));
        OnPropertyChanged(nameof(PlaybackChoice));
        OnPropertyChanged(nameof(PlaysOriginalMix));
    }

    /// <summary>The song's waveform as the timeline draws it, for the Playback and roles card's mini strip.</summary>
    public WaveformSamples? Waveform => _state.WaveformData;

    /// <summary>"Mixed waveform" while it follows the mix, "Source waveform" while it is the file's.</summary>
    public string WaveformCaption => _localization[WaveformFollowsMix ? "Mixer_Waveform_Mixed" : "Mixer_Waveform_Source"];

    // the coordinator publishes from its worker: the bound strip hears it on the UI thread, once per publish
    private void NotifyWaveform()
    {
        OnPropertyChanged(nameof(Waveform));
        OnPropertyChanged(nameof(WaveformCaption));
    }

    partial void OnHasStemsChanged(bool value)
    {
        _ = value;
        OnPropertyChanged(nameof(PlaybackChoices));
        NotifyPlayback();
    }

    /// <summary>"Reset levels" (design D): asks, then every strip's fader back to unity; mute and solo stay. ONE project change.</summary>
    [RelayCommand]
    private void ResetLevels() => ResetLevelsConfirm.Ask(
        _localization.Plural("Mixer_Stems_ResetLevelsAskOne", "Mixer_Stems_ResetLevelsAsk", Tracks.Count), RunResetLevels);

    private void RunResetLevels()
    {
        _deferProjectChanged = true;
        try
        {
            foreach (var track in Tracks) track.ResetLevel();
        }
        finally
        {
            _deferProjectChanged = false;
        }

        if (_projectChangePending && _state.ActiveProject is { } project) _state.NotifyProjectChanged(project);
        _projectChangePending = false;
    }

    /// <summary>A strip's persisted edit: the project is dirty (once per Reset levels).</summary>
    internal void NotifyStemChanged(Stem stem)
    {
        if (_state.ActiveProject is not { } project || !project.Stems.Contains(stem)) return;
        if (_deferProjectChanged) _projectChangePending = true;
        else _state.NotifyProjectChanged(project);
    }
}
