using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Core.Domain.Events;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Editing.Collisions;
using DotCharter.Services.Editing.Tempo;
using DotCharter.Services.State;
using DotCharter.Tone.Infrastructure.Configuration;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>One beat of the grid: tempo, time signature and anchor. One instance per open.</summary>
public sealed class BeatViewModel : DialogViewModel<bool>, IBeatScreen, IDialogSetup<BeatRequest>, ICancelableDialog
{
    private readonly ChartEditDialogServices _edit;
    private readonly ITempoService _tempo;
    private BeatMarker? _target;
    private int _index = -1;
    private double _bpm;
    private int _beatsInMeasure;
    private int _noteDenominator;
    private bool _isAnchor;

    public BeatViewModel(ChartEditDialogServices edit, ITempoService tempo)
    {
        _edit = edit;
        _tempo = tempo;
        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(() => TryCancel());
    }

    public double Bpm
    {
        get => _bpm;
        set => SetProperty(ref _bpm, value);
    }

    public int BeatsInMeasure
    {
        get => _beatsInMeasure;
        set => SetProperty(ref _beatsInMeasure, value);
    }

    public int NoteDenominator
    {
        get => _noteDenominator;
        set => SetProperty(ref _noteDenominator, value);
    }

    public bool IsAnchor
    {
        get => _isAnchor;
        set => SetProperty(ref _isAnchor, value);
    }

    public ICommand SaveCommand { get; }

    public ICommand CancelCommand { get; }

    public void Configure(BeatRequest request)
    {
        var beat = request.Beat;
        _target = beat;
        _index = request.Index;
        Bpm = beat.Bpm;
        BeatsInMeasure = beat.BeatsInMeasure;
        NoteDenominator = beat.NoteDenominator;
        IsAnchor = beat.IsAnchor;
        _edit.Logger.Info(AppLogCategory.Events,
            $"Beat dialog opened. Index={_index}, timeMs={beat.Time:0.###}, bpm={beat.Bpm:0.###}, anchor={beat.IsAnchor}, " +
            $"measure={beat.IsMeasure}, beatsInMeasure={beat.BeatsInMeasure}, denominator={beat.NoteDenominator}.");
    }

    public bool TryCancel()
    {
        if (!IsClosed) _edit.Logger.Info(AppLogCategory.Events, $"Beat dialog closed. TargetIndex={_index}.");
        Close(false);
        return true;
    }

    private void Save()
    {
        if (_edit.RefuseIfBeatMapLocked("Beat dialog save"))
        {
            Close(false);
            return;
        }

        var saved = _target != null && _index >= 0 && _index < _edit.Editor.Beats.Count && Apply(_target, _index);
        if (!IsClosed) _edit.Logger.Info(AppLogCategory.Events, $"Beat dialog closed. TargetIndex={_index}.");
        Close(saved);
    }

    private bool Apply(BeatMarker target, int index)
    {
        var beats = _edit.Editor.Beats;
        var oldDenominator = target.NoteDenominator;
        var oldBeatsInMeasure = target.BeatsInMeasure;
        var signatureChanged = oldDenominator != NoteDenominator || oldBeatsInMeasure != BeatsInMeasure;
        var bpmChanged = Math.Abs(target.Bpm - Bpm) > 0.001;
        var anchorChanged = target.IsAnchor != IsAnchor;

        var oldBeats = SetBpmViewModel.CloneBeats(beats);
        var newBeats = SetBpmViewModel.CloneBeats(beats);

        // A time signature applies from this beat to the next beat whose original
        // signature differs; propagate it across that span and re-flag the
        // downbeats so the measure grid actually moves.
        var sectionEnd = newBeats.Count;
        for (var i = index + 1; i < newBeats.Count; i++)
        {
            if (oldBeats[i].BeatsInMeasure != oldBeatsInMeasure || oldBeats[i].NoteDenominator != oldDenominator)
            {
                sectionEnd = i;
                break;
            }
        }

        for (var i = index; i < sectionEnd; i++)
        {
            newBeats[i].BeatsInMeasure = BeatsInMeasure;
            newBeats[i].NoteDenominator = NoteDenominator;
            if (signatureChanged) newBeats[i].IsMeasure = (i - index) % Math.Max(1, BeatsInMeasure) == 0;
        }

        newBeats[index].IsAnchor = IsAnchor;

        // Clearing the anchor hands this beat's time back to the tempo map, so it
        // lands on the grid its predecessor implies — the one case where the edited
        // beat MOVES. RecalculateBeatTimes only walks forward, so it cannot say this.
        if (anchorChanged && !IsAnchor) _tempo.RetimeBeatFromTempo(newBeats, index);

        if (bpmChanged || anchorChanged)
        {
            _tempo.ApplyTempoChange(Bpm, index, newBeats);
        }
        else
        {
            newBeats[index].Bpm = Bpm;
            if (signatureChanged) _tempo.RecalculateBeatTimes(newBeats, index);
        }

        if (!_edit.History.Do(new MutateBeatsAction(beats, oldBeats, newBeats, () =>
            {
                ChartSpacingEnforcer.EnforceProject(_edit.State.ActiveProject, _edit.Config.LoadAppConfig());
                _edit.Editor.RequestRedraw();
            })))
        {
            return false;
        }

        _edit.Logger.Info(AppLogCategory.Events,
            $"Beat dialog saved. Index={index}, oldTimeMs={oldBeats[index].Time:0.###}, newTimeMs={newBeats[index].Time:0.###}, " +
            $"oldBpm={oldBeats[index].Bpm:0.###}, newBpm={newBeats[index].Bpm:0.###}, anchor={newBeats[index].IsAnchor}, " +
            $"beatsInMeasure={newBeats[index].BeatsInMeasure}, denominator={newBeats[index].NoteDenominator}, " +
            $"recalculated={bpmChanged || anchorChanged || signatureChanged}.");
        return true;
    }
}
