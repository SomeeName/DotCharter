using DotCharter.UI.Controls;

namespace DotCharter.Tests.UI;

/// <summary>
///     #751 (inspector design D): the pure rules behind <see cref="ColumnFlowPanel" />. The width tier of the
///     card body (Tiny / Small / Usual / Wide at the pane's 480 / 620 / 900 px steps, measured on the body,
///     i.e. less its two 8 px margins), the short body, and which card stands in which column and which
///     ones share a pager. <c>Perf/NoteSectionStripUiPerfSmokeTests</c> lays the real panel out.
/// </summary>
public sealed class ColumnFlowTiersTests
{
    [Theory]
    [InlineData(300, ColumnFlowTier.Tiny)]
    [InlineData(463.9, ColumnFlowTier.Tiny)]
    [InlineData(464, ColumnFlowTier.Small)]
    [InlineData(514, ColumnFlowTier.Small)]
    [InlineData(603.9, ColumnFlowTier.Small)]
    [InlineData(604, ColumnFlowTier.Usual)]
    [InlineData(883.9, ColumnFlowTier.Usual)]
    [InlineData(884, ColumnFlowTier.Wide)]
    [InlineData(2000, ColumnFlowTier.Wide)]
    public void TheTier_FollowsTheBodyWidth(double width, ColumnFlowTier tier) =>
        Assert.Equal(tier, ColumnFlowTiers.ForWidth(width));

    [Fact]
    public void NoWidthYet_IsSmall_TheDesignsPrimarySize() =>
        Assert.Equal(ColumnFlowTier.Small, ColumnFlowTiers.ForWidth(double.PositiveInfinity));

    [Theory]
    [InlineData(200, true)]
    [InlineData(319.9, true)]
    [InlineData(320, false)]
    [InlineData(double.PositiveInfinity, false)]
    public void AShortBody_IsUnder320Px(double height, bool isShort) =>
        Assert.Equal(isShort, ColumnFlowTiers.IsShort(height));

    [Theory]
    [InlineData(ColumnFlowTier.Tiny)]
    [InlineData(ColumnFlowTier.Small)]
    [InlineData(ColumnFlowTier.Wide)]
    public void OneCard_StandsAloneInOneColumn_WithNoPager(ColumnFlowTier tier)
    {
        var plan = ColumnFlowPages.Plan(tier, isShort: false, cards: 1, current: 0);
        Assert.Equal([[0]], plan.Columns);
        Assert.Empty(plan.Pages);
        Assert.Equal(-1, plan.PagedColumn);
    }

    [Fact]
    public void NoCard_IsAnEmptyPlan() => Assert.Empty(ColumnFlowPages.Plan(ColumnFlowTier.Wide, false, 0, 0).Columns);

    [Fact]
    public void Tiny_ShowsOneCardAtATime_AndPagesThemAll()
    {
        var plan = ColumnFlowPages.Plan(ColumnFlowTier.Tiny, isShort: false, cards: 3, current: 2);
        Assert.Equal([[2]], plan.Columns);
        Assert.Equal([0, 1, 2], plan.Pages);
        Assert.Equal(0, plan.PagedColumn);
        Assert.Equal(2, plan.Current);
    }

    [Fact]
    public void Small_KeepsTheFirstCard_AndPagesTheRestBesideIt()
    {
        var plan = ColumnFlowPages.Plan(ColumnFlowTier.Small, isShort: false, cards: 3, current: 0);
        // A current card that is not a page (the primary) shows the first page.
        Assert.Equal([[0], [1]], plan.Columns);
        Assert.Equal([1, 2], plan.Pages);
        Assert.Equal(1, plan.PagedColumn);
        Assert.Equal(1, plan.Current);
        Assert.Equal([1.06, 1], plan.Weights);

        plan = ColumnFlowPages.Plan(ColumnFlowTier.Small, isShort: false, cards: 3, current: 2);
        Assert.Equal([[0], [2]], plan.Columns);
    }

    [Theory]
    [InlineData(ColumnFlowTier.Small)]
    [InlineData(ColumnFlowTier.Usual)]
    [InlineData(ColumnFlowTier.Wide)]
    public void TwoCards_StandSideBySide_WithNoPager(ColumnFlowTier tier)
    {
        var plan = ColumnFlowPages.Plan(tier, isShort: true, cards: 2, current: 1);
        Assert.Equal([[0], [1]], plan.Columns);
        Assert.Empty(plan.Pages);
    }

    [Fact]
    public void Usual_Tall_StacksTheRestBesideTheFirst()
    {
        var plan = ColumnFlowPages.Plan(ColumnFlowTier.Usual, isShort: false, cards: 3, current: 0);
        Assert.Equal([[0], [1, 2]], plan.Columns);
        Assert.Empty(plan.Pages);
    }

    [Fact]
    public void Usual_FourCards_TakeThreeColumns()
    {
        var plan = ColumnFlowPages.Plan(ColumnFlowTier.Usual, isShort: false, cards: 4, current: 0);
        Assert.Equal([[0], [1], [2, 3]], plan.Columns);
        Assert.Equal([1, 1, 1], plan.Weights);
    }

    [Fact]
    public void Usual_Short_PagesLikeSmall()
    {
        var plan = ColumnFlowPages.Plan(ColumnFlowTier.Usual, isShort: true, cards: 3, current: 2);
        Assert.Equal([[0], [2]], plan.Columns);
        Assert.Equal([1, 2], plan.Pages);
    }

    [Theory]
    [InlineData(3, new[] { 1 }, new[] { 2 })]
    [InlineData(4, new[] { 1 }, new[] { 2 }, new[] { 3 })]
    [InlineData(6, new[] { 1 }, new[] { 2 }, new[] { 3, 4, 5 })]
    public void Wide_GivesEveryCardItsOwnColumn_TheFourthStacksTheRest(int cards, params int[][] rest)
    {
        var plan = ColumnFlowPages.Plan(ColumnFlowTier.Wide, isShort: false, cards, current: 0);
        Assert.Equal([[0], .. rest], plan.Columns);
        Assert.Empty(plan.Pages);
        Assert.All(plan.Weights, weight => Assert.Equal(1, weight));
    }

    [Fact]
    public void Wide_Short_StillPages()
    {
        var plan = ColumnFlowPages.Plan(ColumnFlowTier.Wide, isShort: true, cards: 4, current: 3);
        Assert.Equal([[0], [3]], plan.Columns);
        Assert.Equal([1, 2, 3], plan.Pages);
    }
}
