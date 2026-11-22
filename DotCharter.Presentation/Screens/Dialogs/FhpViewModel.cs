using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using DotCharter.Core.Domain.Events;
using DotCharter.Core.Domain.Primitives;
using DotCharter.Core.Utilities;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Editing.Anchors;
using DotCharter.Tone.Infrastructure.Configuration;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>The fret-hand position (anchor) editor. One instance per open.</summary>
public sealed class FhpViewModel : DialogViewModel<bool>, IFhpScreen, IDialogSetup<FhpRequest>, ICancelableDialog
{
    private readonly ChartEditDialogServices _edit;
    private FretAnchor? _editing;
    private ObservableCollection<FretAnchor> _anchors = [];
    private IList<BeatMarker> _beats = [];
    private FractionalPosition _position;
    private int _fret = 1;
    private int _width = 4;

    public FhpViewModel(ChartEditDialogServices edit)
    {
        _edit = edit;
        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(() => TryCancel());
    }

    public int Fret
    {
        get => _fret;
        set => SetProperty(ref _fret, value);
    }

    public int Width
    {
        get => _width;
        set => SetProperty(ref _width, value);
    }

    public ICommand SaveCommand { get; }

    public ICommand CancelCommand { get; }

    /// <summary>
    ///     Edits the supplied row head (badge or marker) at its position; otherwise uses the captured row snap.
    ///     With null, edits the anchor at the snapped time or drafts a new one there. New anchors
    ///     take the preview fret or note-based suggestion and the width in force at that position.
    /// </summary>
    public void Configure(FhpRequest request)
    {
        var editor = _edit.Editor;
        var track = request.Target?.Track;
        _anchors = track?.Anchors ?? editor.Anchors;
        _beats = track?.Beats ?? editor.Beats;
        var snapped = request.Target?.Position ?? editor.SnapToGrid(request.TimeMs >= 0 ? request.TimeMs : _edit.State.CurrentTimeMs);
        _editing = request.Anchor ?? _anchors.FirstOrDefault(a => a.Position == snapped);

        if (_editing != null)
        {
            Fret = _editing.Fret;
            Width = _editing.Width;
            _position = _editing.Position;
        }
        else
        {
            var spanning = FretAnchorLookup.InEffectAt(_anchors, snapped);
            Fret = _edit.State.PreviewAnchorFret > 0
                ? _edit.State.PreviewAnchorFret
                : AnchorFretSuggester.Suggest(snapped, track?.Notes ?? editor.PlacedNotes, track?.Chords ?? editor.PlacedChords, _anchors,
                    track?.Arrangement.Capo ?? editor.ActiveArrangement?.Capo ?? 0, spanning?.Fret ?? (track?.Selection ?? editor.Selection).SelectedNote?.Fret);
            Width = spanning?.Width ?? 4;
            _position = snapped;
        }

        _edit.Logger.Info(AppLogCategory.Editing,
            $"FHP dialog opened. EditingExisting={_editing != null}, requestedTimeMs={request.TimeMs:0.###}, " +
            $"position={Format(_position)}, fret={Fret}, width={Width}.");
    }

    public bool TryCancel()
    {
        if (!IsClosed) _edit.Logger.Info(AppLogCategory.Editing, "FHP dialog canceled.");
        Close(false);
        return true;
    }

    private void Save()
    {
        var editor = _edit.Editor;
        if (_editing != null)
        {
            var before = _editing.Clone();
            var after = _editing.Clone();
            after.Fret = Fret;
            after.Width = Width;
            after.Position = _position;
            if (_edit.History.Do(FhpAnchorEdit.Mutate(editor, _editing, before, after, _anchors)))
            {
                _edit.Logger.Info(AppLogCategory.Editing,
                    $"FHP dialog saved existing anchor. Old position={Format(before.Position)}, oldFret={before.Fret}, " +
                    $"oldWidth={before.Width}; new position={Format(after.Position)}, newFret={after.Fret}, newWidth={after.Width}.");
            }
        }
        else
        {
            var anchor = new FretAnchor { Position = _position, Fret = Fret, Width = Width };
            _edit.History.Do(FhpAnchorEdit.Add(editor, anchor, _anchors));
            _edit.Logger.Info(AppLogCategory.Editing,
                $"FHP dialog created anchor. Position={Format(anchor.Position)}, fret={anchor.Fret}, width={anchor.Width}.");
        }

        Close(true);
    }

    private string Format(FractionalPosition position) => position.FormatWithMs(_beats);
}
