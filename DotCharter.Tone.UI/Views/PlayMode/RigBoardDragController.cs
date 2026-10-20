using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;
using DotCharter.Tone.UI.Controls.Dsp;
using DotCharter.Tone.UI.Controls.Fire;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tone.UI.Views.PlayMode;

/// <summary>
///     Drag-and-drop for the Amp &amp; FX board: reorder within a run,
///     re-branch ACROSS runs, and drag a gear row straight onto the board.
///
///     <para>
///         The Fire layer shipped <see cref="FireDragSession" />,
///         <see cref="FireDropMarker" /> and <see cref="FireSlotHitTest" /> —
///         ghost, insertion bar and geometry, all unit-tested — and then nothing
///         attached them to anything. The board rendered its units in signal
///         order and offered no way to change that order, which is what the
///         owner reported as "you can't drag and drop". This is the missing
///         half.
///     </para>
///
///     <para>
///         <b>The drop target is whichever RUN the pointer is over</b> — a
///         serial stretch, or one lane of the split region — resolved by
///         hit-test each move, with the ORIGIN run as the forgiving fallback
///         when the pointer is over nothing droppable. The first lane fold
///         scoped a drag to the run it started in, and that scoping is exactly
///         the gesture the owner reached for and could not make: dragging a
///         card INTO a lane, or from lane A to lane B. A drop on a lane
///         commits through <see cref="PedalboardViewModel.DropToRow" /> (move +
///         branch tag, atomic); a drop on a serial run through
///         <see cref="PedalboardViewModel.MoveTo" />. A gear row dropped on a
///         run commits through <see cref="PedalboardViewModel.AddGearAt" />,
///         then the branch tag if the run is a lane.
///     </para>
///
///     <para>
///         Nothing here is committed until the pointer is released, and every
///         commit applies natively first — a refused native move leaves both
///         the chain and the board exactly as they were.
///     </para>
/// </summary>
internal sealed class RigBoardDragController : IDisposable
{
    /// <summary>The id prefix of every board run. Each run's lane id is
    /// per-PANEL (<see cref="LaneIdOf" />), so a slot resolved against one
    /// run's geometry can never be committed against another's indices.</summary>
    public const string LaneId = "rig.chain";

    /// <summary>How far the pointer must travel before a press becomes a drag.
    /// Without it, every click on a card's head would begin (and instantly end)
    /// a drag, and selecting a unit would flicker a ghost.</summary>
    public const double DragThresholdPx = 4.0;

    private readonly ItemsControl _board;
    private readonly Control _surface;
    private readonly ItemsControl? _gearList;
    private readonly Func<AmpFxRigViewModel?> _resolveScreen;

    private FireDragSession? _session;
    private Control? _container;
    private Panel? _panel;
    private PedalNodeViewModel? _node;
    private RigGearItem? _gearItem;
    private Point _pressInContainer;
    private Point _pressInBoard;
    private bool _pressed;

    /// <summary>The run the marker is currently on — what the release commits
    /// against. Re-resolved every move by hit-test.</summary>
    private RunTarget? _target;

    /// <summary>
    ///     True only while THIS controller is taking the pointer capture.
    ///     <para>
    ///         <c>PointerCaptureLost</c> BUBBLES. The unit card is a Button, so
    ///         it captures the pointer on press; the moment the drag arms and
    ///         re-captures to the surface, that Button loses its capture and the
    ///         event travels up — straight into the cancel handler, tearing down
    ///         the session one frame after it started. The drag looked
    ///         completely dead and nothing failed.
    ///     </para>
    /// </summary>
    private bool _capturing;

    public RigBoardDragController(
        ItemsControl board, Control surface, ItemsControl? gearList,
        Func<AmpFxRigViewModel?> resolveScreen)
    {
        _board = board;
        _surface = surface;
        _gearList = gearList;
        _resolveScreen = resolveScreen;

        // Tunnel for the presses so a card's own Button (select-this-unit) and
        // the gear row's Button (append) do not consume them before a drag can
        // arm; bubble for the rest. Move/release live on the SURFACE — the
        // whole rig screen — because a gear drag starts outside the board.
        _board.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed,
            Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _gearList?.AddHandler(InputElement.PointerPressedEvent, OnGearPointerPressed,
            Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _surface.PointerMoved += OnPointerMoved;
        _surface.PointerReleased += OnPointerReleased;
        _surface.PointerCaptureLost += OnPointerCaptureLost;
    }

    /// <summary>
    ///     Where the dragged unit ends up, given where it came from and which
    ///     GAP the marker was sitting in.
    ///     <para>
    ///         <see cref="FireSlotHitTest.InsertionIndex" /> counts gaps —
    ///         <c>[0, count]</c>, where <c>count</c> means "past the last item".
    ///         <see cref="PedalboardViewModel.MoveTo" /> takes a final POSITION —
    ///         <c>[0, count-1]</c>. They differ by one whenever the item is
    ///         moving forwards, because the item vacates its own slot on the way
    ///         past: dragging unit 0 into the gap after unit 2 is insertion index
    ///         3 and final position 2.
    ///     </para>
    /// </summary>
    internal static int TargetIndex(int fromIndex, int insertionIndex, int count)
    {
        if (count <= 0) return 0;
        var target = insertionIndex > fromIndex ? insertionIndex - 1 : insertionIndex;
        return Math.Clamp(target, 0, count - 1);
    }

    /// <summary>
    ///     The FLAT chain position for a drop into gap
    ///     <paramref name="gapInRun" /> of the unit's OWN run, where the run's
    ///     items sit at <paramref name="runFlatIndices" /> in the flat chain
    ///     (ascending; the dragged unit included at its current place) and the
    ///     dragged unit is at flat index <paramref name="fromFlat" />.
    ///     <para>
    ///         A run's items are NOT contiguous in the chain — lane A and lane B
    ///         interleave in signal order — so the gap has to be mapped through
    ///         the run's own flat indices: land BEFORE the item the gap precedes,
    ///         or after the run's last. The gaps touching the dragged unit
    ///         itself return its own index (a visual no-op must not shuffle the
    ///         unit past other lanes' nodes in the flat order).
    ///     </para>
    /// </summary>
    internal static int TargetFlatIndex(IReadOnlyList<int> runFlatIndices, int fromFlat, int gapInRun)
    {
        if (runFlatIndices.Count == 0) return fromFlat;
        gapInRun = Math.Clamp(gapInRun, 0, runFlatIndices.Count);

        var fromRun = 0;
        while (fromRun < runFlatIndices.Count && runFlatIndices[fromRun] != fromFlat) fromRun++;
        if (fromRun < runFlatIndices.Count && (gapInRun == fromRun || gapInRun == fromRun + 1))
            return fromFlat;

        var insertAt = gapInRun >= runFlatIndices.Count
            ? runFlatIndices[^1] + 1
            : runFlatIndices[gapInRun];
        return insertAt > fromFlat ? insertAt - 1 : insertAt;
    }

    /// <summary>
    ///     The raw flat-chain INSERTION slot for gap <paramref name="gapInRun" />
    ///     of a run the inserted unit is NOT part of: before the item the gap
    ///     precedes, after the run's last item, or — for an EMPTY run (an empty
    ///     lane) — at <paramref name="emptyRunFallback" />, which is the region's
    ///     body start. This is what a GEAR drop feeds to
    ///     <see cref="PedalboardViewModel.AddGearAt" /> unadjusted, because the
    ///     new node is appended at the tail and everything at or after the slot
    ///     shifts right.
    /// </summary>
    internal static int InsertFlatIndex(
        IReadOnlyList<int> runFlatIndices, int gapInRun, int emptyRunFallback)
    {
        if (runFlatIndices.Count == 0) return emptyRunFallback;
        gapInRun = Math.Clamp(gapInRun, 0, runFlatIndices.Count);
        return gapInRun >= runFlatIndices.Count
            ? runFlatIndices[^1] + 1
            : runFlatIndices[gapInRun];
    }

    /// <summary>
    ///     The FLAT chain position for dropping an EXISTING unit into a run it
    ///     is NOT part of — the cross-run half <see cref="TargetFlatIndex" />
    ///     must not answer (its own-gap no-op rule would eat a drop at a foreign
    ///     run's tail). Insert before the gap's item, or after the run's last,
    ///     minus one when the unit vacates an earlier slot on the way.
    /// </summary>
    internal static int TargetFlatIndexInto(
        IReadOnlyList<int> runFlatIndices, int fromFlat, int gapInRun, int emptyRunFallback)
    {
        var insertAt = InsertFlatIndex(runFlatIndices, gapInRun, emptyRunFallback);
        if (insertAt < 0) return -1;
        return insertAt > fromFlat ? insertAt - 1 : insertAt;
    }

    /// <summary>Test seam: the in-flight session, or null.</summary>
    internal FireDragSession? Session => _session;

    public void Dispose()
    {
        Cancel();
        _board.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
        _gearList?.RemoveHandler(InputElement.PointerPressedEvent, OnGearPointerPressed);
        _surface.PointerMoved -= OnPointerMoved;
        _surface.PointerReleased -= OnPointerReleased;
        _surface.PointerCaptureLost -= OnPointerCaptureLost;
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        Cancel();
        if (!e.GetCurrentPoint(_board).Properties.IsLeftButtonPressed) return;
        if (e.Source is not Visual source) return;

        if (!IsDraggableSpot(source))
        {
            // A knob, switch or model window ON a unit's face still picks the
            // unit: the board is the editor, and turning a knob there opens the
            // unit's extras in the dock. The press is not handled, so the control
            // under it takes its gesture as usual.
            if (CardNodeOf(source) is { IsSelected: false } touched && _resolveScreen()?.Rig is { } touchedRig)
                touchedRig.SelectNodeCommand.Execute(touched);
            return;
        }

        var (panel, container) = RunOf(source);
        if (panel is null || container?.DataContext is not PedalNodeViewModel node) return;

        _container = container;
        _panel = panel;
        _node = node;
        _pressInContainer = e.GetPosition(container);
        _pressInBoard = e.GetPosition(_board);
        _pressed = true;
    }

    /// <summary>A press on a gear row arms a possible drag-onto-the-board; a
    /// press that never travels stays a click, and the row's own Button appends
    /// as it always did.</summary>
    private void OnGearPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        Cancel();
        if (!e.GetCurrentPoint(_board).Properties.IsLeftButtonPressed) return;
        if (e.Source is not Visual source) return;

        for (var v = source; v is not null; v = v.GetVisualParent())
        {
            if (v is Control { DataContext: RigGearItem item } row)
            {
                if (!item.CanAdd) return; // the row is disabled — so is its drag
                _container = row;
                _gearItem = item;
                _pressInContainer = e.GetPosition(row);
                _pressInBoard = e.GetPosition(_board);
                _pressed = true;
                return;
            }
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_pressed || _container is null) return;

        if (_session is null)
        {
            var delta = e.GetPosition(_board) - _pressInBoard;
            if (Math.Abs(delta.X) < DragThresholdPx && Math.Abs(delta.Y) < DragThresholdPx) return;

            _session = FireDragSession.Begin(
                _container, _pressInContainer, (object?)_node ?? _gearItem);
            if (_session is null) { _pressed = false; return; }

            _capturing = true;
            try { e.Pointer.Capture(_surface); }
            finally { _capturing = false; }
        }

        _session.MoveGhost(e.GetPosition(_session.Source));

        var target = TargetRunAt(e);

        // A junction never enters a lane: the splitter/merger ARE the region's
        // frame, and nesting one inside a branch is not a drop, it is a
        // restructure. Gear rows carry the same rule by module id.
        if (target?.Lane is not null && DraggingAJunction()) target = null;

        // The ORIGIN run is the forgiving fallback for a CARD drag — pointer
        // drifting off the run's pixels must not kill the marker mid-reorder.
        // A gear drag has no origin run: no run under the pointer, no slot.
        if (target is null && _node is not null && _panel is { } origin)
            target = new RunTarget(origin, origin.DataContext as RigBoardLane, origin);

        _target = target;
        if (target is not null && BuildLane(target) is { } lane)
            _session.Target(lane, e.GetPosition(lane.Host));
        else
            _session.ClearTarget();
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_session is { } session)
        {
            var slot = session.Slot;
            if (slot.IsValid && _target is { } target && slot.LaneId == LaneIdOf(target.Panel)
                && _resolveScreen() is { } screen)
            {
                if (_node is { } node) CommitNodeDrop(screen, node, target, slot.Index);
                else if (_gearItem is { } gear) CommitGearDrop(screen, gear, target, slot.Index);
            }
        }
        else if (_pressed && _node is { } clicked && _resolveScreen()?.Rig is { } rig)
        {
            // A press that never became a drag is a CLICK, and a click on a unit
            // selects it — which is what fills the module editor dock under the
            // board.
            //
            // It belongs here rather than on the card because the card must NOT
            // become a Button: a Button would swallow the press this controller
            // arms on, and the drag would be back to not working. (A gear-row
            // press that stayed a click is the row's own Button — nothing to do.)
            rig.SelectNodeCommand.Execute(clicked);
        }

        e.Pointer.Capture(null);
        Cancel();
    }

    /// <summary>An existing unit dropped on a run: same-run gaps map through
    /// <see cref="TargetFlatIndex" /> (with its own-gap no-op), cross-run gaps
    /// through <see cref="TargetFlatIndexInto" />; a lane target commits move +
    /// branch atomically, a serial target is a plain move.</summary>
    private static void CommitNodeDrop(
        AmpFxRigViewModel screen, PedalNodeViewModel node, RunTarget target, int gap)
    {
        var rig = screen.Rig;
        var fromFlat = rig.Nodes.IndexOf(node);
        if (fromFlat < 0) return;

        var runFlat = RunFlatIndices(target.Panel, rig);
        var index = runFlat.Contains(fromFlat)
            ? TargetFlatIndex(runFlat, fromFlat, gap)
            : TargetFlatIndexInto(runFlat, fromFlat, gap,
                target.Lane is { } l ? screen.BodyStartOf(l) : -1);
        if (index < 0) return;

        if (target.Lane is { } lane) rig.DropToRow(node, index, lane.Branch);
        else rig.MoveTo(node, index);
    }

    /// <summary>A gear row dropped on a run: append natively
    /// (<see cref="PedalboardViewModel.AddGearAt" /> moves it to the slot), then
    /// tag the branch when the run is a lane — the same two native ops the
    /// armed-gap click path runs, driven by geometry instead of a pre-armed
    /// dot.</summary>
    private static void CommitGearDrop(
        AmpFxRigViewModel screen, RigGearItem gear, RunTarget target, int gap)
    {
        var rig = screen.Rig;
        var index = InsertFlatIndex(
            RunFlatIndices(target.Panel, rig), gap,
            target.Lane is { } l ? screen.BodyStartOf(l) : rig.Nodes.Count);
        if (index < 0) return;

        var node = rig.AddGearAt(gear.Type, index);
        if (node is { IsJunction: false } && target.Lane is { } lane)
            rig.DropToRow(node, rig.Nodes.IndexOf(node), lane.Branch);
    }

    private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        // Ours-only: a child Button shedding its capture as we take it is not
        // the drag being cancelled. See `_capturing`.
        if (_capturing) return;
        Cancel();
    }

    private void Cancel()
    {
        _session?.Dispose();
        _session = null;
        _container = null;
        _panel = null;
        _node = null;
        _gearItem = null;
        _target = null;
        _pressed = false;
    }

    private bool DraggingAJunction() =>
        _node is { IsJunction: true }
        || (_gearItem is { } gear
            && (string.Equals(gear.Type.Id, SplitRegionLayout.SplitterId, StringComparison.Ordinal)
                || string.Equals(gear.Type.Id, SplitRegionLayout.MergerId, StringComparison.Ordinal)));

    /// <summary>The id of one run's lane — per PANEL, so a slot resolved in one
    /// run can never be committed against another run's indices.</summary>
    private static string LaneIdOf(Panel? panel) =>
        panel is null ? LaneId : $"{LaneId}.{panel.GetHashCode()}";

    /// <summary>One run under the pointer: its items panel, the lane it renders
    /// (null for a serial run), and the control the drop marker adorns — the
    /// panel, except for an EMPTY lane, whose panel has no size, so the marker
    /// stands on the lane's drop zone instead.</summary>
    private sealed record RunTarget(Panel Panel, RigBoardLane? Lane, Control MarkerHost);

    /// <summary>
    ///     The run under the pointer, resolved by hit-test: walk UP from the hit
    ///     control to the first DataContext that names a run —
    ///     <see cref="RigBoardLane" /> (a lane row, its drop zone, or a card in
    ///     it) or <see cref="RigSerialSegment" /> (a serial run) — then find that
    ///     run's own items panel. Null when the pointer is over nothing
    ///     droppable (the region chrome, the jacks, the wash).
    /// </summary>
    private RunTarget? TargetRunAt(PointerEventArgs e)
    {
        if (_board.InputHitTest(e.GetPosition(_board)) is not Visual hit) return null;

        for (var v = hit; v is not null; v = v.GetVisualParent())
        {
            if (v is Control control)
            {
                switch (control.DataContext)
                {
                    case RigBoardLane lane when RunPanelOf(lane.Items) is { } lanePanel:
                        return new RunTarget(lanePanel, lane,
                            lanePanel.Children.Count > 0 ? lanePanel : DropZoneOf(lane) ?? control);
                    case RigSerialSegment serial when RunPanelOf(serial.Items) is { } serialPanel:
                        return new RunTarget(serialPanel, null, serialPanel);
                }
            }

            if (ReferenceEquals(v, _board)) break;
        }

        return null;
    }

    /// <summary>The realized items panel whose ItemsControl binds exactly
    /// <paramref name="items" /> — identity, not shape, so two runs can never
    /// swap panels.</summary>
    private Panel? RunPanelOf(object items) =>
        _board.GetVisualDescendants().OfType<ItemsControl>()
            .FirstOrDefault(ic => ReferenceEquals(ic.ItemsSource, items))
            ?.ItemsPanelRoot;

    /// <summary>An empty lane's full-size drop zone — the marker's stand-in
    /// host while the lane's own panel has nothing (and so no size).</summary>
    private Control? DropZoneOf(RigBoardLane lane) =>
        _board.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(b => b.Classes.Contains("FireLaneDropZone")
                                 && ReferenceEquals(b.DataContext, lane));

    /// <summary>The target run's realized node containers as one drop lane.
    /// EMPTY bounds are legal now — an empty lane is a real target and
    /// <see cref="FireSlotHitTest" /> resolves it to gap 0 across the marker
    /// host's leading edge.</summary>
    private FireDragLane? BuildLane(RunTarget target)
    {
        var bounds = new List<Rect>(target.Panel.Children.Count);
        foreach (var child in target.Panel.Children)
        {
            if (child.Bounds is { Width: > 0, Height: > 0 } b
                && child.DataContext is PedalNodeViewModel)
            {
                bounds.Add(target.Panel == target.MarkerHost
                    ? b
                    : child.TranslatePoint(default, target.MarkerHost) is { } p
                        ? new Rect(p, b.Size)
                        : b);
            }
        }

        return new FireDragLane(LaneIdOf(target.Panel), target.MarkerHost, bounds);
    }

    /// <summary>The flat chain indices of one run's realized, measured node
    /// containers — the SAME filter <see cref="BuildLane" /> applies, so the
    /// slot's gap indices and this list can never drift apart on an unmeasured
    /// child.</summary>
    private static List<int> RunFlatIndices(Panel panel, PedalboardViewModel rig)
    {
        var flat = new List<int>(panel.Children.Count);
        foreach (var child in panel.Children)
        {
            if (child.Bounds is { Width: > 0, Height: > 0 }
                && child.DataContext is PedalNodeViewModel item)
            {
                var index = rig.Nodes.IndexOf(item);
                if (index >= 0) flat.Add(index);
            }
        }

        return flat;
    }

    /// <summary>
    ///     True when a press at <paramref name="source" /> should begin a drag.
    ///
    ///     <para>
    ///         <b>The whole card is the handle</b>, minus the controls on it.
    ///         The first version made only the card's HEAD draggable, reasoning
    ///         that a knob owns the vertical drag gesture and the two could not
    ///         share pixels. That is true of the knob and false of the card: the
    ///         owner reported the board as "you cant even drag and drop anymore",
    ///         because everyone grabs a card by its body. A title-bar-only handle
    ///         is a rule you have to be told.
    ///     </para>
    ///
    ///     <para>
    ///         So the walk refuses only where an INTERACTIVE control actually
    ///         is — a knob, a button, a picker, a switch, a text field — and
    ///         accepts everywhere else on the card. The gap between two knobs is
    ///         a grab point; the knob is not.
    ///     </para>
    /// </summary>
    private static bool IsDraggableSpot(Visual source)
    {
        for (var v = source; v is not null; v = v.GetVisualParent())
        {
            // Anything that owns its own pointer gesture wins the press. Checked
            // by TYPE rather than by class, so a control added to the card later
            // is excluded automatically instead of silently stealing the drag.
            if (v is FireKnob or RigFaceToggle or Button or ToggleButton or ComboBox or ToggleSwitch
                or TextBox or Slider or CheckBox)
            {
                return false;
            }

            if (v is StyledElement styled && styled.Classes.Contains("FireUnitCard"))
                return true;
        }

        return false;
    }

    /// <summary>The unit whose CARD contains <paramref name="source" />, or null
    /// for a press outside every card (an insert dot, a lane's mixer).</summary>
    internal static PedalNodeViewModel? CardNodeOf(Visual source)
    {
        for (var v = source; v is not null; v = v.GetVisualParent())
        {
            if (v is StyledElement styled && styled.Classes.Contains("FireUnitCard"))
                return styled.DataContext as PedalNodeViewModel;
        }

        return null;
    }

    /// <summary>
    ///     The RUN the press landed in: the items panel whose direct child
    ///     contains <paramref name="source" /> AND whose containers are chain
    ///     nodes. Walked nearest-first, because a card is full of its own
    ///     ItemsControls (the knob row, the file strip) whose containers are
    ///     params — the first ancestor panel with a NODE container is the
    ///     serial run or the lane the card actually sits in.
    /// </summary>
    private static (Panel? Panel, Control? Container) RunOf(Visual source)
    {
        for (var v = source; v is not null; v = v.GetVisualParent())
        {
            if (v is not Control control) continue;
            if (control.GetVisualParent() is not Panel panel) continue;
            if (panel.Parent is not ItemsPresenter and not ItemsControl) continue;
            if (control.DataContext is PedalNodeViewModel) return (panel, control);
        }

        return (null, null);
    }
}
