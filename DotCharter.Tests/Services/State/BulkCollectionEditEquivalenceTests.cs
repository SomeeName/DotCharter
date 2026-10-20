using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace DotCharter.Tests.Services.State;

/// <summary>
///     <see cref="AddElementsAction{T}" /> and <see cref="RemoveElementsAction{T}" />
///     stopped spelling their bulk halves as a per-item loop in v0.60 —
///     <c>Collection.Remove</c> hid an <c>IndexOf</c> scan per item and
///     <c>InsertSorted</c> walked from index 0 per item, so deleting 5,000 of
///     18,000 sounds and undoing it measured a 989 ms median (7x the 150 ms
///     responsiveness budget) purely in list bookkeeping.
///     <para>
///         The replacement is only allowed to be FASTER, so this class pins it
///         against the literal loop it replaced: same resulting sequence by
///         REFERENCE, and the same <c>CollectionChanged</c> script — action, item
///         and index, in order. The reference implementations below are verbatim
///         copies of the old bodies; if one of them is ever "tidied" to match the
///         new code the test goes vacuous, so keep them dumb.
///     </para>
///     <para>
///         The awkward inputs are the point: below the bulk threshold, an item the
///         collection never held, the same item named twice, and a collection that
///         is NOT ordered by position (the fast path's precondition). Each of those
///         must reproduce the loop exactly, including its oddities.
///     </para>
/// </summary>
public sealed class BulkCollectionEditEquivalenceTests
{
    private sealed record Event(NotifyCollectionChangedAction Action, object? Item, int Index);

    private static List<Event> Record(ObservableCollection<GuitarNote> collection)
    {
        var log = new List<Event>();
        collection.CollectionChanged += (_, e) => log.Add(new Event(
            e.Action,
            e.NewItems?.Count > 0 ? e.NewItems[0] : e.OldItems?.Count > 0 ? e.OldItems[0] : null,
            e.NewStartingIndex >= 0 ? e.NewStartingIndex : e.OldStartingIndex));
        return log;
    }

    /// <summary>The old <c>RemoveElementsAction.Execute</c> body, verbatim.</summary>
    private static void ReferenceRemove(ObservableCollection<GuitarNote> collection, List<GuitarNote> items)
    {
        foreach (var item in items) collection.Remove(item);
    }

    /// <summary>The old <c>InsertSorted</c> loop, verbatim.</summary>
    private static void ReferenceInsertSorted(ObservableCollection<GuitarNote> collection, List<GuitarNote> items)
    {
        foreach (var item in items)
        {
            var index = 0;
            while (index < collection.Count && collection[index].Position < item.Position) index++;
            collection.Insert(index, item);
        }
    }

    /// <summary>
    ///     A pool of notes at ascending positions, several sharing a position so the
    ///     equal-position runs a chord produces are covered.
    /// </summary>
    private static List<GuitarNote> Pool(int count, Random rng)
    {
        var notes = new List<GuitarNote>(count);
        var beat = 0;
        while (notes.Count < count)
        {
            var atThisPosition = Math.Min(count - notes.Count, 1 + rng.Next(3));
            for (var s = 0; s < atThisPosition; s++)
            {
                notes.Add(new GuitarNote
                {
                    Position = new FractionalPosition(beat, new Fraction(rng.Next(4), 4)),
                    StringIndex = s,
                    Fret = notes.Count % 22
                });
            }

            beat++;
        }

        // The action's own precondition: ordered by position.
        notes.Sort((a, b) => a.Position.CompareTo(b.Position));
        return notes;
    }

    private static (ObservableCollection<GuitarNote> Fast, ObservableCollection<GuitarNote> Reference)
        TwoCopies(IEnumerable<GuitarNote> seed)
        => (new ObservableCollection<GuitarNote>(seed), new ObservableCollection<GuitarNote>(seed));

    private static void AssertSameOutcome(
        ObservableCollection<GuitarNote> fast, List<Event> fastLog,
        ObservableCollection<GuitarNote> reference, List<Event> referenceLog,
        string what)
    {
        Assert.Equal(reference.Count, fast.Count);
        for (var i = 0; i < reference.Count; i++)
        {
            Assert.True(ReferenceEquals(reference[i], fast[i]),
                $"{what}: element {i} differs from the loop's result.");
        }

        Assert.Equal(referenceLog.Count, fastLog.Count);
        for (var i = 0; i < referenceLog.Count; i++)
        {
            Assert.Equal(referenceLog[i].Action, fastLog[i].Action);
            Assert.Equal(referenceLog[i].Index, fastLog[i].Index);
            Assert.True(ReferenceEquals(referenceLog[i].Item, fastLog[i].Item),
                $"{what}: notification {i} names a different element than the loop's.");
        }
    }

    [Theory]
    // Below the bulk threshold — must take the loop, and match it anyway.
    [InlineData(200, 4, 7)]
    // The shapes a real bulk delete takes: a scattered marquee and a contiguous run.
    [InlineData(2_000, 500, 11)]
    [InlineData(2_000, 1_900, 13)]
    [InlineData(600, 600, 17)]
    public void BulkRemove_matchesThePerItemLoop_elementForElementAndEventForEvent(
        int size, int removeCount, int seed)
    {
        var rng = new Random(seed);
        var pool = Pool(size, rng);
        var victims = pool.OrderBy(_ => rng.Next()).Take(removeCount).ToList();

        var (fast, reference) = TwoCopies(pool);
        var fastLog = Record(fast);
        var referenceLog = Record(reference);

        new RemoveNotesAction(fast, victims).Execute();
        ReferenceRemove(reference, victims);

        AssertSameOutcome(fast, fastLog, reference, referenceLog, $"remove {removeCount} of {size}");
    }

    [Theory]
    [InlineData(2_000, 500, 23)]
    [InlineData(600, 600, 29)]
    public void BulkInsert_matchesThePerItemLoop_elementForElementAndEventForEvent(
        int size, int insertCount, int seed)
    {
        var rng = new Random(seed);
        var pool = Pool(size, rng);
        var incoming = pool.OrderBy(_ => rng.Next()).Take(insertCount).ToList();
        var remaining = pool.Where(n => !incoming.Contains(n)).ToList();

        var (fast, reference) = TwoCopies(remaining);
        var fastLog = Record(fast);
        var referenceLog = Record(reference);

        // Undo of a remove is the insert half; drive it through the real action.
        var action = new RemoveNotesAction(fast, incoming);
        action.Undo();
        ReferenceInsertSorted(reference, incoming);

        AssertSameOutcome(fast, fastLog, reference, referenceLog, $"insert {insertCount} into {remaining.Count}");
    }

    /// <summary>
    ///     An item the collection does not hold: <c>Remove</c> returned false and
    ///     raised nothing, and the fast path must not invent an index for it.
    /// </summary>
    [Fact]
    public void Removing_anItemTheCollectionNeverHeld_isTheSameNoOpAsTheLoop()
    {
        var rng = new Random(31);
        var pool = Pool(500, rng);
        var victims = pool.Take(100).ToList();
        victims.Add(new GuitarNote { Position = new FractionalPosition(9_999), StringIndex = 0, Fret = 3 });

        var (fast, reference) = TwoCopies(pool);
        var fastLog = Record(fast);
        var referenceLog = Record(reference);

        new RemoveNotesAction(fast, victims).Execute();
        ReferenceRemove(reference, victims);

        AssertSameOutcome(fast, fastLog, reference, referenceLog, "remove with a stranger in the batch");
    }

    /// <summary>
    ///     The same element named twice. The loop removed it once and then found
    ///     nothing the second time; the fast path must not resolve two slots for it.
    /// </summary>
    [Fact]
    public void Removing_theSameItemTwice_isTheSameAsTheLoop()
    {
        var rng = new Random(37);
        var pool = Pool(500, rng);
        var victims = pool.Take(40).ToList();
        victims.Add(victims[0]);
        victims.Add(victims[7]);

        var (fast, reference) = TwoCopies(pool);
        var fastLog = Record(fast);
        var referenceLog = Record(reference);

        new RemoveNotesAction(fast, victims).Execute();
        ReferenceRemove(reference, victims);

        AssertSameOutcome(fast, fastLog, reference, referenceLog, "remove with a repeated element");
    }

    /// <summary>
    ///     The binary insertion's ONE precondition. An out-of-order collection is
    ///     not a chart the app should ever hold, but the walk it replaces produced a
    ///     specific answer on one and that answer is what has to survive.
    /// </summary>
    [Fact]
    public void Inserting_intoAnUnorderedCollection_stillMatchesTheWalk()
    {
        var rng = new Random(41);
        var pool = Pool(400, rng);
        var incoming = pool.Take(60).ToList();
        var rest = pool.Skip(60).ToList();
        // Deliberately shuffled: the fast path must detect this and stand down.
        var scrambled = rest.OrderBy(_ => rng.Next()).ToList();

        var (fast, reference) = TwoCopies(scrambled);
        var fastLog = Record(fast);
        var referenceLog = Record(reference);

        new RemoveNotesAction(fast, incoming).Undo();
        ReferenceInsertSorted(reference, incoming);

        AssertSameOutcome(fast, fastLog, reference, referenceLog, "insert into an unordered collection");
    }

    /// <summary>
    ///     Execute-then-Undo is the Ctrl+Z round trip.
    ///     <para>
    ///         It does NOT restore the exact original sequence, and never did: a
    ///         sound removed from the middle of an equal-position run comes back at
    ///         the FRONT of that run, because both the walk and the binary search
    ///         insert at the lower bound. That is pre-existing and invisible (the
    ///         renderers key off position, not slot) — what this pins is that the
    ///         new path lands on the SAME sequence the loop did, and that the chart
    ///         comes back whole and ordered.
    ///     </para>
    /// </summary>
    [Theory]
    [InlineData(2_000, 700, 43)]
    [InlineData(300, 20, 47)]
    public void DeleteThenUndo_matchesTheLoopsRoundTrip(int size, int removeCount, int seed)
    {
        var rng = new Random(seed);
        var pool = Pool(size, rng);
        var victims = pool.OrderBy(_ => rng.Next()).Take(removeCount).ToList();

        var (fast, reference) = TwoCopies(pool);
        var action = new RemoveNotesAction(fast, victims);
        action.Execute();
        Assert.Equal(pool.Count - removeCount, fast.Count);
        action.Undo();

        ReferenceRemove(reference, victims);
        ReferenceInsertSorted(reference, victims);

        Assert.Equal(pool.Count, fast.Count);
        for (var i = 0; i < reference.Count; i++)
        {
            Assert.True(ReferenceEquals(reference[i], fast[i]),
                $"round-trip element {i} differs from the loop's round trip.");
        }

        // …and the chart is whole and still ordered, which is the property the
        // rest of the app actually depends on.
        Assert.Equal(pool.Count, fast.Distinct().Count());
        Assert.All(pool, n => Assert.Contains(n, fast));
        for (var i = 1; i < fast.Count; i++)
            Assert.False(fast[i].Position < fast[i - 1].Position, $"slot {i} broke the position order.");
    }
}
