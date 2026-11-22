using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DotCharter.UI.ViewModels.Editor.Inspector;

/// <summary>The Mixer tab's sections: one entry each on its section strip (design D).</summary>
public enum MixerSection
{
    /// <summary>The playback source, the stem levels, their roles and the fader monitor; without stems, why and "Separate stems".</summary>
    Stems,

    /// <summary>The parametric EQ: response, band and routing.</summary>
    Eq,

    /// <summary>The chart sound (MIDI synth): instrument, routing and play style.</summary>
    Midi
}

/// <summary>
///     Which section of the Mixer tab is on screen. Stems is always offered (design D): a song without stems shows
///     there why, with "Separate stems". Without stems the tab opens on EQ; stems that arrive open nothing by
///     themselves, except on a tab that never had a choice (it opens on Stems the first time they exist).
/// </summary>
public sealed partial class MixerSectionsViewModel : ObservableObject
{
    private readonly StemMixerViewModel _stems;
    private MixerSection _section;
    private bool _chosen;

    public MixerSectionsViewModel(StemMixerViewModel stems)
    {
        _stems = stems;
        _section = stems.HasStems ? MixerSection.Stems : MixerSection.Eq;
        stems.PropertyChanged += OnStemsChanged;
    }

    public MixerSection Section => _section;

    public bool IsStems => _section == MixerSection.Stems;

    public bool IsEq => _section == MixerSection.Eq;

    public bool IsMidi => _section == MixerSection.Midi;

    /// <summary>
    ///     A click on a strip entry. Re-announces the flags even when the section did not change: a click on the lit
    ///     entry unchecks it locally, and its OneWay binding only re-reads on a notification.
    /// </summary>
    [RelayCommand]
    private void Choose(MixerSection section)
    {
        _section = section;
        _chosen = true;
        NotifyLayout();
    }

    private void OnStemsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(StemMixerViewModel.HasStems)) return;
        if (_stems.HasStems && !_chosen) _section = MixerSection.Stems;
        NotifyLayout();
    }

    private void NotifyLayout()
    {
        OnPropertyChanged(nameof(Section));
        OnPropertyChanged(nameof(IsStems));
        OnPropertyChanged(nameof(IsEq));
        OnPropertyChanged(nameof(IsMidi));
    }
}
