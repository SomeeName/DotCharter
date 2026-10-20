// PedalboardViewModel: UNDO / REDO over whole-rig snapshots, and the unit CLIPBOARD (copy / cut / paste).

using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tone.UI.ViewModels.PlayMode;

public sealed partial class PedalboardViewModel
{
    private RigHistory<DcRigPreset>? _history;
    private bool _applyingHistory;
    private RemovedUnitSnapshot? _clipboard;

    /// <summary>The clock the undo history coalesces bursts by (ms). Tests replace it.</summary>
    internal Func<long> HistoryClock { get; set; } = () => Environment.TickCount64;

    private RigHistory<DcRigPreset> History =>
        _history ??= new RigHistory<DcRigPreset>(RigEditState.IsSameState);

    public bool CanUndo => History.CanUndo;
    public bool CanRedo => History.CanRedo;

    /// <summary>A unit has been copied (or cut) and can be pasted.</summary>
    public bool CanPaste => _clipboard is not null;

    /// <summary>Wires a card's edits into the history: every param and string
    /// value, and its bypass. Called for every card the board creates.</summary>
    private void TrackEdits(PedalNodeViewModel node)
    {
        node.PropertyChanged += OnNodeEdited;
        foreach (var param in node.Params) param.PropertyChanged += OnValueEdited;
        foreach (var text in node.StringParams) text.PropertyChanged += OnValueEdited;
    }

    private void OnNodeEdited(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PedalNodeViewModel.Bypassed)) NoteEdit();
    }

    private void OnValueEdited(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PedalParamViewModel.Value)) NoteEdit();
    }

    /// <summary>Something about the rig changed: a step for undo (one per burst).</summary>
    internal void NoteEdit()
    {
        if (_applyingHistory) return;
        History.Edited(BuildCurrentRig(), HistoryClock());
        RaiseHistoryState();
    }

    /// <summary>A discrete gesture (a scene switched, renamed, stored or cleared):
    /// always one undo step of its own.</summary>
    private void CommitHistoryStep()
    {
        History.Committed(BuildCurrentRig());
        RaiseHistoryState();
    }

    /// <summary>Starts the history over — the board on screen is a different one
    /// (a seat or profile switch, an engine rebuild).</summary>
    private void ResetHistory()
    {
        History.Reset(BuildCurrentRig());
        RaiseHistoryState();
    }

    private void RaiseHistoryState()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Ctrl+Z: the rig as it was before the last edit (a knob drag is one step).</summary>
    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo() => ApplyHistory(History.Undo(BuildCurrentRig()));

    /// <summary>Ctrl+Y / Ctrl+Shift+Z: the edit undo took back.</summary>
    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo() => ApplyHistory(History.Redo(BuildCurrentRig()));

    private void ApplyHistory(DcRigPreset? state)
    {
        if (state is null) return;
        var selected = SelectedNode is { } node ? Nodes.IndexOf(node) : -1;
        _applyingHistory = true;
        try
        {
            // the same verbatim apply the A/B compare uses: a snapshot of what was
            // heard comes back exactly, never re-levelled
            _presets.ApplyToPlayer(SelectedPlayer, new DspTonePreset(
                "history", "History", DspToneCategory.Clean, string.Empty, state, DspToneOrigin.User));
            Sync();
        }
        finally
        {
            _applyingHistory = false;
        }

        // keep the editor on the same slot where there still is one
        if (selected >= 0 && selected < Nodes.Count) SelectedNode = Nodes[selected];
        RaiseHistoryState();
    }

    // ---- the clipboard ----------------------------------------------------------

    /// <summary>Ctrl+C: remembers the unit (the selected one by default) with every
    /// value it carries, to paste as a copy.</summary>
    [RelayCommand]
    private void Copy(PedalNodeViewModel? node)
    {
        if ((node ?? SelectedNode) is not { } unit || SnapshotForUndo(unit) is not { } snapshot) return;
        _clipboard = snapshot;
        OnPropertyChanged(nameof(CanPaste));
        PasteCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Ctrl+X: copies the unit, then removes it.</summary>
    [RelayCommand]
    private void Cut(PedalNodeViewModel? node)
    {
        if ((node ?? SelectedNode) is not { IsJunction: false } unit) return;
        Copy(unit);
        Remove(unit);
    }

    /// <summary>Ctrl+V: a copy of the clipboard unit straight after the selected
    /// one (on its lane), or at the end of the chain; the copy is selected.</summary>
    [RelayCommand(CanExecute = nameof(CanPaste))]
    private void Paste(PedalNodeViewModel? after)
    {
        if (_clipboard is not { } snapshot) return;
        var anchor = after ?? SelectedNode;
        var index = anchor is not null && Nodes.IndexOf(anchor) is >= 0 and var i ? i + 1 : Nodes.Count;
        var branch = anchor?.Branch ?? 0;
        var pasted = RestoreSnapshot(snapshot with { Index = index, Branch = branch >= 1 ? branch : snapshot.Branch });
        if (pasted is not null) SelectedNode = pasted;
    }

    /// <summary>Ctrl+D: a copy of the selected unit straight after it.</summary>
    [RelayCommand]
    private void DuplicateSelected()
    {
        if (SelectedNode is { IsJunction: false } unit) DuplicateNode(unit);
    }

    /// <summary>Ctrl+B: switches the selected unit on or off.</summary>
    [RelayCommand]
    private void ToggleSelectedBypass()
    {
        if (SelectedNode is { } unit) unit.Bypassed = !unit.Bypassed;
    }

    /// <summary>Del: removes the selected unit (never a routing junction on its own).</summary>
    [RelayCommand]
    private void DeleteSelected()
    {
        if (SelectedNode is { IsJunction: false } unit) Remove(unit);
    }
}
