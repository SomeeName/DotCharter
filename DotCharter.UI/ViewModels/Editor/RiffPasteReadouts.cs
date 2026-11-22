using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.UI.ViewModels.Editor;

/// <summary>
///     The Riff library's two live readouts (inspector design D):
///     <list type="bullet">
///         <item><see cref="PastesAt" />: where "Paste at playhead" lands, the playhead snapped to the beat grid exactly
///         as the paste snaps it (<see cref="ChartEditorViewModel.SnapToBeatGrid" />, the transport's grid). The
///         playhead moves at up to 250 Hz, so a move only REQUESTS a re-read: one is posted at a time and the label
///         raises a change only when its words change.</item>
///         <item><see cref="SelectionText" />: how many sounds (notes and chords) Capture would store, re-read on the
///         selection's change.</item>
///     </list>
/// </summary>
public sealed class RiffPasteReadouts : ObservableObject
{
    private readonly ProjectState _state;
    private readonly ChartEditorViewModel _editor;
    private readonly ILocalizationService _localization;
    private readonly Action<Action> _post;
    private bool _pending;
    private string _pastesAt = "";
    private string _selectionText = "";

    public RiffPasteReadouts(ProjectState state, ChartEditorViewModel editor, TransportViewModel transport,
        ILocalizationService localization, Action<Action> post)
    {
        _state = state;
        _editor = editor;
        _localization = localization;
        _post = post;
        state.PropertyChanged += OnStateChanged;
        transport.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TransportViewModel.GridDivisor)) RequestPastesAt();
        };
        editor.Selection.SelectionChanged += (_, _) => ReadSelection();
        ReadPastesAt();
        ReadSelection();
    }

    /// <summary>"bar 17 · 0:42.0": the grid-snapped place the paste lands.</summary>
    public string PastesAt
    {
        get => _pastesAt;
        private set => SetProperty(ref _pastesAt, value);
    }

    /// <summary>"Selection: 12 sounds".</summary>
    public string SelectionText
    {
        get => _selectionText;
        private set => SetProperty(ref _selectionText, value);
    }

    /// <summary>How many times the paste place was read: the coalescing test hook.</summary>
    internal int PastesAtReads { get; private set; }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ProjectState.CurrentTimeMs) or nameof(ProjectState.ActiveProject)
            or nameof(ProjectState.ActiveArrangement))
        {
            RequestPastesAt();
        }
    }

    private void RequestPastesAt()
    {
        if (_pending) return;
        _pending = true;
        _post(ReadPastesAt);
    }

    private void ReadPastesAt()
    {
        _pending = false;
        PastesAtReads++;
        var beats = _editor.Beats;
        var ms = _editor.SnapToBeatGrid(_state.CurrentTimeMs).GetPositionMs(beats);
        var time = ElapsedTimeLabel.Format(ms, 1);
        PastesAt = beats.Count > 0 && LiveBarNumbers.BarAt(beats, ms) is { } bar
            ? _localization.Format("PhraseCatalog_PastesAtFmt", bar, time)
            : time;
    }

    private void ReadSelection()
    {
        var count = _editor.Selection.SelectedNotes.Count + _editor.Selection.SelectedChords.Count;
        SelectionText = _localization.Plural("PhraseCatalog_SelectionOne", "PhraseCatalog_SelectionFmt", count);
    }
}
