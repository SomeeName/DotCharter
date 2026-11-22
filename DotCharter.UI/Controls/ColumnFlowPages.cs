namespace DotCharter.UI.Controls;

/// <summary>
/// Where the cards of a <see cref="ColumnFlowPanel" /> stand.
/// </summary>
/// <param name="Columns">Per column, left to right, the cards stacked in it (indices into the shown cards).</param>
/// <param name="Weights">Per column, its share of the width.</param>
/// <param name="Pages">The cards the pager lists, in card order; empty when nothing is paged.</param>
/// <param name="PagedColumn">The column the pager stands over, or -1.</param>
/// <param name="Current">The paged card on screen, or -1.</param>
public sealed record ColumnFlowPlan(
    IReadOnlyList<IReadOnlyList<int>> Columns,
    IReadOnlyList<double> Weights,
    IReadOnlyList<int> Pages,
    int PagedColumn,
    int Current)
{
    public static readonly ColumnFlowPlan Empty = new([], [], [], -1, -1);
}

/// <summary>
/// The card layout of inspector design D, pure so the gate pins it:
///     <list type="bullet">
///         <item>One card stands alone, whatever the width.</item>
///         <item><see cref="ColumnFlowTier.Tiny" />: one card at a time, the pager lists every card.</item>
///         <item>Otherwise the FIRST card keeps the left column. Two cards stand side by side.</item>
///         <item>More than one card beside it share a pager in the right column while the body is Small or
///         short; a tall Wide body gives each its own column (the fourth and later stack in the last one); a
///         tall Usual body gives the second its own column when there are four cards, else stacks them
///         beside the first.</item>
///     </list>
///     The two-column split is the design's 1.06 : 1 (the first card holds the board); three or more share equally.
/// </summary>
public static class ColumnFlowPages
{
    private const double PrimaryWeight = 1.06;

    /// <param name="tier">The body's width tier.</param>
    /// <param name="isShort">Whether the body is short (<see cref="ColumnFlowTiers.IsShort" />).</param>
    /// <param name="cards">How many cards are shown.</param>
    /// <param name="current">The card the pager was last set to; a card that is not paged picks the first page.</param>
    public static ColumnFlowPlan Plan(ColumnFlowTier tier, bool isShort, int cards, int current)
    {
        if (cards <= 0) return ColumnFlowPlan.Empty;
        if (cards == 1) return new ColumnFlowPlan([[0]], [1], [], -1, -1);
        if (tier == ColumnFlowTier.Tiny) return Paged([.. Enumerable.Range(0, cards)], current, primary: false);

        var rest = Enumerable.Range(1, cards - 1).ToArray();
        if (rest.Length == 1) return new ColumnFlowPlan([[0], [1]], [PrimaryWeight, 1], [], -1, -1);
        if (tier == ColumnFlowTier.Small || isShort) return Paged(rest, current, primary: true);
        if (tier == ColumnFlowTier.Wide)
        {
            IReadOnlyList<int>[] wide = rest.Length <= 3
                ? [[0], .. rest.Select(card => (IReadOnlyList<int>)[card])]
                : [[0], [rest[0]], [rest[1]], rest[2..]];
            return new ColumnFlowPlan(wide, [.. wide.Select(_ => 1d)], [], -1, -1);
        }

        return rest.Length == 3
            ? new ColumnFlowPlan([[0], [rest[0]], rest[1..]], [1, 1, 1], [], -1, -1)
            : new ColumnFlowPlan([[0], rest], [PrimaryWeight, 1], [], -1, -1);
    }

    private static ColumnFlowPlan Paged(int[] pages, int current, bool primary)
    {
        var shown = pages.Contains(current) ? current : pages[0];
        return primary
            ? new ColumnFlowPlan([[0], [shown]], [PrimaryWeight, 1], pages, 1, shown)
            : new ColumnFlowPlan([[shown]], [1], pages, 0, shown);
    }
}
