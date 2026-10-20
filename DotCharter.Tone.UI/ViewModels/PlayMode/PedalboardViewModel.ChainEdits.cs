// PedalboardViewModel: structural chain edits - add / duplicate / replace / remove (+ one level of undo) / reorder / drop-onto-a-branch-row, applied natively first and only then mirrored.

using CommunityToolkit.Mvvm.Input;
using DotCharter.Tone.Audio.Dsp;

namespace DotCharter.Tone.UI.ViewModels.PlayMode;

/// <summary>
///     What <see cref="PedalboardViewModel.ReplaceCommand" /> is asked to do: swap
///     <see cref="Node" /> for a unit of <see cref="Type" />. A record rather than
///     two loose command parameters because a command carries exactly one — and
///     because the pair has to be validated together (neither side may be a
///     routing junction).
/// </summary>
public sealed record RigModuleReplacement(PedalNodeViewModel? Node, DspModuleType? Type);

/// <summary>
///     Everything the board remembers about the unit <c>Remove</c> last took off
///     it, so one press can put it back: which module it was, the chain slot it
///     sat in, its lane, whether it was switched out, every float and string
///     value it carried, and whether the module editor was showing it.
/// </summary>
/// <param name="ModuleTypeId">The catalogue module type — what to re-add.</param>
/// <param name="Index">The chain slot it was removed from; CLAMPED on restore,
/// because the chain may have changed shape since.</param>
/// <param name="Branch">Its ABI-v23 branch tag (0 = untagged / serial).</param>
/// <param name="Bypassed">Whether it was switched out at the moment of removal.</param>
/// <param name="Values">Its float params by id.</param>
/// <param name="StringValues">Its non-empty string params by id — the loaded
/// capture, the cabinet IR, the lyric text. Dropping these would restore a
/// MODELLESS neural amp, i.e. a silent one.</param>
/// <param name="WasSelected">Whether it was the selected unit, so undo re-opens
/// the editor on it.</param>
public sealed record RemovedUnitSnapshot(
    string ModuleTypeId,
    int Index,
    int Branch,
    bool Bypassed,
    IReadOnlyDictionary<string, float> Values,
    IReadOnlyDictionary<string, string> StringValues,
    bool WasSelected);

public sealed partial class PedalboardViewModel
{
    /// <summary>Adds a catalogue module at the END of the chain (the gear-rail
    /// click path). Returns the created pedal card, or null on native failure.
    /// Adding a <c>splitter</c> auto-appends a paired <c>merger</c> right after it
    /// (a lone splitter is legal but confusing) — the returned card is the
    /// splitter.</summary>
    public PedalNodeViewModel? AddGear(DspModuleType type)
    {
        var vm = AddGearCore(type);
        if (vm == null) return null;

        // Auto-pair a splitter with a merger so a fresh split region is complete —
        // reusing any merger the chain already carries (never a second one).
        if (vm.IsSplitter) EnsureClosingMerger(vm, wrapNext: false);

        RebuildBoardColumns();
        return vm;
    }

    /// <summary>Adds a module at the chain end PRE-SET to a named model (the gear
    /// rail's browse-by-model path: "Plexi 100" → an amp with voicing=10). The
    /// param value is the 0-based model index; pushed after the node exists.
    /// Returns the created card, or null on native failure.</summary>
    public PedalNodeViewModel? AddGearWithModel(DspModuleType type, string paramId, int index)
    {
        var vm = AddGearCore(type);
        if (vm == null) return null;

        var param = vm.Params.FirstOrDefault(p => p.Id == paramId);
        param?.SelectedIndex = index; // writes native + refreshes the badge/plate

        RebuildBoardColumns();
        return vm;
    }

    // The raw single-node add (native + mirror), WITHOUT the splitter/merger
    // pairing or a board rebuild — the composable primitive for AddGear/AddGearAt.
    private PedalNodeViewModel? AddGearCore(DspModuleType type)
    {
        var node = _dsp.AddNode(type.Id);
        if (node == null) return null;

        var vm = CreateNodeVm(node);
        // The rig is OFF: a pedal added while the master bypass is armed joins the
        // board switched out like everything else on it. It is deliberately NOT
        // added to the restore set, so disarming switches it IN — which is what
        // "add a pedal to a bypassed rig, then turn the rig back on" has to mean.
        if (IsMasterBypassed) vm.Bypassed = true;
        Nodes.Add(vm);
        OnPropertyChanged(nameof(HasNodes));
        return vm;
    }

    /// <summary>Adds a catalogue module AT a chain slot (the gear-rail drop
    /// path): AddNode appends natively, then MoveNode places it. The index is
    /// clamped; an out-of-range drop degrades to an append. Returns the created
    /// pedal card, or null on native failure.</summary>
    public PedalNodeViewModel? AddGearAt(DspModuleType type, int index)
    {
        var vm = AddGearCore(type);
        if (vm == null) return null;

        MoveNodeToIndex(vm, index);

        // A splitter dropped mid-chain auto-pairs a merger right after it (reusing an
        // existing merger — the chain never carries two).
        if (vm.IsSplitter) EnsureClosingMerger(vm, wrapNext: false);

        RebuildBoardColumns();
        return vm;
    }

    // Moves a just-added tail node to a clamped chain slot (native + mirror).
    private void MoveNodeToIndex(PedalNodeViewModel vm, int index)
    {
        var target = Math.Clamp(index, 0, Nodes.Count - 1);
        var current = Nodes.IndexOf(vm);
        if (current >= 0 && target != current && _dsp.MoveNode(vm.NodeId, target))
            Nodes.Move(current, target);
    }

    // ---- duplicate ------------------------------------------------------------

    /// <summary>A routing JUNCTION is never duplicated: a splitter and its merger
    /// are one pair bounding one region, and a second splitter is exactly the
    /// two-mergers bug <see cref="EnsureClosingMerger"/> exists to prevent. The
    /// command's <c>CanExecute</c> so the affordance is visibly disabled on a
    /// junction card rather than silently doing nothing.</summary>
    private static bool CanDuplicate(PedalNodeViewModel? node) => node is { IsJunction: false };

    [RelayCommand(CanExecute = nameof(CanDuplicate))]
    private void Duplicate(PedalNodeViewModel? node) => DuplicateNode(node);

    /// <summary>
    ///     Adds a second module of <paramref name="node" />'s type immediately
    ///     AFTER it and copies the whole unit onto it — every float param, every
    ///     string param (the loaded capture / IR / lyric text), the bypass flag
    ///     and the branch tag — then selects the copy, which is what makes the
    ///     gesture read as "another one of these, here".
    ///     <para>
    ///         Native FIRST, like every edit on this surface: the copy is created
    ///         through <see cref="AddGearAt" />, so a refused add leaves the board
    ///         exactly as it was and returns null. The per-param writes then go
    ///         through the copy's OWN <c>PedalParamViewModel</c>s /
    ///         <c>PedalStringParamViewModel</c>s rather than the chain service, so
    ///         each one is snapped, pushed and mirrored by the one path that
    ///         already owns that param.
    ///     </para>
    ///     <para>
    ///         A JUNCTION is refused (see <see cref="CanDuplicate" />). Returns
    ///         the new card, or null when the node is unknown, its module type is
    ///         not in the catalogue, or the native add failed.
    ///     </para>
    /// </summary>
    public PedalNodeViewModel? DuplicateNode(PedalNodeViewModel? node)
    {
        if (!CanDuplicate(node) || node is null) return null;

        var index = Nodes.IndexOf(node);
        if (index < 0) return null;
        var type = AvailableModules.FirstOrDefault(m => m.Id == node.ModuleTypeId);
        if (type == null) return null;

        var copy = AddGearAt(type, index + 1);
        if (copy == null) return null;

        CopyUnitState(node, copy);
        SelectedNode = copy;
        return copy;
    }

    /// <summary>Copies everything a unit CARRIES from <paramref name="source"/>
    /// onto <paramref name="target"/>: float params, string params, the bypass
    /// flag and the branch tag. Each write takes the param's own push path, and
    /// the branch goes through <see cref="DropToRow"/> at the target's current
    /// index — a same-index drop is a no-op move that still applies the tag,
    /// which is exactly the half needed here.</summary>
    private void CopyUnitState(PedalNodeViewModel source, PedalNodeViewModel target)
    {
        foreach (var param in source.Params)
        {
            if (target.Params.FirstOrDefault(p => p.Id == param.Id) is { } mirror)
                mirror.Value = param.Value;
        }

        foreach (var str in source.StringParams)
        {
            if (str.Value.Length > 0 &&
                target.StringParams.FirstOrDefault(p => p.Id == str.Id) is { } mirror)
            {
                mirror.CopyValueFrom(str.Value);
            }
        }

        if (source.Bypassed != target.Bypassed) target.Bypassed = source.Bypassed;

        // Branch 0 is "unassigned" and DropToRow clamps to 1..3, so an untagged
        // source has nothing to carry — the fresh node is already untagged.
        if (source.Branch >= 1) DropToRow(target, Nodes.IndexOf(target), source.Branch);
    }

    // ---- replace --------------------------------------------------------------

    /// <summary>
    ///     Swaps one unit for another module type IN PLACE — the module editor's
    ///     "replace with…" picker. Carries the branch tag and the bypass flag
    ///     over (its position in the signal path and whether it is switched in
    ///     are properties of the SLOT, not of the module that was in it) and
    ///     selects the new unit.
    ///     <para>
    ///         Ordered add-then-remove, native first at both steps: the new node
    ///         is created at the old one's index and only then is the old one
    ///         removed, so a refused ADD leaves the board untouched and a refused
    ///         REMOVE is rolled back by removing the node just added. Neither
    ///         half can leave the board half-swapped.
    ///     </para>
    ///     <para>
    ///         A junction can be neither the target nor the replacement: the
    ///         splitter/merger pair is the region's frame, and dropping one in
    ///         through here would build a region with no partner.
    ///     </para>
    /// </summary>
    public PedalNodeViewModel? ReplaceNode(PedalNodeViewModel? node, DspModuleType? type)
    {
        if (!CanReplace(new RigModuleReplacement(node, type)) || node is null || type is null) return null;

        var index = Nodes.IndexOf(node);
        if (index < 0) return null;

        var branch = node.Branch;
        var bypassed = node.Bypassed;
        var wasSelected = ReferenceEquals(SelectedNode, node);

        var fresh = AddGearAt(type, index);
        if (fresh == null) return null;

        if (!_dsp.RemoveNode(node.NodeId))
        {
            // The chain refused the removal, so the swap cannot complete — take
            // the node we just added back out again rather than leaving two.
            if (_dsp.RemoveNode(fresh.NodeId)) Nodes.Remove(fresh);
            OnPropertyChanged(nameof(HasNodes));
            RebuildBoardColumns();
            return null;
        }

        if (wasSelected) SelectedNode = null;
        Nodes.Remove(node);
        OnPropertyChanged(nameof(HasNodes));

        if (bypassed) fresh.Bypassed = true;
        if (branch >= 1) DropToRow(fresh, Nodes.IndexOf(fresh), branch);

        RebuildBoardColumns();
        SelectedNode = fresh;
        return fresh;
    }

    /// <summary>Neither side of a replacement may be a routing junction, and both
    /// sides have to exist.</summary>
    private static bool CanReplace(RigModuleReplacement? request) =>
        request is { Node.IsJunction: false, Type: not null } r && !IsJunctionModule(r.Type.Id);

    [RelayCommand(CanExecute = nameof(CanReplace))]
    private void Replace(RigModuleReplacement? request) => ReplaceNode(request?.Node, request?.Type);

    /// <summary>True for the two ROUTING module types, which are structural and
    /// never a unit a duplicate/replace gesture may create on its own.</summary>
    private static bool IsJunctionModule(string? moduleTypeId) =>
        string.Equals(moduleTypeId, SplitterModuleTypeId, StringComparison.Ordinal) ||
        string.Equals(moduleTypeId, MergerModuleTypeId, StringComparison.Ordinal);

    // ---- remove (with one level of undo) --------------------------------------

    [RelayCommand]
    private void Remove(PedalNodeViewModel? node)
    {
        if (node == null) return;

        // Taken BEFORE the native call — after it the card's params cannot be
        // read back off a node the chain has forgotten.
        var snapshot = SnapshotForUndo(node);
        if (!_dsp.RemoveNode(node.NodeId)) return;

        var wasSelected = ReferenceEquals(SelectedNode, node);
        if (wasSelected) SelectedNode = null;
        Nodes.Remove(node);
        OnPropertyChanged(nameof(HasNodes));
        LastRemoved = snapshot is null ? null : snapshot with { WasSelected = wasSelected };
        RebuildBoardColumns();
    }

    /// <summary>
    ///     The last unit <see cref="RemoveCommand" /> took off THIS board, or null
    ///     when there is nothing to put back. Null again after the snapshot is
    ///     spent (<see cref="UndoRemoveCommand" />) and after anything replaces
    ///     the board it belonged to — a seat repoint, a profile switch, an engine
    ///     rebuild (<see cref="ResyncAll" />) or a preset apply — because a chain
    ///     index and a set of param values only mean anything against the chain
    ///     they were read from.
    /// </summary>
    public RemovedUnitSnapshot? LastRemoved
    {
        get => _lastRemoved;
        private set
        {
            if (ReferenceEquals(_lastRemoved, value)) return;
            _lastRemoved = value;
            OnPropertyChanged(nameof(LastRemoved));
            OnPropertyChanged(nameof(CanUndoRemove));
            OnPropertyChanged(nameof(UndoRemoveLabel));
            UndoRemoveCommand.NotifyCanExecuteChanged();
        }
    }

    private RemovedUnitSnapshot? _lastRemoved;

    /// <summary>Whether an "undo remove" affordance has anything to offer — what
    /// the board's undo chip binds its visibility to.</summary>
    public bool CanUndoRemove => _lastRemoved != null;

    /// <summary>"Undo — removed Overdrive", named off the catalogue rather than
    /// off the dead card (which is gone). Empty when there is nothing to undo, so
    /// the chip never renders a sentence about no unit.</summary>
    public string UndoRemoveLabel => _lastRemoved is { } removed
        ? _loc.Format("Rig_UndoRemoved", ModuleDisplayName(removed.ModuleTypeId))
        : string.Empty;

    private string ModuleDisplayName(string moduleTypeId) =>
        AvailableModules.FirstOrDefault(m => m.Id == moduleTypeId) is { DisplayName.Length: > 0 } type
            ? type.DisplayName
            : moduleTypeId;

    /// <summary>
    ///     Everything a removed unit needs to come back: what it was, where it
    ///     was, which lane it ran on, whether it was switched out, every value it
    ///     carried and whether the module editor was showing it.
    ///     <para>
    ///         A routing JUNCTION is deliberately never snapshotted — half of a
    ///         splitter/merger pair put back on its own would open a region with
    ///         no close (or close one that does not open), and
    ///         <c>RoutingSerial</c> removes the pair one node at a time, which
    ///         would leave an "undo" offering only the last of the two.
    ///     </para>
    /// </summary>
    private RemovedUnitSnapshot? SnapshotForUndo(PedalNodeViewModel node)
    {
        if (node.IsJunction) return null;
        var index = Nodes.IndexOf(node);
        if (index < 0) return null;

        return new RemovedUnitSnapshot(
            node.ModuleTypeId,
            index,
            node.Branch,
            node.Bypassed,
            node.Params.ToDictionary(p => p.Id, p => p.Value, StringComparer.Ordinal),
            node.StringParams.Where(p => p.Value.Length > 0)
                .ToDictionary(p => p.Id, p => p.Value, StringComparer.Ordinal),
            false);
    }

    /// <summary>
    ///     Puts the last removed unit back where it was, with everything it
    ///     carried, and re-selects it when it had been selected. The recorded
    ///     index is CLAMPED — the chain may have shrunk or grown since — and the
    ///     snapshot is spent either way, so the chip cannot re-add a second copy.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUndoRemove))]
    private void UndoRemove() => RestoreLastRemoved();

    /// <inheritdoc cref="UndoRemoveCommand" />
    /// <returns>The restored card, or null when there was nothing to restore, the
    /// module has left the catalogue, or the native add was refused.</returns>
    public PedalNodeViewModel? RestoreLastRemoved()
    {
        if (_lastRemoved is not { } snapshot) return null;
        var restored = RestoreSnapshot(snapshot);
        if (restored == null) return null;
        LastRemoved = null;
        if (snapshot.WasSelected) SelectedNode = restored;
        return restored;
    }

    /// <summary>A unit rebuilt from <paramref name="snapshot" /> at its (clamped)
    /// index and lane, with every value it carried — the undo-remove and the paste.</summary>
    private PedalNodeViewModel? RestoreSnapshot(RemovedUnitSnapshot snapshot)
    {
        var type = AvailableModules.FirstOrDefault(m => m.Id == snapshot.ModuleTypeId);
        if (type == null) return null;

        var restored = AddGearAt(type, Math.Clamp(snapshot.Index, 0, Nodes.Count));
        if (restored == null) return null;

        foreach (var (id, value) in snapshot.Values)
        {
            if (restored.Params.FirstOrDefault(p => p.Id == id) is { } param)
                param.Value = value;
        }

        foreach (var (id, value) in snapshot.StringValues)
        {
            if (restored.StringParams.FirstOrDefault(p => p.Id == id) is { } str)
                str.CopyValueFrom(value);
        }

        if (snapshot.Bypassed) restored.Bypassed = true;
        if (snapshot.Branch >= 1) DropToRow(restored, Nodes.IndexOf(restored), snapshot.Branch);
        return restored;
    }

    /// <summary>Drops the undo snapshot — called wherever the BOARD it was taken
    /// from is replaced wholesale (<see cref="ResyncAll"/>, a preset apply).</summary>
    private void ClearRemovedUnitSnapshot() => LastRemoved = null;

    [RelayCommand]
    private void MoveUp(PedalNodeViewModel? node)
    {
        if (node == null) return;
        var index = Nodes.IndexOf(node);
        if (index <= 0) return;
        if (!_dsp.MoveNode(node.NodeId, index - 1)) return;

        Nodes.Move(index, index - 1);
        RebuildBoardColumns();
    }

    [RelayCommand]
    private void MoveDown(PedalNodeViewModel? node)
    {
        if (node == null) return;
        var index = Nodes.IndexOf(node);
        if (index < 0 || index >= Nodes.Count - 1) return;
        if (!_dsp.MoveNode(node.NodeId, index + 1)) return;

        Nodes.Move(index, index + 1);
        RebuildBoardColumns();
    }

    /// <summary>Moves <paramref name="node"/> to <paramref name="newIndex"/> in
    /// signal order — the drag-reorder entry point for the board surface. Applies
    /// natively first and only mirrors the managed list on success; a no-op move
    /// (same index) returns true without touching the service. Returns false when
    /// the node is unknown, the index invalid, or the native call fails.</summary>
    public bool MoveTo(PedalNodeViewModel? node, int newIndex)
    {
        if (node == null) return false;
        var index = Nodes.IndexOf(node);
        if (index < 0) return false;
        if (newIndex < 0 || newIndex >= Nodes.Count) return false;
        if (newIndex == index) return true;
        if (!_dsp.MoveNode(node.NodeId, newIndex)) return false;

        Nodes.Move(index, newIndex);
        RebuildBoardColumns();
        return true;
    }

    /// <summary>Drops a pedal onto row <paramref name="branch"/> of a split region
    /// at flat chain slot <paramref name="newIndex"/> — the split-board drag drop.
    /// Applies the move and the branch tag ATOMICALLY (both native ops, then the
    /// board re-lays once), so a pedal never renders in the wrong row for a frame.
    /// A rejected move leaves the branch untouched; the branch is clamped 1..3.
    /// Returns true when at least the move landed.</summary>
    public bool DropToRow(PedalNodeViewModel? node, int newIndex, int branch)
    {
        if (node == null) return false;
        var index = Nodes.IndexOf(node);
        if (index < 0) return false;
        branch = Math.Clamp(branch, 1, 3);

        var target = Math.Clamp(newIndex, 0, Nodes.Count - 1);
        var moved = target == index || (_dsp.MoveNode(node.NodeId, target) && MirrorMove(index, target));
        if (!moved) return false;

        node.ApplyBranch(branch);
        RebuildBoardColumns();
        return true;
    }

    private bool MirrorMove(int from, int to)
    {
        Nodes.Move(from, to);
        return true;
    }
}
