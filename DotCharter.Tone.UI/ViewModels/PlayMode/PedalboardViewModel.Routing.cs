// PedalboardViewModel: split/parallel ROUTING - the serial / 2-branch / 3-branch toolbar and the one-splitter-one-merger invariant.

using CommunityToolkit.Mvvm.Input;
using DotCharter.Tone.UI.Controls.Dsp;

namespace DotCharter.Tone.UI.ViewModels.PlayMode;

public sealed partial class PedalboardViewModel
{
    // ---- routing mode (the chain-builder canvas's left toolbar) -----------------

    /// <summary>The chain's live splitter — the FIRST non-bypassed one, exactly the
    /// node <see cref="SplitRegionLayout"/> treats as opening the region.</summary>
    private PedalNodeViewModel? ActiveSplitter =>
        Nodes.FirstOrDefault(n => n.IsSplitter && !n.Bypassed);

    /// <summary>How many parallel branches the chain currently runs: 1 = serial (no
    /// live split region), else the splitter's <c>enabledBranches</c> (2..3).</summary>
    public int RoutingBranches => ActiveSplitter?.EnabledBranches ?? 1;

    public bool IsRoutingSerial => RoutingBranches <= 1;
    public bool IsRoutingSplit2 => RoutingBranches == 2;
    public bool IsRoutingSplit3 => RoutingBranches == 3;

    private void NotifyRoutingMode()
    {
        OnPropertyChanged(nameof(RoutingBranches));
        OnPropertyChanged(nameof(IsRoutingSerial));
        OnPropertyChanged(nameof(IsRoutingSplit2));
        OnPropertyChanged(nameof(IsRoutingSplit3));
    }

    /// <summary>Any splitter in the chain — bypassed or not. Region OWNERSHIP is
    /// keyed on this, not on <see cref="ActiveSplitter"/>: a bypassed splitter still
    /// OCCUPIES the chain, so treating it as "no region" is what let the toolbar
    /// build a SECOND splitter+merger pair (the owner's two-mergers bug).</summary>
    private PedalNodeViewModel? AnySplitter => Nodes.FirstOrDefault(n => n.IsSplitter);

    /// <summary>Collapses the chain back to a single serial path: removes the
    /// junction pair (the body pedals stay, in chain order). Bypassed / stray
    /// junctions go too — serial means NO routing modules left, so the next split
    /// can never find a leftover merger to double up on.</summary>
    [RelayCommand]
    private void RoutingSerial()
    {
        if (!Nodes.Any(n => n.IsJunction)) return;

        foreach (var junction in Nodes.Where(n => n.IsJunction).ToList())
            Remove(junction);

        NotifyRoutingMode();
    }

    /// <summary>Opens (or re-widths) a 2-branch parallel region.</summary>
    [RelayCommand]
    private void RoutingSplit2() => SetRoutingBranches(2);

    /// <summary>Opens (or re-widths) a 3-branch parallel region.</summary>
    [RelayCommand]
    private void RoutingSplit3() => SetRoutingBranches(3);

    /// <summary>Builds the splitter+merger pair AROUND the current selection (so the
    /// selected pedal lands inside the new parallel region) — or appends the pair at
    /// the chain end when nothing is selected. An existing region just re-widths to
    /// <paramref name="branches"/>.</summary>
    private void SetRoutingBranches(int branches)
    {
        branches = Math.Clamp(branches, 2, 3);

        // A region ALREADY EXISTS (even a bypassed one): re-width it in place —
        // never build a second junction pair.
        if (AnySplitter is { } live)
        {
            live.Bypassed = false; // a bypassed splitter reads as "serial" — re-arm it
            live.SetEnabledBranches(branches); // StructureChanged → RebuildBoardColumns
            // Nothing else moves: the region already has a body (and a merger, or a
            // deliberate auto-merge). Re-widthing must never manufacture junctions.
            RebuildBoardColumns();
            return;
        }

        var splitterType = AvailableModules.FirstOrDefault(m => m.Id == SplitterModuleTypeId);
        if (splitterType == null) return;

        var anchor = SelectedNode != null ? Nodes.IndexOf(SelectedNode) : -1;

        var splitter = AddGearCore(splitterType);
        if (splitter == null) return;

        if (anchor >= 0)
            MoveNodeToIndex(splitter, anchor); // splitter lands ahead of the selection

        splitter.SetEnabledBranches(branches);
        // …and the merger right after the wrapped selection (or right after the
        // splitter when nothing was selected — an empty region, ready to fill).
        EnsureClosingMerger(splitter, wrapNext: anchor >= 0);
        RebuildBoardColumns();
    }

    /// <summary>Guarantees EXACTLY ONE live merger closing <paramref name="splitter"/>'s
    /// region: an EXISTING merger is re-armed and re-seated, and only a chain with
    /// none at all gets a fresh one. The old code added a merger unconditionally, so
    /// any leftover (a bypassed one, or one the positional region rule skipped) left
    /// TWO Mergers on the board — the owner's bug. <paramref name="wrapNext"/> seats
    /// it one node further along, so the pedal right after the splitter ends up
    /// INSIDE the region.</summary>
    private void EnsureClosingMerger(PedalNodeViewModel splitter, bool wrapNext)
    {
        var splitterIndex = Nodes.IndexOf(splitter);
        if (splitterIndex < 0) return;

        var merger = Nodes.FirstOrDefault(n => n.IsMerger);
        if (merger != null)
        {
            merger.Bypassed = false; // a bypassed merger doesn't close the region
        }
        else
        {
            var mergerType = AvailableModules.FirstOrDefault(m => m.Id == MergerModuleTypeId);
            if (mergerType == null) return;
            merger = AddGearCore(mergerType);
            if (merger == null) return;
        }

        // Seat it after the splitter (+1 more when it should wrap the next pedal),
        // clamped into the chain — MoveNodeToIndex clamps and no-ops on equality.
        var target = splitterIndex + (wrapNext ? 2 : 1);
        if (Nodes.IndexOf(merger) != target)
            MoveNodeToIndex(merger, target);
    }

    /// <summary>The routing module ids whose add-flow is special-cased (a splitter
    /// auto-pairs with a merger).</summary>
    private const string SplitterModuleTypeId = "splitter";
    private const string MergerModuleTypeId = "merger";
}
