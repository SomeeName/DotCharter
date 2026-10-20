using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Tone.Audio.Dsp;

namespace DotCharter.Tone.UI.ViewModels.PlayMode;

public enum RigAssignmentStatus { None, Saved, Removed, Invalid, Unavailable, Learning }

/// <summary>Draft editor for a real numeric parameter. Labels for indices/status belong to the view.</summary>
public sealed partial class RigControlAssignmentEditorViewModel : ObservableObject
{
    private readonly RigMacroBankViewModel _bank;
    private PedalNodeViewModel? _node;
    private IReadOnlyList<RigControlAssignment> _last = Array.Empty<RigControlAssignment>();
    internal RigControlAssignmentEditorViewModel(RigMacroBankViewModel bank) => _bank = bank;
    public IReadOnlyList<PedalParamViewModel> Parameters => _node is null ? Array.Empty<PedalParamViewModel>() : _node.Params;
    public bool CanEdit => _node is not null && _bank.Contains(_node) && SelectedParameter is not null && Parameters.Contains(SelectedParameter);
    [ObservableProperty] private PedalParamViewModel? _selectedParameter;
    [ObservableProperty] private int _macroIndex = -1;
    [ObservableProperty] private float _min;
    [ObservableProperty] private float _max;
    [ObservableProperty] private int _midiChannel = -1;
    [ObservableProperty] private int _midiController = -1;
    [ObservableProperty] private bool _isLearning;
    [ObservableProperty] private RigAssignmentStatus _status;
    /// <summary>Raised for every accepted save/remove, including clear-MIDI and learn.
    /// Unlike Status changes, successive successful edits each notify subscribers.</summary>
    public event Action? AssignmentCommitted;
    public bool HasAssignment => SelectedParameter is { } p && _last.Any(a => a.ParameterId == p.Id);

    internal void Edit(PedalNodeViewModel? node)
    {
        if (ReferenceEquals(_node, node)) return;
        CancelMidiLearn();
        _node = node;
        _last = Array.Empty<RigControlAssignment>();
        OnPropertyChanged(nameof(Parameters));
        SelectedParameter = Parameters.FirstOrDefault();
        Refresh();
        OnPropertyChanged(nameof(CanEdit));
    }
    partial void OnSelectedParameterChanged(PedalParamViewModel? value)
    {
        CancelMidiLearn();
        LoadDraft();
        OnPropertyChanged(nameof(CanEdit));
    }
    internal void Refresh()
    {
        if (_node is not null && !_bank.Contains(_node)) { Edit(null); return; }
        var assignments = _node is null ? Array.Empty<RigControlAssignment>() :
            _bank.Chain.GetNodes().FirstOrDefault(n => n.NodeId == _node.NodeId)?.ControlAssignments ?? Array.Empty<RigControlAssignment>();
        if (_last.SequenceEqual(assignments)) return;
        CancelMidiLearn();
        _last = assignments;
        LoadDraft();
    }
    private void LoadDraft()
    {
        var a = _last.FirstOrDefault(a => a.ParameterId == SelectedParameter?.Id);
        MacroIndex = a?.MacroIndex ?? -1;
        Min = a?.Min ?? SelectedParameter?.Minimum ?? 0;
        Max = a?.Max ?? SelectedParameter?.Maximum ?? 1;
        MidiChannel = a?.MidiChannel ?? -1;
        MidiController = a?.MidiController ?? -1;
        OnPropertyChanged(nameof(HasAssignment));
    }
    [RelayCommand]
    private void Assign()
    {
        if (!CanEdit || _node is null || SelectedParameter is null) { Status = RigAssignmentStatus.Unavailable; return; }
        if (MacroIndex == -1 && MidiChannel == -1 && MidiController == -1) { Remove(); return; }
        var current = _bank.Chain.GetNodes().FirstOrDefault(n => n.NodeId == _node.NodeId);
        if (current is null) { Status = RigAssignmentStatus.Unavailable; return; }
        var values = current.ControlAssignments.Where(a => a.ParameterId != SelectedParameter.Id).ToList();
        values.Add(new(SelectedParameter.Id, MacroIndex, Min, Max, MidiChannel, MidiController));
        if (!_bank.Chain.SetControlAssignments(_node.NodeId, values)) { Status = RigAssignmentStatus.Invalid; return; }
        Status = RigAssignmentStatus.Saved;
        _bank.Refresh();
        AssignmentCommitted?.Invoke();
    }
    [RelayCommand]
    private void Remove()
    {
        if (!CanEdit || _node is null || SelectedParameter is null) { Status = RigAssignmentStatus.Unavailable; return; }
        CancelMidiLearn();
        var current = _bank.Chain.GetNodes().FirstOrDefault(n => n.NodeId == _node.NodeId);
        if (current is null || !_bank.Chain.SetControlAssignments(_node.NodeId,
            current.ControlAssignments.Where(a => a.ParameterId != SelectedParameter.Id).ToArray()))
        { Status = RigAssignmentStatus.Unavailable; return; }
        Status = RigAssignmentStatus.Removed;
        _bank.Refresh();
        AssignmentCommitted?.Invoke();
    }
    [RelayCommand]
    private void BeginMidiLearn()
    {
        if (!CanEdit) { Status = RigAssignmentStatus.Unavailable; return; }
        _bank.NotifyLearningStarted();
        IsLearning = true;
        Status = RigAssignmentStatus.Learning;
    }
    [RelayCommand]
    private void CancelMidiLearn()
    {
        IsLearning = false;
        if (Status == RigAssignmentStatus.Learning) Status = RigAssignmentStatus.None;
    }
    [RelayCommand]
    private void ClearMidi()
    {
        CancelMidiLearn();
        MidiChannel = MidiController = -1;
        Assign();
    }
    internal bool AcceptLearn(int channel, int controller)
    {
        if (!IsLearning) return false;
        IsLearning = false;
        MidiChannel = channel; MidiController = controller;
        Assign();
        return true;
    }
}
