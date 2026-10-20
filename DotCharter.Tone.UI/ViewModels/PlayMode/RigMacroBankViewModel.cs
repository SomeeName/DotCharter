using System.ComponentModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Tone.Audio.Dsp;

namespace DotCharter.Tone.UI.ViewModels.PlayMode;

/// <summary>One UI-thread control bank for the shared pedalboard. Numeric DSP values
/// remain the source of truth; there is no second persisted macro-value store.</summary>
public sealed class RigMacroBankViewModel : ObservableObject
{
    private readonly PedalboardViewModel _rig;
    private readonly Func<IDspChainService> _source;
    private readonly Action<Action> _post;
    private IDspChainService? _chain;
    private Action? _nodesChanged;
    private readonly List<PedalParamViewModel> _observed = [];
    private readonly List<Target> _targets = [];
    private int _pending;
    private bool _writing;
    private sealed record Target(int NodeId, PedalParamViewModel Parameter, RigControlAssignment Assignment);

    internal RigMacroBankViewModel(PedalboardViewModel rig, Func<IDspChainService> source, Action<Action> post)
    {
        _rig = rig; _source = source; _post = post;
        Macros = Array.AsReadOnly(Enumerable.Range(0, 4).Select(i => new RigMacroViewModel(this, i)).ToArray());
        Editor = new(this);
        _rig.Nodes.CollectionChanged += OnNodesChanged;
        _rig.PropertyChanged += OnRigChanged;
        Refresh();
    }

    public IReadOnlyList<RigMacroViewModel> Macros { get; }
    public RigMacroViewModel Drive => Macros[0];
    public RigMacroViewModel Space => Macros[1];
    public RigMacroViewModel Motion => Macros[2];
    public RigMacroViewModel Level => Macros[3];
    public RigControlAssignmentEditorViewModel Editor { get; }
    public event Action? LearningStarted;
    public void Edit(PedalNodeViewModel? node) => Editor.Edit(node);
    internal void NotifyLearningStarted() => LearningStarted?.Invoke();
    internal IDspChainService Chain => _source();
    internal bool Contains(PedalNodeViewModel node) => _rig.Nodes.Any(n => ReferenceEquals(n, node));

    private void OnNodesChanged(object? sender, NotifyCollectionChangedEventArgs e) => Refresh();
    private void OnRigChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PedalboardViewModel.SelectedSeat)) Refresh();
        if (e.PropertyName == nameof(PedalboardViewModel.SelectedNode)) Editor.Edit(_rig.SelectedNode);
    }
    private void QueueRefresh()
    {
        if (Interlocked.Exchange(ref _pending, 1) != 0) return;
        _post(() => { Interlocked.Exchange(ref _pending, 0); Refresh(); });
    }
    internal void Refresh()
    {
        var chain = _source();
        if (!ReferenceEquals(chain, _chain))
        {
            if (_chain is not null && _nodesChanged is not null) _chain.NodesChanged -= _nodesChanged;
            _chain = chain;
            // The native service must not keep an abandoned board alive.
            var weak = new WeakReference<RigMacroBankViewModel>(this);
            _nodesChanged = () => { if (weak.TryGetTarget(out var bank)) bank.QueueRefresh(); };
            chain.NodesChanged += _nodesChanged;
            Editor.Edit(null);
        }
        foreach (var p in _observed) p.PropertyChanged -= OnParameterChanged;
        _observed.Clear(); _targets.Clear();
        foreach (var node in chain.GetNodes())
        {
            var vm = _rig.Nodes.FirstOrDefault(n => n.NodeId == node.NodeId && n.ModuleTypeId == node.ModuleTypeId);
            if (vm is null) continue;
            foreach (var a in node.ControlAssignments)
            {
                var p = vm.Params.FirstOrDefault(p => p.Id == a.ParameterId);
                if (p is null) continue;
                if (node.Values.TryGetValue(p.Id, out var current)) p.MirrorValue(current);
                _targets.Add(new(node.NodeId, p, a));
                _observed.Add(p);
                p.PropertyChanged += OnParameterChanged;
            }
        }
        Editor.Refresh();
        RefreshValues();
    }
    private void OnParameterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_writing && e.PropertyName == nameof(PedalParamViewModel.Value)) RefreshValues();
    }
    private void RefreshValues()
    {
        foreach (var macro in Macros)
        {
            var values = _targets.Where(t => t.Assignment.MacroIndex == macro.Index)
                .Select(t => t.Assignment.Max == t.Assignment.Min ? 0f :
                    Math.Clamp((t.Parameter.Value - t.Assignment.Min) / (t.Assignment.Max - t.Assignment.Min), 0, 1)).ToArray();
            macro.Mirror(values.Length > 0 ? values[0] : 0, values.Length > 0,
                values.Skip(1).Any(v => Math.Abs(v - values[0]) > .001f));
        }
    }
    internal void SetMacro(int index, float value)
    {
        if (!float.IsFinite(value)) return;
        Refresh();
        Apply(_targets.Where(t => t.Assignment.MacroIndex == index).ToArray(), Math.Clamp(value, 0, 1));
    }
    private void Apply(IReadOnlyList<Target> targets, float fraction)
    {
        _writing = true;
        try
        {
            foreach (var t in targets)
            {
                var p = t.Parameter;
                var value = t.Assignment.Min + (t.Assignment.Max - t.Assignment.Min) * fraction;
                var step = p.Step > 0 ? p.Step : p.HasValueLabels ? 1f : 0f;
                if (step > 0) value = p.Minimum + MathF.Round((value - p.Minimum) / step) * step;
                value = Math.Clamp(value, p.Minimum, p.Maximum);
                // Only mirror accepted writes; a failed engine call must not move the visible knob.
                if (Chain.SetParam(t.NodeId, p.Id, value)) p.MirrorValue(value);
            }
        }
        finally { _writing = false; RefreshValues(); }
    }
    /// <summary>Called once on the UI thread by the shared MIDI bridge. Learn consumes
    /// its first message without changing sound; subsequent matching CCs drive mapped parameters.</summary>
    public void HandleMidiControl(int channel, int controller, int value)
    {
        if (channel is < 0 or > 15 || controller is < 0 or > 127 || value is < 0 or > 127) return;
        Refresh();
        if (Editor.AcceptLearn(channel, controller)) return;
        Apply(_targets.Where(t => t.Assignment.MidiChannel == channel && t.Assignment.MidiController == controller).ToArray(), value / 127f);
    }
}

public sealed class RigMacroViewModel : ObservableObject
{
    private readonly RigMacroBankViewModel _bank;
    private float _value;
    private bool _canControl, _hasMixedValues;
    internal RigMacroViewModel(RigMacroBankViewModel bank, int index) { _bank = bank; Index = index; }
    public int Index { get; }
    public float Value { get => _value; set => _bank.SetMacro(Index, value); }
    public bool CanControl => _canControl;
    public bool HasMixedValues => _hasMixedValues;
    internal void Mirror(float value, bool canControl, bool mixed)
    {
        SetProperty(ref _value, value, nameof(Value));
        SetProperty(ref _canControl, canControl, nameof(CanControl));
        SetProperty(ref _hasMixedValues, mixed, nameof(HasMixedValues));
    }
}
