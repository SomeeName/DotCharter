using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DotCharter.Tone.UI.ViewModels.PlayMode;

/// <summary>
/// One branch's real merger parameters. Mute/solo are native persisted switches,
/// never destructive edits of level or effect bypass. Missing merger/capability
/// exposes a disabled mixer instead of pretending an implicit merge is adjustable.
/// </summary>
public sealed class RigLaneMixerViewModel : ObservableObject, IDisposable
{
    private PedalNodeViewModel? _merger;
    private PedalParamViewModel? _mute;
    private PedalParamViewModel? _solo;
    private readonly int _branch;
    private readonly PropertyChangedEventHandler _changed;

    public RigLaneMixerViewModel(int branch)
    {
        _branch = branch;
        // A surviving rig node must not root a discarded screen. Explicit detach
        // handles refolds; the weak target also covers a whole screen being closed.
        var weak = new WeakReference<RigLaneMixerViewModel>(this);
        PropertyChangedEventHandler? handler = null;
        handler = (sender, args) =>
        {
            if (weak.TryGetTarget(out var owner)) owner.OnParameterChanged(args);
            else if (sender is INotifyPropertyChanged source) source.PropertyChanged -= handler;
        };
        _changed = handler;
    }

    public PedalParamViewModel? Level { get; private set; }
    public PedalParamViewModel? Pan { get; private set; }
    public string LevelText => Level is not { Value: > 0 } ? "−∞ dB" : $"{20 * Math.Log10(Level.Value):+0.0;-0.0;0.0} dB";
    public string PanText => Pan is null || Math.Abs(Pan.Value) < .005 ? "C" : $"{(Pan.Value < 0 ? "L" : "R")} {Math.Abs(Pan.Value) * 100:0}";
    public bool HasExplicitMerger => _merger is not null;
    public bool CanMix => Level is not null && Pan is not null;
    public bool CanMuteSolo => CanMix && _mute is not null && _solo is not null;
    public bool RequiresUpdatedEngine => HasExplicitMerger && !CanMuteSolo;
    public bool IsMuted
    {
        get => _mute?.Value >= .5f;
        set { if (CanMuteSolo) _mute!.Value = value ? 1 : 0; }
    }
    public bool IsSoloed
    {
        get => _solo?.Value >= .5f;
        set { if (CanMuteSolo) _solo!.Value = value ? 1 : 0; }
    }

    internal void Attach(PedalNodeViewModel? merger)
    {
        if (ReferenceEquals(_merger, merger)) return;
        Detach();
        _merger = merger;
        PedalParamViewModel? Param(string stem) => merger?.Params.FirstOrDefault(p => p.Id == stem + _branch);
        Level = Param("level");
        Pan = Param("pan");
        _mute = Param("mute");
        _solo = Param("solo");
        Level?.PropertyChanged += _changed;
        Pan?.PropertyChanged += _changed;
        _mute?.PropertyChanged += _changed;
        _solo?.PropertyChanged += _changed;
        OnPropertyChanged(nameof(Level));
        OnPropertyChanged(nameof(Pan));
        OnPropertyChanged(nameof(HasExplicitMerger));
        OnPropertyChanged(nameof(CanMix));
        OnPropertyChanged(nameof(CanMuteSolo));
        OnPropertyChanged(nameof(RequiresUpdatedEngine));
        OnParameterChanged(new PropertyChangedEventArgs(nameof(PedalParamViewModel.Value)));
    }

    private void OnParameterChanged(PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(PedalParamViewModel.Value)) return;
        OnPropertyChanged(nameof(LevelText));
        OnPropertyChanged(nameof(PanText));
        OnPropertyChanged(nameof(IsMuted));
        OnPropertyChanged(nameof(IsSoloed));
    }

    private void Detach()
    {
        Level?.PropertyChanged -= _changed;
        Pan?.PropertyChanged -= _changed;
        _mute?.PropertyChanged -= _changed;
        _solo?.PropertyChanged -= _changed;
    }
    public void Dispose() => Attach(null);
}
