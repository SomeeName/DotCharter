// AmpFxRigViewModel: the board's LANE FOLD - serial runs and the split region's stacked lanes, reconciled in place (never cleared).

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Tone.UI.Controls.Dsp;

namespace DotCharter.Tone.UI.ViewModels.PlayMode;

public sealed partial class AmpFxRigViewModel
{
    /// <summary>
    ///     The board, folded into RENDERABLE segments: a serial run of cards, or
    ///     the split region with its stacked lanes (the v8 mockup's
    ///     <c>bk.lanes</c> panel). The fold is <see cref="SplitRegionLayout" /> —
    ///     the SAME pure layout the native routing contract is mirrored through —
    ///     so the board can never draw a region the engine is not running.
    ///
    ///     <para>
    ///         Reconciled, never cleared: the board's ItemsControls are
    ///         non-virtualized, so this collection follows the same no-Reset,
    ///         instance-survival rules as <see cref="Chain" />
    ///         (<c>AmpFxRigChainMirrorTests</c>); the lane fold's own guard is
    ///         <c>AmpFxRigBoardLaneTests</c>.
    ///     </para>
    /// </summary>
    public ObservableCollection<RigBoardSegment> BoardSegments { get; } = [];

    /// <summary>The colour the serial run's wires draw in: lane A's on a
    /// series board (the design labels that run "Lane A"), the accent for the
    /// trunk around a split region.</summary>
    public string SerialLaneToken => _rig.IsRoutingSerial ? "FireLaneA" : "FireAccent";

    /// <summary>"Lane A" over the series run.</summary>
    public string SerialLaneLabel => _loc.Format("Rig_Editor_LaneLabel", "A");

    /// <summary>The board caption: "Series signal chain" or "3 parallel lanes".</summary>
    public string RoutingSummary => _rig.IsRoutingSerial
        ? _loc["Rig_Board_SerialSummary"]
        : _loc.Format("Rig_Board_ParallelSummary", _rig.RoutingBranches);

    /// <summary>
    ///     Which parallel LANE the armed insertion gap belongs to (1..3), or null
    ///     for a serial gap. Armed together with <see cref="InsertAt" /> by the
    ///     insert dots inside a lane, and read by <see cref="AddGear" /> so a
    ///     unit added into lane B actually lands ON branch 2 — an index alone
    ///     would seat it at the right chain slot with the default branch tag,
    ///     which renders in lane A.
    /// </summary>
    [ObservableProperty]
    private int? _insertBranch;

    /// <summary>An armed gap that stops existing (chain shrank, region closed)
    /// drops its lane half with it.</summary>
    partial void OnInsertAtChanged(int? value)
    {
        if (value is null) InsertBranch = null;
    }

    /// <summary>
    ///     Arms the gap at the END of <paramref name="lane" /> — the trailing
    ///     insert dot every lane row carries (an empty lane has nothing else to
    ///     click). Toggles off when the same lane gap is already armed.
    /// </summary>
    [RelayCommand]
    private void InsertIntoLane(RigBoardLane? lane)
    {
        if (lane is null) return;

        var index = lane.Items.Count > 0
            ? Chain.IndexOf(lane.Items[^1]) + 1
            : RegionBodyStart();
        if (index <= 0) return;

        if (InsertAt == index && InsertBranch == lane.Branch)
        {
            InsertAt = null;
            return;
        }

        InsertAt = index;
        InsertBranch = lane.Branch;
    }

    /// <summary>The first body slot of the live split region (right after the
    /// splitter), or -1 when the chain is serial.</summary>
    private int RegionBodyStart()
    {
        var split = BoardSegments.OfType<RigSplitSegment>().FirstOrDefault();
        return BodyStartOf(split);
    }

    /// <summary>
    ///     The first body slot of the region <paramref name="lane" /> belongs
    ///     to, or -1 when the lane is not on the board. This is the drop
    ///     controller's fallback slot for an EMPTY lane — a drop there has no
    ///     item to land beside, but "right after the splitter" is always inside
    ///     the region, and the branch tag decides the lane.
    /// </summary>
    internal int BodyStartOf(RigBoardLane lane) =>
        BodyStartOf(BoardSegments.OfType<RigSplitSegment>()
            .FirstOrDefault(s => s.Lanes.Contains(lane)));

    private int BodyStartOf(RigSplitSegment? split)
    {
        if (split is null) return -1;
        var index = Chain.IndexOf(split.Splitter);
        return index < 0 ? -1 : index + 1;
    }

    /// <summary>The lane <paramref name="node" /> renders in (1..3), or null when
    /// it is serial / a junction. Read off the folded segments rather than the
    /// raw branch tag, because an untagged node (branch 0) INSIDE the region
    /// renders in lane 1 — the native clamp.</summary>
    private int? LaneOf(PedalNodeViewModel node) =>
        BoardSegments.OfType<RigSplitSegment>()
            .SelectMany(s => s.Lanes)
            .FirstOrDefault(l => l.Items.Contains(node))?.Branch;

    // ── the fold + reconcile ─────────────────────────────────────────────────

    /// <summary>
    ///     Re-folds <see cref="PedalboardViewModel.Nodes" /> into
    ///     <see cref="BoardSegments" />. Hooked to the rig's
    ///     <see cref="PedalboardViewModel.BoardStructureChanged" /> — one refold
    ///     per finished gesture, covering the mutations that never touch the
    ///     Nodes collection (a branch re-tag, a 2↔3 re-width, a junction
    ///     bypass): the routing properties are a LAYOUT input, and a
    ///     collection-change-only subscription leaves the board painting the old
    ///     routing (the 0.16 owner bug).
    /// </summary>
    internal void RefreshBoardSegments()
    {
        OnPropertyChanged(nameof(DockLaneLabel));
        OnPropertyChanged(nameof(DockLaneTokenKey));
        OnPropertyChanged(nameof(RoutingSummary));
        OnPropertyChanged(nameof(SerialLaneToken));
        var nodes = _rig.Nodes;
        var layout = SplitRegionLayout.Compute(
            nodes.Select(n => new BoardNodeInfo(n.ModuleTypeId, n.Bypassed, n.Branch, n.EnabledBranches))
                .ToList());

        // 1) Fold the pure column list into the segment plans the board renders.
        //    A junction column folds INTO the split segment (the region visual is
        //    the junction pair); serial pedals accumulate into runs.
        var plans = new List<object>();
        var run = new List<PedalNodeViewModel>();
        RigSplitPlan? open = null;

        void FlushRun()
        {
            if (run.Count == 0) return;
            plans.Add(run);
            run = [];
        }

        foreach (var col in layout.Columns)
        {
            switch (col.Kind)
            {
                case BoardColumnKind.Pedal:
                    run.Add(nodes[col.NodeIndex]);
                    break;

                case BoardColumnKind.Junction:
                    var junction = nodes[col.NodeIndex];
                    if (junction.IsSplitter)
                    {
                        FlushRun();
                        open = new RigSplitPlan(junction);
                    }
                    else if (open is not null)
                    {
                        open.Merger = junction;
                        plans.Add(open);
                        open = null;
                    }

                    break;

                case BoardColumnKind.Region:
                    if (open is not null && col.Region is { } region)
                    {
                        for (var r = 0; r < region.RowCount; r++)
                            open.Lanes.Add(region.Rows[r].Select(i => nodes[i]).ToList());
                    }

                    break;
            }
        }

        if (open is not null) plans.Add(open); // auto-merge: region runs to the chain end
        FlushRun();

        // 2) Reconcile the live segments against the plan: reuse a segment whose
        //    KIND (and, for a split, whose splitter) survived, replace at index
        //    otherwise, trim the tail. Individual operations only — no Reset.
        for (var i = 0; i < plans.Count; i++)
        {
            var current = i < BoardSegments.Count ? BoardSegments[i] : null;
            switch (plans[i])
            {
                case List<PedalNodeViewModel> serialPlan:
                    if (current is RigSerialSegment serial)
                    {
                        ReconcileItems(serial.Items, serialPlan);
                    }
                    else
                    {
                        var fresh = new RigSerialSegment();
                        ReconcileItems(fresh.Items, serialPlan);
                        PlaceSegment(i, fresh);
                    }

                    break;

                case RigSplitPlan splitPlan:
                    if (current is RigSplitSegment split && ReferenceEquals(split.Splitter, splitPlan.Splitter))
                    {
                        ReconcileSplit(split, splitPlan);
                    }
                    else
                    {
                        var fresh = new RigSplitSegment(splitPlan.Splitter);
                        ReconcileSplit(fresh, splitPlan);
                        PlaceSegment(i, fresh);
                    }

                    break;
            }
        }

        while (BoardSegments.Count > plans.Count)
        {
            ReleaseMixers(BoardSegments[^1]);
            BoardSegments.RemoveAt(BoardSegments.Count - 1);
        }

        // The gear panel's add-target options are a projection of the lanes that
        // now exist, so they move with the region — opening, widening, narrowing
        // or closing it — rather than being offered for a lane the board dropped.
        RefreshAddTargetLanes();
    }

    private void PlaceSegment(int index, RigBoardSegment segment)
    {
        if (index < BoardSegments.Count)
        {
            ReleaseMixers(BoardSegments[index]);
            BoardSegments[index] = segment;
        }
        else
        {
            BoardSegments.Add(segment);
        }
    }

    private static void ReleaseMixers(RigBoardSegment segment)
    {
        if (segment is RigSplitSegment split)
            foreach (var lane in split.Lanes) lane.Mixer.Dispose();
    }

    private void ReconcileSplit(RigSplitSegment split, RigSplitPlan plan)
    {
        while (split.Lanes.Count > plan.Lanes.Count)
        {
            split.Lanes[^1].Mixer.Dispose();
            split.Lanes.RemoveAt(split.Lanes.Count - 1);
        }
        while (split.Lanes.Count < plan.Lanes.Count)
            split.Lanes.Add(new RigBoardLane(split.Lanes.Count + 1, _loc));

        for (var r = 0; r < plan.Lanes.Count; r++)
        {
            ReconcileItems(split.Lanes[r].Items, plan.Lanes[r]);
            split.Lanes[r].Mixer.Attach(plan.Merger);
        }

        split.Merger = plan.Merger;
    }

    /// <summary>Mutates <paramref name="items" /> into <paramref name="target" />
    /// with minimal operations (remove gone / move survivors / insert new) — the
    /// same diff <see cref="RefreshChain" /> runs on <see cref="Chain" />.</summary>
    private static void ReconcileItems(
        ObservableCollection<PedalNodeViewModel> items, IReadOnlyList<PedalNodeViewModel> target)
    {
        var keep = new HashSet<PedalNodeViewModel>(target);
        for (var i = items.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(items[i]))
                items.RemoveAt(i);
        }

        for (var i = 0; i < target.Count; i++)
        {
            if (i < items.Count && ReferenceEquals(items[i], target[i])) continue;
            var j = items.IndexOf(target[i]);
            if (j >= 0) items.Move(j, i);
            else items.Insert(i, target[i]);
        }
    }

    /// <summary>The transient plan for one split segment while folding — never
    /// rendered, so it is a private scratch type rather than a view model.</summary>
    private sealed class RigSplitPlan
    {
        public RigSplitPlan(PedalNodeViewModel splitter) => Splitter = splitter;

        public PedalNodeViewModel Splitter { get; }
        public List<List<PedalNodeViewModel>> Lanes { get; } = [];
        public PedalNodeViewModel? Merger { get; set; }
    }
}

/// <summary>
///     One renderable stretch of the board: a <see cref="RigSerialSegment" />
///     (cards in a horizontal run) or a <see cref="RigSplitSegment" /> (the
///     parallel region). The board ItemsControl binds
///     <see cref="AmpFxRigViewModel.BoardSegments" /> and picks a template by
///     type — the same shape the retired rack's <c>BoardColumnViewModel</c>
///     used, folded one level further so the junction pair IS the region visual.
/// </summary>
public abstract class RigBoardSegment : ObservableObject
{
}

/// <summary>A serial run of units — cards with connectors between them, exactly
/// the flat board's one row, now bounded by the next junction.</summary>
public sealed class RigSerialSegment : RigBoardSegment
{
    /// <summary>The run's units, in signal order. Reconciled in place.</summary>
    public ObservableCollection<PedalNodeViewModel> Items { get; } = [];
}

/// <summary>
///     The live split region: the opening splitter, N stacked lanes, and the
///     closing merger (null while the region auto-merges at the chain end).
///     The splitter/merger NODES render as this segment's frame rather than as
///     cards — the region visual is the junction pair.
/// </summary>
public sealed partial class RigSplitSegment : RigBoardSegment
{
    /// <summary>Creates the segment for one region.</summary>
    /// <param name="splitter">The region-opening splitter node — the segment's
    /// reuse identity across refolds.</param>
    public RigSplitSegment(PedalNodeViewModel splitter) => Splitter = splitter;

    /// <summary>The region-opening splitter node.</summary>
    public PedalNodeViewModel Splitter { get; }

    /// <summary>The stacked lanes, top (branch 1 = A) to bottom. Reconciled in
    /// place; a 2↔3 re-width adds/removes the LAST lane only.</summary>
    public ObservableCollection<RigBoardLane> Lanes { get; } = [];

    /// <summary>The region-closing merger, or null while the region runs to the
    /// chain end and the engine unity-averages the branches
    /// (<see cref="HasAutoMerge" />).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAutoMerge))]
    private PedalNodeViewModel? _merger;

    /// <summary>True when the region has no closing merger — surfaced as the
    /// board's "auto-merge at end" tag.</summary>
    public bool HasAutoMerge => Merger is null;
}

/// <summary>One lane of the split region: its branch, its letter, its colour
/// token and its units in signal order.</summary>
public sealed class RigBoardLane : ObservableObject
{
    private readonly ILocalizationService _loc;

    /// <summary>Creates the lane for one branch.</summary>
    /// <param name="branch">1-based branch index (= the native branch tag).</param>
    /// <param name="localization">The words of its chip and mixer labels.</param>
    public RigBoardLane(int branch, ILocalizationService localization)
    {
        Branch = branch;
        _loc = localization;
        Mixer = new RigLaneMixerViewModel(branch);
        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>True while the lane has no units — what swaps the lane's run
    /// for the full-size drop zone ("drag a pedal here"). A bare trailing dot
    /// was not an honest target for an empty lane.</summary>
    public bool IsEmpty => Items.Count == 0;

    /// <summary>1-based branch index this lane renders (= native branch tag).</summary>
    public int Branch { get; }

    public RigLaneMixerViewModel Mixer { get; }

    /// <summary>"Lane A": the tone-rig design's lane chip on the graph.</summary>
    public string ChipLabel => _loc.Format("Rig_Editor_LaneLabel", Label);

    /// <summary>"Mixer A": the compact mixer node at the lane's end.</summary>
    public string MixerLabel => _loc.Format("Rig_Editor_MixerLabel", Label);

    /// <summary>The lane letter (branch 1 = A) — the mockup's lane badge.</summary>
    public string Label => Branch switch
    {
        1 => "A",
        2 => "B",
        3 => "C",
        _ => Branch.ToString(System.Globalization.CultureInfo.InvariantCulture)
    };

    /// <summary>The resource key of the lane's identity colour
    /// (<c>FireLaneA</c>/<c>B</c>/<c>C</c> — the mockup's fixed path colours).</summary>
    public string TokenKey => Branch switch
    {
        1 => "FireLaneA",
        2 => "FireLaneB",
        _ => "FireLaneC"
    };

    /// <summary>The lane's units, in signal order. Reconciled in place.</summary>
    public ObservableCollection<PedalNodeViewModel> Items { get; } = [];
}
