using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Tone.UI.ViewModels.Shell;

namespace DotCharter.UI.ViewModels.Editor.Inspector;

/// <summary>
/// Mixer ▸ MIDI synth ▸ Playing style: how the chart synth plays THIS
///     song — whether hand shapes ring, and whether a single note or a chord is
///     held over a short gap into the next note, with "short" as a note value.
///     One rule set for guitar arrangements and one for bass; the Guitar | Bass
///     switch picks which one the rows edit and follows the arrangement being
///     edited. Every change is an undoable <see cref="SetSynthPlayStyleAction" />
///     on the project, which saves it; the synth re-uploads on its own because the
///     style is part of its fingerprint.
/// </summary>
public sealed partial class ChartSynthPlayStyleViewModel : ViewModelBase, IDisposable
{
    private readonly ProjectState _projectState;
    private readonly IHistoryService _history;
    private bool _editingBass;

    // True while Refresh raises: a picker write that arrives then is the control
    // echoing the old selection, never a pick.
    private bool _refreshing;

    public ChartSynthPlayStyleViewModel(ProjectState projectState, IHistoryService history,
        ILocalizationService localization)
    {
        _projectState = projectState;
        _history = history;
        NoProjectText = localization["Mixer_ChartSynth_Style_NoProject"];
        _editingBass = IsBassArrangement;
        _history.HistoryChanged += Refresh;
        _projectState.PropertyChanged += OnProjectStateChanged;
    }

    /// <summary>The note values a gap is measured against, longest first. ONE
    /// instance: a ComboBox handed a new list re-selects and writes back.</summary>
    public IReadOnlyList<ChartSynthChoice<int>> GapChoices { get; } =
        ChartSynthPlayRules.GapDivisors.Select(d => new ChartSynthChoice<int>(d, "1/" + d)).ToArray();

    /// <summary>A project is open: the rows have a style to edit.</summary>
    public bool HasProject => _projectState.ActiveProject != null;

    /// <summary>Shown instead of the rows while no project is open.</summary>
    public string NoProjectText { get; }

    /// <summary>The rows edit the guitar rules.</summary>
    public bool EditingGuitar
    {
        get => !_editingBass;
        set
        {
            if (value) SetEditingBass(false);
        }
    }

    /// <summary>The rows edit the bass rules.</summary>
    public bool EditingBass
    {
        get => _editingBass;
        set
        {
            if (value) SetEditingBass(true);
        }
    }

    /// <summary>Notes inside a hand shape ring over the notes after them.</summary>
    public bool HandShapesRing
    {
        get => Rules?.HandShapesRing ?? true;
        set => Change(r => r.HandShapesRing = value);
    }

    /// <summary>A single note is held into the next note over a short gap.</summary>
    public bool HoldSingleNoteGaps
    {
        get => Rules?.HoldSingleNoteGaps ?? true;
        set => Change(r => r.HoldSingleNoteGaps = value);
    }

    /// <summary>How short a gap after a single note is held over.</summary>
    public ChartSynthChoice<int>? SingleNoteGap
    {
        get => Choice(Rules?.SingleNoteGapDivisor);
        set
        {
            if (value != null) Change(r => r.SingleNoteGapDivisor = value.Value);
        }
    }

    /// <summary>A chord is held into the next note over a short gap.</summary>
    public bool HoldChordGaps
    {
        get => Rules?.HoldChordGaps ?? true;
        set => Change(r => r.HoldChordGaps = value);
    }

    /// <summary>How short a gap after a chord is held over.</summary>
    public ChartSynthChoice<int>? ChordGap
    {
        get => Choice(Rules?.ChordGapDivisor);
        set
        {
            if (value != null) Change(r => r.ChordGapDivisor = value.Value);
        }
    }

    /// <summary>The shown rule set differs from the defaults.</summary>
    public bool CanReset => Rules is { IsDefault: false };

    /// <summary>Puts the shown rule set (guitar or bass) back to the defaults.</summary>
    [RelayCommand]
    private void ResetToDefaults()
    {
        if (Rules is not { } target) return;
        _history.Do(new SetSynthPlayStyleAction(target, new ChartSynthPlayRules()));
    }

    private ChartSynthPlayRules? Rules => _projectState.ActiveProject?.SynthPlayStyle.For(_editingBass);

    // The family the arrangement PLAYS by: a 5/6-string bass charted on a guitar path
    // at A220 is a bass part, the same answer ChartNoteEventBuilder gives.
    private bool IsBassArrangement =>
        _projectState.ActiveArrangement is { } active && ArrangementInstrument.IsBassPart(active);

    private ChartSynthChoice<int>? Choice(int? divisor) =>
        divisor is { } d ? GapChoices.FirstOrDefault(c => c.Value == ChartSynthPlayRules.Sanitize(d)) : null;

    private void Change(Action<ChartSynthPlayRules> edit)
    {
        if (_refreshing || Rules is not { } target) return;
        var next = target.Clone();
        edit(next);
        // Do() refreshes through HistoryChanged; a no-op change is not an undo step.
        if (!next.SameAs(target)) _history.Do(new SetSynthPlayStyleAction(target, next));
    }

    private void SetEditingBass(bool bass)
    {
        if (_editingBass == bass) return;
        _editingBass = bass;
        Refresh();
    }

    private void OnProjectStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ProjectState.ActiveArrangement):
                // Follow the part being edited: a bass arrangement shows the bass rules.
                _editingBass = IsBassArrangement;
                Refresh();
                break;
            case nameof(ProjectState.ActiveProject):
                Refresh();
                break;
        }
    }

    private void Refresh()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            OnPropertyChanged(nameof(HasProject));
            OnPropertyChanged(nameof(EditingGuitar));
            OnPropertyChanged(nameof(EditingBass));
            OnPropertyChanged(nameof(HandShapesRing));
            OnPropertyChanged(nameof(HoldSingleNoteGaps));
            OnPropertyChanged(nameof(SingleNoteGap));
            OnPropertyChanged(nameof(HoldChordGaps));
            OnPropertyChanged(nameof(ChordGap));
            OnPropertyChanged(nameof(CanReset));
        }
        finally
        {
            _refreshing = false;
        }
    }

    public void Dispose()
    {
        _history.HistoryChanged -= Refresh;
        _projectState.PropertyChanged -= OnProjectStateChanged;
    }
}
