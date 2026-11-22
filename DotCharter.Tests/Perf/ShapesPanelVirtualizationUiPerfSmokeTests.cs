using Avalonia.Controls;
using Avalonia.VisualTree;
using DotCharter.Tests.Presentation.Editor;
using DotCharter.UI.Controls;
using DotCharter.UI.Views.Editor;

namespace DotCharter.Tests.Perf;

[Collection("HeadlessUI")]
public sealed class ShapesPanelVirtualizationUiPerfSmokeTests
{
    /// <summary>
    ///     The list card's lines are virtualized at every tier: as many columns of lines as the LIST is wide enough for
    ///     (one under 680 px of list), only the viewport's lines realized, the last one reachable, the head pinned.
    /// </summary>
    [Theory]
    [InlineData(320, 370)]
    [InlineData(530, 370)]
    [InlineData(680, 524)]
    [InlineData(1050, 524)]
    [InlineData(1500, 524)]
    public Task OnlyTheViewportIsRealizedAndTheFinalCardIsReachable(double width, double height) =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var rig = new ShapesSwitchRig();
            var view = new ShapesPanelView { DataContext = rig.Screen };
            var window = NarrowWidthLayout.NewWindow(width, height, view);
            window.Show();
            ShapesPanelCards.Settle(view);
            try
            {
                Assert.True(view.Bounds is { Width: > 0, Height: > 0 });
                Assert.True(view.RowsList.Bounds is { Width: > 0, Height: > 0 }, $"the list laid out {view.RowsList.Bounds}");
                var columns = Math.Max(1, (int)(view.RowsList.Bounds.Width / ShapeRowsList.MinCardWidth));
                var strips = view.RowsList.Items.Cast<ShapeCardStrip>().ToList();
                Assert.Equal(columns, strips[0].Cards.Count);
                Assert.Equal(rig.Screen.Rows, strips.SelectMany(s => s.Cards));
                Assert.InRange(ShapesPanelCards.Realized(view).Count(), 1, 12 * columns);
                Assert.True(ShapesPanelCards.Realized(view).Count() < rig.Screen.Rows.Count);
                Assert.DoesNotContain(view.FindControl<TextBlock>("ShapesHeader")!.GetVisualAncestors(), a => a is ScrollViewer);

                var last = rig.Screen.Rows[^1];
                var card = ShapesPanelCards.Card(view, last);

                Assert.Same(last, card.DataContext);
                Assert.True(card.Bounds is { Width: > 0, Height: > 0 });
                Assert.InRange(ShapesPanelCards.Realized(view).Count(), 1, 12 * columns);
                rig.Screen.SearchText = "Shape 63";
                ShapesPanelCards.Settle(view);
                Assert.Single(rig.Screen.Rows);
                Assert.Same(last, ShapesPanelCards.Realized(view).Single().DataContext);
            }
            finally { window.Close(); }
        }, TestContext.Current.CancellationToken);

    [Fact]
    public Task AReplacedCatalogueRebindsTheCardsInViewInsteadOfBuildingThem() =>
        WithSwitchedCatalogue((view, rig, before) =>
        {
            var cards = ShapesPanelCards.Realized(view).ToList();

            Assert.Equal(before.Cards, cards);
            Assert.Equal(before.Built, Built(view));
            Assert.Equal(rig.Screen.Rows.Take(cards.Count), cards.Select(c => c.DataContext));
            Assert.All(cards, c => Assert.True(c.Bounds is { Width: > 0, Height: > 0 }));
        });

    [Fact]
    public Task AReplacedCatalogueKeepsItsStripsAndHandsThemTheNewRows() =>
        WithSwitchedCatalogue((view, rig, before) =>
        {
            var strips = view.RowsList.Items.Cast<ShapeCardStrip>().ToList();

            Assert.Equal(before.Strips, strips);
            Assert.Equal(rig.Screen.Rows, strips.SelectMany(s => s.Cards));
        });

    private static int Built(ShapesPanelView view) => view.GetVisualDescendants().OfType<ShapeStripCards>().Sum(s => s.CardsBuilt);

    /// <summary>Shows arrangement 0's catalogue, switches to arrangement 1 (as many shapes, none shared) and hands over both states.</summary>
    private static Task WithSwitchedCatalogue(Action<ShapesPanelView, ShapesSwitchRig, (List<Border> Cards, List<ShapeCardStrip> Strips, int Built)> check) =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var rig = new ShapesSwitchRig();
            var view = new ShapesPanelView { DataContext = rig.Screen };
            var window = NarrowWidthLayout.NewWindow(600, 524, view);
            window.Show();
            ShapesPanelCards.Settle(view);
            try
            {
                var before = (ShapesPanelCards.Realized(view).ToList(), view.RowsList.Items.Cast<ShapeCardStrip>().ToList(), Built(view));
                Assert.NotEmpty(before.Item1);
                var rows = rig.Screen.Rows;

                rig.Chart.State.ActiveArrangement = rig.Chart.Project.Arrangements[1];
                rig.Dispatcher.Drain();
                ShapesPanelCards.Settle(view);

                Assert.NotSame(rows, rig.Screen.Rows);
                Assert.Same(rig.Chart.Project.Arrangements[1].ChordTemplates[0], rig.Screen.AllRows[0].Template);
                check(view, rig, before);
            }
            finally { window.Close(); }
        }, TestContext.Current.CancellationToken);

    [Fact]
    public Task RebindingANameBoxCommitsItsOriginalRowAndRecyclingKeepsTicks() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var rig = new ShapesSwitchRig();
            var view = new ShapesPanelView { DataContext = rig.Screen };
            var window = NarrowWidthLayout.NewWindow(600, 524, view);
            window.Show();
            ShapesPanelCards.Settle(view);
            try
            {
                // The Shape card's name box follows the current shape: a box rebound mid-edit commits to the
                // shape it was typed for, never to the one it now shows.
                var first = rig.Screen.AllRows[0];
                var last = rig.Screen.Rows[^1];
                var lastName = last.NameText;
                rig.Screen.CurrentRow = first;
                view.Cards.ShowPage(view.FindControl<Control>("ShapesShapeCard")!);
                ShapesPanelCards.Settle(view);
                var box = view.FindControl<TextBox>("ShapeName")!;
                Assert.True(box.Focus());
                box.Text = "Revised shape";
                rig.Screen.CurrentRow = last;
                ShapesPanelCards.Settle(view);
                Assert.Same(last, box.DataContext);
                view.FindControl<TextBox>("ShapesSearch")!.Focus();
                ShapesPanelCards.Settle(view);

                Assert.Equal("Revised shape", first.Template.Name);
                Assert.Equal(lastName, last.NameText);
                var card = ShapesPanelCards.Card(view, first);
                first.IsSelected = true;
                ShapesPanelCards.Card(view, last);
                var returned = ShapesPanelCards.Card(view, first);

                Assert.True(returned.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == "ShapeTick").IsChecked);
                Assert.Contains("Record", returned.Classes);
            }
            finally { window.Close(); }
        }, TestContext.Current.CancellationToken);
}
