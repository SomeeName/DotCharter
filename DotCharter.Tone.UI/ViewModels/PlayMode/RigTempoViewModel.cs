using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DotCharter.Tone.UI.ViewModels.PlayMode;

/// <summary>Tempo projection over this seat's live delay parameters; never enables sync implicitly.</summary>
public sealed class RigTempoViewModel : ObservableObject, IDisposable
{
    private readonly ObservableCollection<PedalNodeViewModel> _nodes;
    private readonly TimeProvider _clock;
    private readonly List<PedalNodeViewModel> _observedNodes = [];
    private readonly List<PedalParamViewModel> _observedParams = [];
    private readonly List<PedalParamViewModel> _targets = [];
    private long? _lastTap;
    private bool _disposed;

    public RigTempoViewModel(ObservableCollection<PedalNodeViewModel> nodes, TimeProvider? clock = null)
    {
        _nodes = nodes;
        _clock = clock ?? TimeProvider.System;
        TapCommand = new RelayCommand(Tap, () => HasTargets);
        _nodes.CollectionChanged += OnStructureChanged;
        Rebind();
    }

    public IRelayCommand TapCommand { get; }
    public bool HasTargets => _targets.Count > 0;
    public bool HasMixedValues => _targets.Count > 1 &&
        _targets.Skip(1).Any(p => p.Value != _targets[0].Value);

    /// <summary>First target's live BPM, or null when no delay supports tempo. Mixed values are explicit.</summary>
    public double? BPM
    {
        get => HasTargets ? _targets[0].Value : null;
        set
        {
            if (_disposed || value is not { } bpm || !double.IsFinite(bpm)) return;
            foreach (var parameter in _targets.ToArray())
                parameter.Value = (float)Math.Clamp(bpm, parameter.Minimum, parameter.Maximum);
        }
    }

    private void Tap()
    {
        if (_disposed || !HasTargets) return;
        var now = _clock.GetTimestamp();
        if (_lastTap is not { } previous) { _lastTap = now; return; }
        var interval = _clock.GetElapsedTime(previous, now).TotalMilliseconds;
        if (interval < 150) return; // bounce does not move the last accepted beat
        _lastTap = now;
        if (interval > 3000) return; // first beat of a new tapping session
        BPM = 60000 / interval;
    }

    private void OnStructureChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebind();

    private void Rebind()
    {
        UnsubscribeParameters();
        _targets.Clear();
        _lastTap = null;
        foreach (var node in _nodes.Where(n => n.ModuleTypeId == "delay").Distinct())
        {
            _observedNodes.Add(node);
            node.Params.CollectionChanged += OnStructureChanged;
            foreach (var parameter in node.Params.Where(p => p.Id is "bpm" or "sync"))
            {
                _observedParams.Add(parameter);
                parameter.PropertyChanged += OnParameterChanged;
            }
            if (node.Params.Any(p => p.Id == "sync") &&
                node.Params.FirstOrDefault(p => p.Id == "bpm") is { } bpm)
            {
                _targets.Add(bpm);
            }
        }
        NotifyTempoChanged();
    }

    private void OnParameterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PedalParamViewModel.Value)) NotifyTempoChanged();
    }

    private void NotifyTempoChanged()
    {
        OnPropertyChanged(nameof(BPM));
        OnPropertyChanged(nameof(HasTargets));
        OnPropertyChanged(nameof(HasMixedValues));
        TapCommand.NotifyCanExecuteChanged();
    }

    private void UnsubscribeParameters()
    {
        foreach (var node in _observedNodes) node.Params.CollectionChanged -= OnStructureChanged;
        foreach (var parameter in _observedParams) parameter.PropertyChanged -= OnParameterChanged;
        _observedNodes.Clear();
        _observedParams.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _nodes.CollectionChanged -= OnStructureChanged;
        UnsubscribeParameters();
        _targets.Clear();
        _lastTap = null;
        NotifyTempoChanged();
    }
}
