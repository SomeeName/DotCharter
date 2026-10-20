using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace DotCharter.Core.Utilities;

/// <summary>
///     An <see cref="ObservableCollection{T}" /> that can replace its whole contents
///     in ONE notification instead of one per item.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>
    ///     Clearing an ALREADY-EMPTY collection raises nothing.
    ///     <para>
    ///         <see cref="ObservableCollection{T}.ClearItems" /> raises
    ///         <c>Reset</c> plus two <c>PropertyChanged</c> unconditionally, and
    ///         a <c>Reset</c> means "the contents changed, re-read" — which is
    ///         false when they did not. It is the same no-change early-out
    ///         <see cref="ResetWith" /> already documents, on the path a
    ///         defensive <c>Clear()</c> takes.
    ///     </para>
    ///     <para>
    ///         The cost is a FAN-OUT, not a copy. Every one of the selection
    ///         service's six collections raises its coarse
    ///         <c>SelectionChanged</c> from its own <c>CollectionChanged</c>,
    ///         so an empty clear runs the whole subscriber set — the inspector's
    ///         two O(selection) syncs among them — for a selection that did not
    ///         move. `SelectionService.ClearSelections` clears all six
    ///         unconditionally (six fan-outs, of which five are typically
    ///         no-ops), and `HighwayInteractionTool`'s plain-click path clears
    ///         four more before it selects the hit sound — hand shapes and
    ///         anchors are empty on an ordinary note click, so two of those four
    ///         were always pure notification.
    ///     </para>
    /// </summary>
    protected override void ClearItems()
    {
        if (Items.Count == 0) return;
        base.ClearItems();
    }

    /// <summary>
    ///     Replaces every item, raising a single <see cref="NotifyCollectionChangedAction.Reset" />.
    ///     <para>
    ///         The collection is mutated FULLY before anything is raised. The previous
    ///         shape cleared, raised <c>Remove</c>, and only then repopulated — so a
    ///         handler that threw on <c>Remove</c> left the collection permanently
    ///         EMPTY (a visible data-loss bug for the selection service and the song
    ///         browser), and in between the two events <c>Items</c> was observably empty
    ///         to anything that read <c>Count</c> or re-entered. <c>CheckReentrancy</c>
    ///         guards only the entry, so that window was unprotected.
    ///     </para>
    ///     <para>
    ///         One <c>Reset</c> rather than <c>Remove</c>-then-<c>Add</c>: every consumer
    ///         in this repo ignores the event args and re-reads the collection, the
    ///         two-phase pair made each of them run TWICE per replace, and range
    ///         <c>Remove</c>/<c>Add</c> args throw in
    ///         <c>DataGridCollectionView</c>-style wrappers.
    ///     </para>
    ///     <para>
    ///         The no-change early-out compares with the default equality comparer.
    ///         Every <c>T</c> used here today is a reference type that does not override
    ///         <c>Equals</c>, so that is reference identity in practice — but a
    ///         <c>record</c> would make two value-equal, distinct instances suppress the
    ///         update and leave bindings pointing at the stale ones.
    ///     </para>
    /// </summary>
    public void ResetWith(IEnumerable<T> items)
    {
        // Not IReadOnlyList: this collection is one, so ResetWith(this) would alias
        // the backing store and Clear would empty the source mid-copy.
        var newItems = items as List<T> ?? items.ToList();

        CheckReentrancy();

        if (Items.Count == newItems.Count && Items.SequenceEqual(newItems))
            return;

        Items.Clear();
        foreach (var item in newItems)
            Items.Add(item);

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <summary>
    ///     How many single-item edits <see cref="SyncWith" /> will apply before
    ///     falling back to one <c>Reset</c>. Each edit is one
    ///     <c>CollectionChanged</c> the consumer processes; past a point a burst
    ///     of them costs more than the Reset it replaces. 128 comfortably covers
    ///     the refine-a-typed-query and toggle-one-chip shapes it exists for.
    /// </summary>
    public const int MaxIncrementalEdits = 128;

    /// <summary>
    ///     Replaces the contents like <see cref="ResetWith" />, but when the new
    ///     list is the old one with a FEW items removed (a query refined) or a few
    ///     inserted (a query relaxed), it applies exactly those edits instead of
    ///     raising <c>Reset</c>.
    ///     <para>
    ///         The point is what a <c>Reset</c> does downstream: a virtualizing
    ///         list drops and re-realizes every visible container — measured at
    ///         104–148 ms of the 126–170 ms a song-library filter pass costs —
    ///         even when the visible rows barely changed. A single-item
    ///         <c>Remove</c>/<c>Insert</c> only touches the containers it names,
    ///         so a pass that trims a handful of rows leaves the rest standing.
    ///     </para>
    ///     <para>
    ///         Only PURE removal and pure insertion diffs are attempted (both
    ///         O(n) two-pointer walks — filtering a list sorted the same way
    ///         produces exactly these shapes). Anything else — a sort change, a
    ///         re-keyed rebuild, more than <see cref="MaxIncrementalEdits" />
    ///         edits — falls back to <see cref="ResetWith" />, which also keeps
    ///         every consumer's Reset path exercised. Single-item notifications
    ///         only: range args throw in <c>DataGridCollectionView</c>-style
    ///         wrappers (see <see cref="ResetWith" />).
    ///     </para>
    /// </summary>
    public void SyncWith(IEnumerable<T> items)
    {
        var newItems = items as List<T> ?? items.ToList();

        CheckReentrancy();

        if (Items.Count == newItems.Count && Items.SequenceEqual(newItems))
            return;

        // From or to EMPTY nothing is standing that an edit script could
        // preserve — one Reset is strictly cheaper than a burst of edits.
        if (Items.Count == 0 || newItems.Count == 0)
        {
            ResetWith(newItems);
            return;
        }

        if (Items.Count > newItems.Count && TrySubsequenceDiff(newItems, Items, out var toRemove)
            && toRemove.Count <= MaxIncrementalEdits)
        {
            // Descending, so each removal leaves the still-pending indices valid.
            for (var i = toRemove.Count - 1; i >= 0; i--)
                RemoveAt(toRemove[i]);
            return;
        }

        if (Items.Count < newItems.Count && TrySubsequenceDiff(Items, newItems, out var toInsert)
            && toInsert.Count <= MaxIncrementalEdits)
        {
            // Ascending: each index is a position in the NEW list, and inserting
            // in order makes the prefix converge on it.
            foreach (var index in toInsert)
                Insert(index, newItems[index]);
            return;
        }

        ResetWith(newItems);
    }

    /// <summary>
    ///     When <paramref name="shorter" /> is a subsequence of
    ///     <paramref name="longer" /> (default equality — reference identity for
    ///     every <c>T</c> used here), yields the indices INTO
    ///     <paramref name="longer" /> of the surplus items, in ascending order.
    ///     False as soon as an item of <paramref name="shorter" /> is missing.
    /// </summary>
    private static bool TrySubsequenceDiff(IList<T> shorter, IList<T> longer, out List<int> surplus)
    {
        surplus = [];
        var comparer = EqualityComparer<T>.Default;
        var s = 0;
        for (var l = 0; l < longer.Count; l++)
        {
            if (s < shorter.Count && comparer.Equals(longer[l], shorter[s])) s++;
            else surplus.Add(l);
        }

        return s == shorter.Count;
    }
}
