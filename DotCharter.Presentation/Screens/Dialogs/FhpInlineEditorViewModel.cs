using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using DotCharter.Core.Domain.Events;
using DotCharter.Core.Domain.Primitives;
using DotCharter.Core.Utilities;
using DotCharter.Services.Editing.Anchors;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Tone.Infrastructure.Configuration;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>
///     The fret-hand position editor that sits ON the timeline's FHP row instead of in a
///     dialog: a fret box (pre-filled from the notes under the anchor, selected for
///     typing) and a width box one Tab away. Enter commits, Escape cancels, and the
///     commit is the SAME edit the dialog makes (<see cref="FhpAnchorEdit" />), so it is
///     one undo step. A NEW anchor is only added on commit, which is why cancelling
///     leaves no edit and no undo entry. Clicking away cancels (see the PR for why).
/// </summary>
public sealed class FhpInlineEditorViewModel : ObservableObject
{
    /// <summary>The fret range the dialog's boxes accept.</summary>
    public const int MinValue = 1;

    /// <inheritdoc cref="MinValue" />
    public const int MaxValue = 24;

    private const int DefaultWidth = 4;

    private readonly ChartEditDialogServices _edit;
    private FretAnchor? _editing;
    private ObservableCollection<FretAnchor> _anchors = [];
    private IList<BeatMarker> _beats = [];
    private FractionalPosition _position;
    private bool _isOpen;
    private double _placementMs;
    private string _fretText = "";
    private string _widthText = "";
    private bool _hasInvalidInput;

    public FhpInlineEditorViewModel(ChartEditDialogServices edit) => _edit = edit;

    public bool IsOpen
    {
        get => _isOpen;
        private set => SetProperty(ref _isOpen, value);
    }

    /// <summary>The song time the editor sits at: the anchor's own time, or the snapped grid time for a new one.</summary>
    public double PlacementMs
    {
        get => _placementMs;
        private set => SetProperty(ref _placementMs, value);
    }

    public bool IsEditingExisting => _editing != null;

    public string FretText
    {
        get => _fretText;
        set
        {
            if (SetProperty(ref _fretText, value)) HasInvalidInput = false;
        }
    }

    public string WidthText
    {
        get => _widthText;
        set
        {
            if (SetProperty(ref _widthText, value)) HasInvalidInput = false;
        }
    }

    /// <summary>True after a commit was refused because a box did not hold a number in range.</summary>
    public bool HasInvalidInput
    {
        get => _hasInvalidInput;
        private set => SetProperty(ref _hasInvalidInput, value);
    }

    /// <summary>
    ///     Edits the supplied <paramref name="anchor" /> at its position, otherwise uses the active editor snap.
    ///     With null, edits the anchor at the snapped <paramref name="timeMs" /> (the playhead when negative),
    ///     or drafts a new one there. Whatever was open is dropped unedited.
    /// </summary>
    public void Begin(double timeMs, FretAnchor? anchor) => Begin(new FhpRequest(timeMs, anchor));

    /// <summary>Captures the requested row's collections so commit and undo retain that row.</summary>
    public void Begin(FhpRequest request)
    {
        var editor = _edit.Editor;
        var track = request.Target?.Track;
        var timeMs = request.TimeMs;
        _anchors = track?.Anchors ?? editor.Anchors;
        _beats = track?.Beats ?? editor.Beats;
        var beats = _beats;
        var snapped = request.Target?.Position ?? editor.SnapToGrid(timeMs >= 0 ? timeMs : _edit.State.CurrentTimeMs);
        _editing = request.Anchor ?? _anchors.FirstOrDefault(a => a.Position == snapped);

        int fret;
        int width;
        if (_editing != null)
        {
            _position = _editing.Position;
            fret = _editing.Fret;
            width = _editing.Width;
        }
        else
        {
            _position = snapped;
            var spanning = FretAnchorLookup.InEffectAt(_anchors, snapped);
            // The preview fret is a fret the user already chose; otherwise the notes the anchor will govern decide
            // (the same suggester the one-click placement uses).
            fret = _edit.State.PreviewAnchorFret > 0
                ? _edit.State.PreviewAnchorFret
                : AnchorFretSuggester.Suggest(snapped, track?.Notes ?? editor.PlacedNotes, track?.Chords ?? editor.PlacedChords, _anchors,
                    track?.Arrangement.Capo ?? editor.ActiveArrangement?.Capo ?? 0, spanning?.Fret ?? (track?.Selection ?? editor.Selection).SelectedNote?.Fret);
            width = spanning?.Width ?? DefaultWidth;
        }

        FretText = Clamp(fret).ToString(CultureInfo.InvariantCulture);
        WidthText = Clamp(width).ToString(CultureInfo.InvariantCulture);
        HasInvalidInput = false;
        PlacementMs = _position.GetPositionMs(beats);
        OnPropertyChanged(nameof(IsEditingExisting));
        IsOpen = true;
        _edit.Logger.Info(AppLogCategory.Editing,
            $"FHP inline editor opened. EditingExisting={_editing != null}, requestedTimeMs={timeMs:0.###}, " +
            $"position={_position.FormatWithMs(beats)}, fret={FretText}, width={WidthText}.");
    }

    /// <summary>Writes the edit as one undo step and closes. False (staying open) when a box is not a number in range.</summary>
    public bool Commit()
    {
        if (!IsOpen) return false;
        if (!TryParse(FretText, out var fret) || !TryParse(WidthText, out var width))
        {
            HasInvalidInput = true;
            return false;
        }

        var editor = _edit.Editor;
        if (_editing != null)
        {
            var before = _editing.Clone();
            var after = _editing.Clone();
            after.Fret = fret;
            after.Width = width;
            _edit.History.Do(FhpAnchorEdit.Mutate(editor, _editing, before, after, _anchors));
        }
        else
        {
            _edit.History.Do(FhpAnchorEdit.Add(editor, new FretAnchor { Position = _position, Fret = fret, Width = width }, _anchors));
        }

        _edit.Logger.Info(AppLogCategory.Editing,
            $"FHP inline editor committed. EditingExisting={_editing != null}, position={_position.FormatWithMs(_beats)}, fret={fret}, width={width}.");
        Close();
        return true;
    }

    /// <summary>Closes without writing anything.</summary>
    public void Cancel()
    {
        if (!IsOpen) return;
        _edit.Logger.Info(AppLogCategory.Editing, "FHP inline editor canceled.");
        Close();
    }

    private void Close()
    {
        _editing = null;
        IsOpen = false;
        OnPropertyChanged(nameof(IsEditingExisting));
    }

    private static int Clamp(int value) => Math.Clamp(value, MinValue, MaxValue);

    private static bool TryParse(string text, out int value) =>
        int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value) && value is >= MinValue and <= MaxValue;
}
