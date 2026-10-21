using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Core.Domain.Events;
using DotCharter.Core.Domain.Primitives;
using DotCharter.Core.Utilities;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Editing;
using DotCharter.Services.State;
using DotCharter.Tone.Infrastructure.Configuration;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>The fret-hand position (anchor) editor. One instance per open.</summary>
public sealed class FhpViewModel : DialogViewModel<bool>, IFhpScreen, IDialogSetup<FhpRequest>, ICancelableDialog
{
    private readonly ChartEditDialogServices _edit;
    private FretAnchor? _editing;
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
    ///     Edits the given anchor, else the one already on the snapped grid
    ///     position; a new anchor starts from the one spanning that position, then
    ///     from the selected note's fret.
    /// </summary>
    public void Configure(FhpRequest request)
    {
        var editor = _edit.Editor;
        var snapped = editor.SnapToGrid(request.TimeMs >= 0 ? request.TimeMs : _edit.State.CurrentTimeMs);
        _editing = request.Anchor ?? editor.Anchors.FirstOrDefault(a => a.Position == snapped);

        if (_editing != null)
        {
            Fret = _editing.Fret;
            Width = _editing.Width;
            _position = _editing.Position;
        }
        else
        {
            var spanning = editor.Anchors.LastOrDefault(a => a.Position <= snapped);
            Fret = spanning?.Fret ?? editor.Selection.SelectedNote?.Fret ?? 1;
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
            if (_edit.History.Do(new MutateAnchorAction(editor, _editing, before, after)))
            {
                _edit.Logger.Info(AppLogCategory.Editing,
                    $"FHP dialog saved existing anchor. Old position={Format(before.Position)}, oldFret={before.Fret}, " +
                    $"oldWidth={before.Width}; new position={Format(after.Position)}, newFret={after.Fret}, newWidth={after.Width}.");
            }
        }
        else
        {
            var anchor = new FretAnchor { Position = _position, Fret = Fret, Width = Width };
            _edit.History.Do(new AddAnchorAction(editor, anchor));
            _edit.Logger.Info(AppLogCategory.Editing,
                $"FHP dialog created anchor. Position={Format(anchor.Position)}, fret={anchor.Fret}, width={anchor.Width}.");
        }

        Close(true);
    }

    private string Format(FractionalPosition position) => position.FormatWithMs(_edit.Editor.Beats);

    /// <summary>Re-sorts the anchors by position and redraws; both actions end here.</summary>
    private static void Resort(IChartEditorSurface editor)
    {
        var sorted = editor.Anchors.OrderBy(a => a.Position).ToList();
        editor.Anchors.Clear();
        foreach (var anchor in sorted) editor.Anchors.Add(anchor);
        editor.RequestRedraw();
    }

    private sealed class MutateAnchorAction(IChartEditorSurface editor, FretAnchor target, FretAnchor before, FretAnchor after)
        : IChartAction, ILinkPolicyAction
    {
        /// <summary>Linked measures: an editing gesture, copied to every linked placement.</summary>
        public LinkPolicy LinkPolicy => LinkPolicy.Propagate;

        public bool HasEffect => !ChartActionStateComparer.AnchorsEqual(before, after);

        public void Execute() => Apply(after);

        public void Undo() => Apply(before);

        private void Apply(FretAnchor state)
        {
            target.Fret = state.Fret;
            target.Width = state.Width;
            target.Position = state.Position;
            Resort(editor);
        }
    }

    private sealed class AddAnchorAction(IChartEditorSurface editor, FretAnchor anchor) : IChartAction, ILinkPolicyAction
    {
        /// <summary>Linked measures: an editing gesture, copied to every linked placement.</summary>
        public LinkPolicy LinkPolicy => LinkPolicy.Propagate;

        public void Execute()
        {
            editor.Anchors.Add(anchor);
            Resort(editor);
        }

        public void Undo()
        {
            editor.Anchors.Remove(anchor);
            Resort(editor);
        }
    }
}
