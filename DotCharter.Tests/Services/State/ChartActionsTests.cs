using System.Collections.ObjectModel;

namespace DotCharter.Tests.Services.State;

// Pins the round-trip ordering contract of AddElementsAction /
// RemoveElementsAction. These actions are the foundation of the entire
// undo system; if they ever desync, every editor command silently drifts.
public class ChartActionsTests
{
    private sealed record Item(FractionalPosition Pos);

    private static FractionalPosition GetPos(Item i) => i.Pos;

    private static ObservableCollection<Item> Sorted(params Item[] items)
    {
        var coll = new ObservableCollection<Item>();
        foreach (var i in items.OrderBy(x => x.Pos)) coll.Add(i);
        return coll;
    }

    [Fact]
    public void AddElementsAction_InsertsInSortedPosition()
    {
        var coll = Sorted(new Item(new FractionalPosition(0)), new Item(new FractionalPosition(10)));
        var added = new[] { new Item(new FractionalPosition(5)), new Item(new FractionalPosition(2)) };

        var act = new AddElementsAction<Item>(coll, added, GetPos);
        act.Execute();

        // Collection must remain sorted by position regardless of the order
        // items were supplied to the action.
        var positions = coll.Select(i => i.Pos).ToList();
        var sortedPositions = positions.OrderBy(p => p).ToList();
        Assert.Equal(sortedPositions, positions);
    }

    [Fact]
    public void AddElementsAction_UndoRestoresOriginalCollection()
    {
        var original = new[] { new Item(new FractionalPosition(0)), new Item(new FractionalPosition(10)) };
        var coll = Sorted(original);

        var added = new[] { new Item(new FractionalPosition(5)) };
        var act = new AddElementsAction<Item>(coll, added, GetPos);
        act.Execute();
        Assert.Equal(3, coll.Count);

        act.Undo();

        // Undo by reference removal preserves the sorted ordering of the
        // surviving items; the collection must equal the original snapshot.
        Assert.Equal(original, coll);
    }

    [Fact]
    public void RemoveElementsAction_UndoReinsertsInSortedPosition()
    {
        var a = new Item(new FractionalPosition(0));
        var b = new Item(new FractionalPosition(5));
        var c = new Item(new FractionalPosition(10));
        var coll = Sorted(a, b, c);

        var act = new RemoveElementsAction<Item>(coll, new[] { b }, GetPos);
        act.Execute();
        Assert.Equal(new[] { a, c }, coll);

        act.Undo();

        Assert.Equal(new[] { a, b, c }, coll);
    }

    [Fact]
    public void AddRemove_RoundTrip_LeavesCollectionUnchangedAcrossManyCycles()
    {
        var coll = Sorted(
            new Item(new FractionalPosition(0)),
            new Item(new FractionalPosition(4)),
            new Item(new FractionalPosition(8)));
        var snapshot = coll.ToList();

        var add = new AddElementsAction<Item>(coll,
            new[] { new Item(new FractionalPosition(2)), new Item(new FractionalPosition(6)) }, GetPos);
        var remove = new RemoveElementsAction<Item>(coll, new[] { coll[0] }, GetPos);

        // Execute both, then undo both in reverse — classic composite pattern.
        add.Execute();
        remove.Execute();
        remove.Undo();
        add.Undo();

        Assert.Equal(snapshot, coll);
    }

    private static readonly string[] Expected = ["+a", "+b", "+c", "-c", "-b", "-a"];

    [Fact]
    public void CompositeAction_UndoRunsInReverseOrder()
    {
        var trace = new List<string>();
        var actions = new IChartAction[]
        {
            new TraceAction("a", trace),
            new TraceAction("b", trace),
            new TraceAction("c", trace)
        };
        var composite = new CompositeAction(actions);

        composite.Execute();
        composite.Undo();

        Assert.Equal(Expected, trace);
    }

    private sealed class TraceAction(string name, List<string> trace) : IChartAction
    {
        public bool HasEffect => true;
        public void Execute() => trace.Add("+" + name);
        public void Undo() => trace.Add("-" + name);
    }
}
