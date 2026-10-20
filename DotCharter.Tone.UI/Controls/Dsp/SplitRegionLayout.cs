namespace DotCharter.Tone.UI.Controls.Dsp;

/// <summary>
/// The routing-relevant facts about one chain node, fed to the pure
/// <see cref="SplitRegionLayout"/>. Mirrors the native ABI-v23 split-region
/// contract inputs: the module type (to spot <c>splitter</c>/<c>merger</c>), the
/// bypass flag (a bypassed splitter collapses the region) and the branch tag.
/// <see cref="EnabledBranches"/> is only read on a <c>splitter</c> node (its
/// <c>enabledBranches</c> param, 2..3) — ignored elsewhere.
/// </summary>
public readonly record struct BoardNodeInfo(string ModuleTypeId, bool Bypassed, int Branch, int EnabledBranches = 2);

/// <summary>What a laid-out board column holds.</summary>
public enum BoardColumnKind
{
    /// <summary>A single pedal outside any split region (renders in row 1).</summary>
    Pedal,

    /// <summary>The region-opening <c>splitter</c> or region-closing <c>merger</c>
    /// — a full-height junction node spanning the region's rows.</summary>
    Junction,

    /// <summary>The fanned parallel body: N stacked branch rows.</summary>
    Region
}

/// <summary>The fanned body of a split region: <see cref="Rows"/>[r] holds the
/// chain indices of the pedals tagged to branch <c>r+1</c> (r = 0..RowCount-1),
/// in chain order. <see cref="AutoMergeAtEnd"/> is true when the region has NO
/// closing merger — the engine then unity-averages the branches at the chain end
/// (surfaced as a subtle "auto-merge" indicator).</summary>
public sealed record SplitRegionBody(int RowCount, IReadOnlyList<IReadOnlyList<int>> Rows, bool AutoMergeAtEnd);

/// <summary>One column of the laid-out board.</summary>
public sealed record BoardColumn(BoardColumnKind Kind, int NodeIndex, SplitRegionBody? Region = null);

/// <summary>The full board layout: an ordered list of columns plus the split
/// region's row count (1 when the chain is linear — no active region).</summary>
public sealed record BoardLayout(IReadOnlyList<BoardColumn> Columns, int RegionRowCount);

/// <summary>
/// Pure, Avalonia-free split/parallel-region board layout (ABI v23). Mirrors the
/// native routing contract EXACTLY so the board draws what the engine renders:
/// <list type="bullet">
/// <item>A region is positional — the nodes strictly between the FIRST
/// non-bypassed <c>splitter</c> and the NEXT non-bypassed <c>merger</c> after
/// it. Outside a region every node renders in row 1.</item>
/// <item>Row = <c>clamp(max(tag, 1), 1, N)</c> where N = the splitter's
/// <c>enabledBranches</c> (clamped 2..3) — the exact native clamp.</item>
/// <item>A BYPASSED splitter collapses the whole chain to a single linear row
/// (tags inert).</item>
/// <item>A missing / bypassed merger ⇒ the region runs to the chain end and
/// auto-merges (unity average) — flagged via <see cref="SplitRegionBody.AutoMergeAtEnd"/>.</item>
/// <item>No nesting — a second splitter inside the open region is a plain wire
/// (rendered as an ordinary pedal in its branch row).</item>
/// </list>
/// Operates on chain INDICES so the caller maps them back to its own node view
/// models; never throws, and an empty / all-linear chain yields one Pedal column
/// per node.
/// </summary>
public static class SplitRegionLayout
{
    public const string SplitterId = "splitter";
    public const string MergerId = "merger";

    /// <summary>Clamps a raw branch tag to a valid row (1-based) for a region of
    /// <paramref name="rowCount"/> branches — the EXACT native clamp
    /// (<c>clamp(max(tag, 1), 1, N)</c>).</summary>
    public static int RowForBranch(int branch, int rowCount)
        => Math.Clamp(Math.Max(branch, 1), 1, Math.Max(1, rowCount));

    /// <summary>The row count a splitter opens: its <c>enabledBranches</c> clamped
    /// to the native 2..3 range.</summary>
    public static int RowCountFor(BoardNodeInfo splitter)
        => Math.Clamp(splitter.EnabledBranches, 2, 3);

    /// <summary>The 1-based branch row whose horizontal band contains
    /// <paramref name="y"/> within a region body of height
    /// <paramref name="regionHeight"/> split into <paramref name="rowCount"/>
    /// equal bands (row 1 = top). Clamped to 1..N; a non-positive height or a
    /// single row yields 1 — the drop-to-row picker.</summary>
    public static int RowForY(double y, double regionHeight, int rowCount)
    {
        if (rowCount <= 1 || regionHeight <= 0) return 1;
        var band = regionHeight / rowCount;
        var row = (int)(y / band) + 1;
        return Math.Clamp(row, 1, rowCount);
    }

    public static BoardLayout Compute(IReadOnlyList<BoardNodeInfo> nodes)
    {
        var columns = new List<BoardColumn>(nodes.Count);

        // The FIRST non-bypassed splitter opens the (single) region.
        var splitterIdx = -1;
        for (var i = 0; i < nodes.Count; i++)
        {
            if (IsSplitter(nodes[i]) && !nodes[i].Bypassed)
            {
                splitterIdx = i;
                break;
            }
        }

        if (splitterIdx < 0)
        {
            // Linear chain — every node (including any bypassed splitter/merger)
            // renders as an ordinary row-1 pedal.
            for (var i = 0; i < nodes.Count; i++)
                columns.Add(new BoardColumn(BoardColumnKind.Pedal, i));
            return new BoardLayout(columns, 1);
        }

        var rowCount = RowCountFor(nodes[splitterIdx]);

        // The NEXT non-bypassed merger after the splitter closes the region.
        var mergerIdx = -1;
        for (var j = splitterIdx + 1; j < nodes.Count; j++)
        {
            if (IsMerger(nodes[j]) && !nodes[j].Bypassed)
            {
                mergerIdx = j;
                break;
            }
        }

        var autoMerge = mergerIdx < 0;
        var regionEndExclusive = autoMerge ? nodes.Count : mergerIdx;

        // 1) Linear pedals ahead of the splitter.
        for (var i = 0; i < splitterIdx; i++)
            columns.Add(new BoardColumn(BoardColumnKind.Pedal, i));

        // 2) The splitter junction.
        columns.Add(new BoardColumn(BoardColumnKind.Junction, splitterIdx));

        // 3) The fanned region body — every body node dropped into its branch row.
        var rows = new List<int>[rowCount];
        for (var r = 0; r < rowCount; r++) rows[r] = [];
        for (var i = splitterIdx + 1; i < regionEndExclusive; i++)
        {
            var row = RowForBranch(nodes[i].Branch, rowCount);
            rows[row - 1].Add(i);
        }

        columns.Add(new BoardColumn(BoardColumnKind.Region, -1,
            new SplitRegionBody(rowCount, rows, autoMerge)));

        // 4) The merger junction + any trailing linear pedals.
        if (!autoMerge)
        {
            columns.Add(new BoardColumn(BoardColumnKind.Junction, mergerIdx));
            for (var i = mergerIdx + 1; i < nodes.Count; i++)
                columns.Add(new BoardColumn(BoardColumnKind.Pedal, i));
        }

        return new BoardLayout(columns, rowCount);
    }

    private static bool IsSplitter(BoardNodeInfo n) => string.Equals(n.ModuleTypeId, SplitterId, StringComparison.Ordinal);
    private static bool IsMerger(BoardNodeInfo n) => string.Equals(n.ModuleTypeId, MergerId, StringComparison.Ordinal);
}
