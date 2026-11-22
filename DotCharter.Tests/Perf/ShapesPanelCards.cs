using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Presentation.Contracts.Editor;
using DotCharter.UI.Controls;
using DotCharter.UI.Views.Editor;

namespace DotCharter.Tests.Perf;

internal static class ShapesPanelCards
{
    /// <summary>The cards on show. A strip container waiting in the list's recycle pool keeps its card visuals: those do not count.</summary>
    internal static IEnumerable<Border> Realized(Control view) =>
        view.GetVisualDescendants().OfType<Border>()
            .Where(b => b.Name == "ShapeCard" && b.DataContext is IShapePanelRow && b.IsEffectivelyVisible);

    internal static Border Card(ShapesPanelView view, IShapePanelRow row)
    {
        Settle(view);
        var list = view.RowsList;
        var strips = list.Items.Cast<ShapeCardStrip>().ToList();
        var index = strips.FindIndex(s => s.Cards.Any(r => ReferenceEquals(r, row)));
        Assert.True(index >= 0, "the row is absent from the Shapes catalogue");
        list.ScrollIntoView(index);
        Settle(view);
        var card = Realized(view).Single(c => ReferenceEquals(c.DataContext, row));
        card.BringIntoView(new Rect(0, 0, card.Bounds.Width, Math.Min(card.Bounds.Height, view.Scroll.Viewport.Height)));
        Settle(view);
        return Realized(view).Single(c => ReferenceEquals(c.DataContext, row));
    }

    internal static void ForEach(ShapesPanelView view, Action<Border> check)
    {
        var offset = view.Scroll.Offset;
        foreach (var row in view.RowsList.SourceRows?.ToArray() ?? []) check(Card(view, row));
        view.Scroll.Offset = offset;
        Settle(view);
    }

    internal static void Settle(ShapesPanelView view)
    {
        var window = Assert.IsType<Window>(TopLevel.GetTopLevel(view));
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
            window.UpdateLayout();
        }
    }
}
