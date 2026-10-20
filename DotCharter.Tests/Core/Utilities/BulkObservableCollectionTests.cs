using System.Collections.Specialized;
using System.ComponentModel;

namespace DotCharter.Tests.Core.Utilities;

public class BulkObservableCollectionTests
{
    [Fact]
    public void ResetWith_EmptyToItems_RaisesOneResetEvent()
    {
        var coll = new BulkObservableCollection<int>();
        var events = new List<NotifyCollectionChangedAction>();
        coll.CollectionChanged += (_, e) => events.Add(e.Action);

        coll.ResetWith(new[] { 1, 2, 3 });

        Assert.Equal(new[] { 1, 2, 3 }, coll);
        Assert.Single(events);
        Assert.Equal(NotifyCollectionChangedAction.Reset, events[0]);
    }

    [Fact]
    public void ResetWith_ReplaceItems_RaisesOneResetEvent()
    {
        // Deliberately ONE event, not Remove-then-Add: every consumer re-reads the
        // collection and ignored the args, so the pair made each of them run twice.
        var coll = new BulkObservableCollection<int> { 1, 2, 3 };
        var events = new List<NotifyCollectionChangedAction>();
        coll.CollectionChanged += (_, e) => events.Add(e.Action);

        coll.ResetWith(new[] { 7, 8 });

        Assert.Equal(new[] { 7, 8 }, coll);
        Assert.Equal(new[] { NotifyCollectionChangedAction.Reset }, events);
    }

    [Fact]
    public void ResetWith_IdenticalContents_IsNoOp()
    {
        var coll = new BulkObservableCollection<int> { 1, 2, 3 };
        var changes = 0;
        coll.CollectionChanged += (_, _) => changes++;

        coll.ResetWith(new[] { 1, 2, 3 });

        Assert.Equal(0, changes);
    }

    [Fact]
    public void ResetWith_ToEmpty_ClearsAndRaisesOneResetEvent()
    {
        var coll = new BulkObservableCollection<int> { 1, 2, 3 };
        var events = new List<NotifyCollectionChangedAction>();
        coll.CollectionChanged += (_, e) => events.Add(e.Action);

        coll.ResetWith(Array.Empty<int>());

        Assert.Empty(coll);
        Assert.Equal(new[] { NotifyCollectionChangedAction.Reset }, events);
    }

    [Fact]
    public void ResetWith_RaisesCountPropertyChanged()
    {
        var coll = new BulkObservableCollection<int>();
        var propertyChanges = new List<string?>();
        ((INotifyPropertyChanged)coll).PropertyChanged +=
            (_, e) => propertyChanges.Add(e.PropertyName);

        coll.ResetWith(new[] { 1, 2, 3 });

        Assert.Contains("Count", propertyChanges);
        Assert.Contains("Item[]", propertyChanges);
    }

    [Fact]
    public void ResetWith_ListenerThatThrows_DoesNotLeaveTheCollectionEmpty()
    {
        // The old shape cleared, raised Remove, and only THEN repopulated, so a
        // throwing handler aborted before the new items were ever added and the
        // collection was silently emptied.
        var coll = new BulkObservableCollection<int> { 1, 2, 3 };
        coll.CollectionChanged += (_, _) => throw new InvalidOperationException("boom");

        Assert.Throws<InvalidOperationException>(() => coll.ResetWith(new[] { 7, 8, 9 }));

        Assert.Equal(new[] { 7, 8, 9 }, coll);
    }

    [Fact]
    public void ResetWith_IsNeverObservablyEmptyDuringNotification()
    {
        // Between the old Remove and Add events the collection was empty and
        // reentrancy was NOT blocked, so anything reading Count mid-notification
        // saw zero items.
        var coll = new BulkObservableCollection<int> { 1, 2, 3 };
        var countsSeen = new List<int>();
        coll.CollectionChanged += (_, _) => countsSeen.Add(coll.Count);

        coll.ResetWith(new[] { 7, 8 });

        Assert.Equal(new[] { 2 }, countsSeen);
    }

    [Fact]
    public void ResetWith_MutatingTheSourceListAfterwards_DoesNotChangeTheCollection()
    {
        // The caller's List<T> instance used to be kept and handed straight to the
        // Add event args, so a later mutation retroactively changed what was
        // reported.
        var coll = new BulkObservableCollection<int>();
        var source = new List<int> { 1, 2 };

        coll.ResetWith(source);
        source.Add(3);

        Assert.Equal(new[] { 1, 2 }, coll);
    }

    [Fact]
    public void ResetWith_WithItself_KeepsTheContents()
    {
        // This collection is an IReadOnlyList<T>, so treating the argument as one
        // would alias the backing store and Clear would empty the source mid-copy.
        var coll = new BulkObservableCollection<int> { 1, 2, 3 };

        coll.ResetWith(coll);

        Assert.Equal(new[] { 1, 2, 3 }, coll);
    }

    // ── SyncWith: the differential republish ─────────────────────────────────

    [Fact]
    public void SyncWith_RemovalOnlyDiff_RaisesOneRemovePerSurplusItem_AndNoReset()
    {
        // The refine-a-typed-query shape: same order, fewer rows. A Reset makes a
        // virtualizing list re-realize every visible container; per-item Removes
        // only touch the rows that actually left.
        var coll = new BulkObservableCollection<int> { 1, 2, 3, 4, 5, 6 };
        var events = new List<NotifyCollectionChangedAction>();
        coll.CollectionChanged += (_, e) => events.Add(e.Action);

        coll.SyncWith(new[] { 1, 3, 6 });

        Assert.Equal(new[] { 1, 3, 6 }, coll);
        Assert.Equal([
            NotifyCollectionChangedAction.Remove,
            NotifyCollectionChangedAction.Remove,
            NotifyCollectionChangedAction.Remove
        ], events);
    }

    [Fact]
    public void SyncWith_InsertionOnlyDiff_RaisesOneAddPerNewItem_AtTheRightIndex()
    {
        // The relax-a-query shape (backspace): same order, a few rows return.
        var coll = new BulkObservableCollection<int> { 1, 3, 6 };
        var events = new List<(NotifyCollectionChangedAction Action, int Index)>();
        coll.CollectionChanged += (_, e) => events.Add((e.Action, e.NewStartingIndex));

        coll.SyncWith(new[] { 1, 2, 3, 4, 5, 6 });

        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6 }, coll);
        Assert.All(events, e => Assert.Equal(NotifyCollectionChangedAction.Add, e.Action));
        Assert.Equal([1, 3, 4], events.Select(e => e.Index));
    }

    [Fact]
    public void SyncWith_IdenticalContents_IsNoOp()
    {
        var coll = new BulkObservableCollection<int> { 1, 2, 3 };
        var changes = 0;
        coll.CollectionChanged += (_, _) => changes++;

        coll.SyncWith(new[] { 1, 2, 3 });

        Assert.Equal(0, changes);
    }

    [Fact]
    public void SyncWith_ReorderedContents_FallsBackToOneReset()
    {
        // A sort flip is not a removal or an insertion; the diff must refuse it
        // rather than "fix it up" with a storm of edits.
        var coll = new BulkObservableCollection<int> { 1, 2, 3 };
        var events = new List<NotifyCollectionChangedAction>();
        coll.CollectionChanged += (_, e) => events.Add(e.Action);

        coll.SyncWith(new[] { 3, 2, 1 });

        Assert.Equal(new[] { 3, 2, 1 }, coll);
        Assert.Equal([NotifyCollectionChangedAction.Reset], events);
    }

    [Fact]
    public void SyncWith_MixedDiff_FallsBackToOneReset()
    {
        // Same length, one item swapped: neither a pure removal nor a pure
        // insertion, even though counts alone cannot tell.
        var coll = new BulkObservableCollection<int> { 1, 2, 3, 9 };
        var events = new List<NotifyCollectionChangedAction>();
        coll.CollectionChanged += (_, e) => events.Add(e.Action);

        coll.SyncWith(new[] { 1, 2, 3, 4, 5 });

        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, coll);
        Assert.Equal([NotifyCollectionChangedAction.Reset], events);
    }

    [Fact]
    public void SyncWith_MoreEditsThanTheCap_FallsBackToOneReset()
    {
        // Past the cap a burst of single-item events costs the consumer more
        // than the Reset it replaces — and the big narrow (2,160 → 15) is
        // exactly that shape.
        var coll = new BulkObservableCollection<int>();
        coll.ResetWith(Enumerable.Range(0, BulkObservableCollection<int>.MaxIncrementalEdits + 50));
        var events = new List<NotifyCollectionChangedAction>();
        coll.CollectionChanged += (_, e) => events.Add(e.Action);

        coll.SyncWith(new[] { 0, 1, 2 });

        Assert.Equal(new[] { 0, 1, 2 }, coll);
        Assert.Equal([NotifyCollectionChangedAction.Reset], events);
    }

    [Fact]
    public void SyncWith_ExactlyTheCap_StaysIncremental()
    {
        var cap = BulkObservableCollection<int>.MaxIncrementalEdits;
        var coll = new BulkObservableCollection<int>();
        coll.ResetWith(Enumerable.Range(0, cap + 3));
        var events = new List<NotifyCollectionChangedAction>();
        coll.CollectionChanged += (_, e) => events.Add(e.Action);

        coll.SyncWith(new[] { 0, 1, 2 });

        Assert.Equal(new[] { 0, 1, 2 }, coll);
        Assert.Equal(cap, events.Count);
        Assert.All(events, e => Assert.Equal(NotifyCollectionChangedAction.Remove, e));
    }

    [Fact]
    public void SyncWith_FromAndToEmpty_IsOneResetEach()
    {
        // From or to empty, nothing is standing that an edit script could
        // preserve — a scan completing into an empty list must not pay one Add
        // per song.
        var coll = new BulkObservableCollection<int>();
        var events = new List<NotifyCollectionChangedAction>();
        coll.CollectionChanged += (_, e) => events.Add(e.Action);

        coll.SyncWith(new[] { 1, 2 });                 // 0 → 2
        coll.SyncWith(Array.Empty<int>());             // 2 → 0

        Assert.Empty(coll);
        Assert.Equal([NotifyCollectionChangedAction.Reset, NotifyCollectionChangedAction.Reset], events);
    }

    [Fact]
    public void SyncWith_WithItself_KeepsTheContents()
    {
        var coll = new BulkObservableCollection<int> { 1, 2, 3 };

        coll.SyncWith(coll);

        Assert.Equal(new[] { 1, 2, 3 }, coll);
    }

    [Theory]
    [InlineData(new[] { 1, 2, 3, 4, 5 }, new[] { 2, 4 })]
    [InlineData(new[] { 2, 4 }, new[] { 1, 2, 3, 4, 5 })]
    [InlineData(new[] { 1, 2 }, new[] { 3, 4 })]
    [InlineData(new int[0], new[] { 1 })]
    [InlineData(new[] { 1 }, new int[0])]
    public void SyncWith_AlwaysEndsWithExactlyTheNewContents(int[] before, int[] after)
    {
        var coll = new BulkObservableCollection<int>();
        coll.ResetWith(before);

        coll.SyncWith(after);

        Assert.Equal(after, coll);
    }

    // ---- the empty Clear ----------------------------------------------------

    /// <summary>
    ///     A <c>Clear()</c> that removes nothing raises nothing. The base
    ///     `ObservableCollection` raises `Reset` plus two `PropertyChanged`
    ///     unconditionally, and every defensive `Clear()` on the selection
    ///     service's collections therefore ran the whole `SelectionChanged`
    ///     subscriber set for a selection that did not move.
    /// </summary>
    [Fact]
    public void Clear_OnAnEmptyCollection_RaisesNothing()
    {
        var coll = new BulkObservableCollection<int>();
        var collectionEvents = 0;
        var propertyEvents = new List<string?>();
        coll.CollectionChanged += (_, _) => collectionEvents++;
        ((INotifyPropertyChanged)coll).PropertyChanged += (_, e) => propertyEvents.Add(e.PropertyName);

        coll.Clear();
        coll.Clear();

        Assert.Equal(0, collectionEvents);
        Assert.Empty(propertyEvents);
        Assert.Empty(coll);
    }

    /// <summary>
    ///     …and a clear that really removes something still raises exactly one
    ///     `Reset` plus `Count` and `Item[]`, so the early-out cannot be
    ///     mistaken for "Clear stopped notifying".
    /// </summary>
    [Fact]
    public void Clear_OnANonEmptyCollection_StillRaisesOneReset()
    {
        var coll = new BulkObservableCollection<int> { 1, 2, 3 };
        var actions = new List<NotifyCollectionChangedAction>();
        var propertyEvents = new List<string?>();
        coll.CollectionChanged += (_, e) => actions.Add(e.Action);
        ((INotifyPropertyChanged)coll).PropertyChanged += (_, e) => propertyEvents.Add(e.PropertyName);

        coll.Clear();

        Assert.Equal([NotifyCollectionChangedAction.Reset], actions);
        Assert.Equal(["Count", "Item[]"], propertyEvents);
        Assert.Empty(coll);

        // …and the SECOND clear, now that it is empty, is silent.
        actions.Clear();
        coll.Clear();
        Assert.Empty(actions);
    }
}
