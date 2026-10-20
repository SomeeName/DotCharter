using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Core.Domain.Events;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Services.Audio.Coordination;
using DotCharter.Services.Editing.Collisions;
using DotCharter.Services.Editing.Tempo;
using DotCharter.Services.State;
using DotCharter.Tone.Infrastructure.Configuration;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>Tempo ▸ Set BPM for all measures. One instance per open.</summary>
public sealed class SetBpmViewModel : DialogViewModel<bool>, ISetBpmScreen
{
    private readonly ChartEditDialogServices _edit;
    private readonly ITempoService _tempo;
    private readonly IEditorTransport _transport;
    private double _bpm;

    /// <summary>
    ///     Seeds from the GRID, not from the nominal <c>ChartProject.Bpm</c>: the
    ///     beat dialog moves beat 0's tempo without touching that field, and an
    ///     undo of this dialog's own apply puts the grid back, so the nominal value
    ///     is the one thing here that can disagree with the chart.
    /// </summary>
    public SetBpmViewModel(ChartEditDialogServices edit, ITempoService tempo, IEditorTransport transport)
    {
        _edit = edit;
        _tempo = tempo;
        _transport = transport;
        var beats = edit.Editor.Beats;
        _bpm = beats.Count > 0 && beats[0].Bpm > 0 ? beats[0].Bpm : edit.State.ActiveProject?.Bpm ?? transport.Bpm;
        if (_bpm <= 0) _bpm = 120.0;
        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(() => Close(false));
        edit.Logger.Info(AppLogCategory.Events, $"Set-BPM-for-all-measures dialog opened. Bpm={_bpm:0.###}.");
    }

    public double Bpm
    {
        get => _bpm;
        set => SetProperty(ref _bpm, value);
    }

    public ICommand SaveCommand { get; }

    public ICommand CancelCommand { get; }

    private void Save()
    {
        var editor = _edit.Editor;
        var project = _edit.State.ActiveProject;
        if (project == null || editor.Beats.Count == 0 || Bpm <= 0 || _edit.RefuseIfBeatMapLocked("Set-BPM-for-all-measures"))
        {
            Close(false);
            return;
        }

        var oldBeats = CloneBeats(editor.Beats);
        var newBeats = CloneBeats(editor.Beats);
        _tempo.SetUniformBpm(newBeats, Bpm);

        // The nominal tempo travels WITH the grid, in the action's post-execute
        // callback, which MutateBeatsAction runs on Undo as well as Execute — so a
        // Ctrl+Z puts project.Bpm and the transport back beside the beats.
        var applied = _edit.History.Do(new MutateBeatsAction(editor.Beats, oldBeats, newBeats, () =>
        {
            ChartSpacingEnforcer.EnforceProject(project, _edit.Config.LoadAppConfig());
            var bpm = editor.Beats.Count > 0 ? editor.Beats[0].Bpm : 0;
            if (bpm > 0)
            {
                project.Bpm = bpm;
                _transport.Bpm = bpm;
            }

            editor.RequestRedraw();
        }));
        if (applied)
        {
            _edit.Logger.Info(AppLogCategory.Events,
                $"Applied uniform BPM to all measures. Bpm={Bpm:0.###}, beats={newBeats.Count}.");
        }

        Close(applied);
    }

    internal static List<BeatMarker> CloneBeats(IEnumerable<BeatMarker> beats) =>
        beats.Select(b => new BeatMarker
        {
            Time = b.Time,
            Bpm = b.Bpm,
            IsAnchor = b.IsAnchor,
            IsMeasure = b.IsMeasure,
            BeatsInMeasure = b.BeatsInMeasure,
            NoteDenominator = b.NoteDenominator
        }).ToList();
}
