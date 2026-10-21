using System.Collections.ObjectModel;

namespace DotCharter.Services.State;

public class CompositeAction(IEnumerable<IChartAction> actions) : IChartAction, IMergeableAction, ILinkPolicyAction
{
    private readonly List<IChartAction> _actions = actions.Where(a => a.HasEffect).ToList();

    /// <summary>
    ///     What the ONE deferred post-pass changed outside every child's owned
    ///     set (<see cref="DeferredPostPassScope" />). The children hold none
    ///     when they ran inside this composite's scope; this is restored LAST on
    ///     undo, after the single pass, and merged on a gesture absorb exactly as
    ///     <c>CollectionActionBase.AbsorbCollateral</c> merges a child's.
    /// </summary>
    private SpacingCollateral? _collateral;

    /// <summary>The effective wrapped actions — read-only; the history uses
    /// this to describe the composite by its content ("Add Chords") instead
    /// of the meaningless type name "Composite".</summary>
    public IReadOnlyList<IChartAction> Actions => _actions;

    /// <summary>
    ///     The resx key the history shows for the WHOLE gesture ("History_Paste"),
    /// set by the code that built it. Null: the history names the first
    ///     effective child instead, which is right only when the children are all
    ///     the same kind of edit.
    /// </summary>
    public string? LabelKey { get; init; }

    /// <summary>
    ///     The link policy of the WHOLE composite, set by a tool whose children are ordinary
    ///     edits but whose result is outside truth (an AI pass: <see cref="LinkPolicy.UnlinkOnDivergence" />).
    ///     Null: the children's strictest policy (<see cref="LinkPolicies.Of" />).
    /// </summary>
    public LinkPolicy? LinkPolicyOverride { get; init; }

    public LinkPolicy LinkPolicy => LinkPolicies.Of(this);

    public bool HasEffect => _actions.Count > 0;

    public long EstimatedBytes
    {
        get
        {
            var bytes = 0L;
            foreach (var action in _actions) bytes += action.EstimatedBytes;
            return bytes;
        }
    }

    /// <summary>
    ///     A composite is a gesture only when EVERY child is the same gesture.
    ///     <para>
    ///         This is what makes a mixed selection behave like a plain one:
    ///         adjusting the sustain of a note and a chord together builds a
    ///         two-child composite, and without this the very common
    ///         "select a run, hold the key" case still pushed one node per tick
    ///         while a chord-only selection collapsed to one.
    ///     </para>
    /// </summary>
    public string MergeVerb
    {
        get
        {
            if (_actions.Count == 0) return "";
            if (_actions[0] is not IMergeableAction first || first.MergeVerb.Length == 0) return "";
            for (var i = 1; i < _actions.Count; i++)
            {
                if (_actions[i] is not IMergeableAction m ||
                    !string.Equals(m.MergeVerb, first.MergeVerb, StringComparison.Ordinal))
                {
                    return "";
                }
            }

            return first.MergeVerb;
        }
    }

    /// <summary>
    ///     All-or-nothing: every child must be able to absorb its counterpart,
    ///     checked in full BEFORE any of them commits (see
    ///     <see cref="IMergeableAction.CanAbsorb" />).
    /// </summary>
    public bool CanAbsorb(IChartAction next)
    {
        if (next is not CompositeAction other) return false;
        if (other._actions.Count != _actions.Count) return false;

        for (var i = 0; i < _actions.Count; i++)
        {
            if (_actions[i] is not IMergeableAction head || !head.CanAbsorb(other._actions[i]))
                return false;
        }

        return true;
    }

    public void Absorb(IChartAction next)
    {
        var other = (CompositeAction)next;
        for (var i = 0; i < _actions.Count; i++)
            ((IMergeableAction)_actions[i]).Absorb(other._actions[i]);
        // The absorbed tick's collateral, keeping OUR snapshot for anything
        // both ticks touched — the AbsorbCollateral rule, one level up.
        _collateral = _collateral == null
            ? other._collateral
            : _collateral.MergeKeepingEarliest(other._collateral);
    }

    /// <summary>
    ///     All-or-nothing, the same contract <see cref="CanAbsorb" /> already has.
    ///     <para>
    ///         A composite is how one gesture edits many sounds at once, so a child
    ///         that throws halfway would leave the chart with the first N edits
    ///         applied and the rest not — and because <c>HistoryService.Do</c>
    ///         pushes the undo node only AFTER <c>Execute</c> returns, there would be no
    ///         node to undo it with, and no baseline change either, so the project
    ///         would read CLEAN (<see cref="ProjectDirtyTracker" /> compares node ids).
    ///         The charter would close the app on the close prompt's word and lose the
    ///         half-edit's worth of work along with the damage.
    ///     </para>
    ///     <para>
    ///         So: remember how far we got and undo exactly those children in
    ///         reverse before rethrowing. An <see cref="Undo" /> that throws in turn
    ///         is not swallowed — it is attached to the original as an
    ///         <see cref="AggregateException" />, because at that point the chart
    ///         state is genuinely unknown and the caller must not be told the
    ///         rollback worked.
    ///     </para>
    /// </summary>
    public void Execute()
    {
        // One spacing pass per GESTURE, not per child: while this scope is
        // open every child's ExecutePostPass defers into it, and the pass runs
        // once below under one collateral recorder. Null when nested — an
        // inner composite defers into the outer one (DeferredPostPassScope).
        var scope = DeferredPostPassScope.BeginIfNone();
        var applied = 0;
        try
        {
            for (; applied < _actions.Count; applied++) _actions[applied].Execute();
            if (scope != null) _collateral = scope.RunOnceRecordingCollateral();
        }
        catch (Exception ex)
        {
            var rollbackFailures = new List<Exception>();
            for (var i = applied - 1; i >= 0; i--)
            {
                try
                {
                    _actions[i].Undo();
                }
                catch (Exception undoEx)
                {
                    rollbackFailures.Add(undoEx);
                }
            }

            // No pass ran (it runs only after every child succeeded), so the
            // rolled-back children have restored the exact pre-edit state and
            // there is nothing to re-enforce.
            if (rollbackFailures.Count > 0)
            {
                throw new AggregateException(
                    "A composite action failed and could not be fully rolled back.",
                    [ex, .. rollbackFailures]);
            }

            throw;
        }
        finally
        {
            scope?.Dispose();
        }
    }

    /// <summary>
    ///     Inverses, then the one collateral record — and NO pass. The children's
    ///     inverses restore everything they own and the record restores everything
    ///     the single execute-time pass changed outside that, so the chart IS the
    ///     pre-edit chart afterwards. The per-child flow re-ran the pass on undo
    ///     and it could only ever re-shorten that restored chart: whatever it took
    ///     that the execute-time record had not happened to capture stayed short
    ///     (<c>HandShapeTailTrimTests.OneUndoTakesTheBarAndTheTrimsBackTogether</c>
    ///     is exactly that shape — a trimmed tail restored to a length the
    ///     enforcer would cap). Skipping it is what makes undo exact.
    /// </summary>
    public void Undo()
    {
        var scope = DeferredPostPassScope.BeginIfNone();
        try
        {
            for (var i = _actions.Count - 1; i >= 0; i--) _actions[i].Undo();
            if (scope == null) return;
            _collateral?.Restore();
            _collateral = null;
        }
        finally
        {
            scope?.Dispose();
        }
    }
}

public abstract class CollectionActionBase<T>(
    ObservableCollection<T> collection,
    Func<T, FractionalPosition> getPosition,
    Action? onPostExecute)
    : IChartAction, ICollectionChartAction, ILinkPolicyAction
    where T : class
{
    /// <summary>Linked measures: an editing gesture, copied to every linked placement.</summary>
    public LinkPolicy LinkPolicy => LinkPolicy.Propagate;

    protected readonly ObservableCollection<T> Collection = collection;
    protected readonly Func<T, FractionalPosition> GetPosition = getPosition;
    protected readonly Action? OnPostExecute = onPostExecute;

    /// <summary>
    ///     What the post-execute spacing pass changed on elements this action does
    ///     NOT own, captured during <c>Execute</c> so <c>Undo</c> can put it back.
    /// </summary>
    private SpacingCollateral? _collateral;

    object ICollectionChartAction.EditedCollection => Collection;
    bool ICollectionChartAction.HasPostPass => OnPostExecute != null;

    public abstract bool HasEffect { get; }
    public abstract void Execute();
    public abstract void Undo();

    /// <summary>
    ///     Runs the post-execute pass (the spacing enforcer) and records the
    ///     collateral edits it made outside <paramref name="owned" />.
    ///     <para>
    ///         The enforcer's non-link branch only ever SHORTENS, so placing a note
    ///         next to a long sustain truncated that NEIGHBOUR — a sound this action
    ///         does not hold — and <c>Undo</c> re-running the same pass could not
    ///         lengthen anything back. The neighbour stayed truncated and repeating
    ///         the gesture eroded the chart.
    ///     </para>
    /// </summary>
    protected void ExecutePostPass(IEnumerable<object> owned)
    {
        if (OnPostExecute == null)
        {
            _collateral = null;
            return;
        }

        // Inside a composite the pass is deferred: the composite runs it once
        // for the whole gesture and holds the collateral (DeferredPostPassScope).
        if (DeferredPostPassScope.Current is { } scope)
        {
            scope.Defer(OnPostExecute, owned);
            _collateral = null;
            return;
        }

        using var recorder = SpacingCollateralRecorder.Begin();
        OnPostExecute();
        _collateral = recorder.Build(owned);
    }

    /// <summary>
    ///     The inverse of <see cref="ExecutePostPass" />: re-runs the pass, then puts
    ///     the recorded collateral back.
    ///     <para>
    ///         Restoring LAST is deliberate. Once the action's own inverse has run,
    ///         the chart is the pre-edit chart, and the recorded values ARE its
    ///         pre-edit values — a state the enforcer had already accepted. Restoring
    ///         before the pass would let the pass re-shorten them.
    ///     </para>
    /// </summary>
    protected void UndoPostPass()
    {
        if (OnPostExecute != null && DeferredPostPassScope.Current is not null)
        {
            // Inside a composite's undo no pass runs at all — the composite's
            // inverses + its one collateral record restore the exact pre-edit
            // chart (see CompositeAction.Undo). A child executed inside a scope
            // holds no collateral of its own; one executed standalone is never
            // undone inside a scope.
            _collateral?.Restore();
            _collateral = null;
            return;
        }

        OnPostExecute?.Invoke();
        _collateral?.Restore();
        _collateral = null;
    }

    /// <summary>
    ///     Takes over a merged-away tick's collateral, keeping OUR snapshot for any
    ///     element both ticks touched.
    ///     <para>
    ///         <b>Any <see cref="IMergeableAction.Absorb" /> on a collection action
    ///         must call this.</b> <c>HistoryService.Do</c> executes the incoming
    ///         action first — which records ITS enforcer collateral on ITS own
    ///         instance — and only then folds it into the head, dropping the
    ///         instance. So tick 1 truncating neighbour A and tick 7 truncating
    ///         neighbour B produced ONE undo node holding A's record only: Ctrl+Z
    ///         restored the dragged element and left B permanently short, with the
    ///         node that could have restored it already gone. Hold `]` on a
    ///         selected sound, or wheel-drag a hand shape, and the chart erodes one
    ///         gesture at a time — exactly what <c>SpacingCollateral</c> exists
    ///         to stop, which gesture merging would otherwise reopen.
    ///     </para>
    /// </summary>
    protected void AbsorbCollateral(CollectionActionBase<T> next)
    {
        _collateral = _collateral == null
            ? next._collateral
            : _collateral.MergeKeepingEarliest(next._collateral);
    }

    protected void InsertSorted(T item)
    {
        var index = 0;
        while (index < Collection.Count && GetPosition(Collection[index]) < GetPosition(item)) index++;
        Collection.Insert(index, item);
    }

    /// <summary>
    ///     Below this many items the index bookkeeping costs more than the scans it
    ///     replaces, so both bulk helpers fall straight through to the loop they
    ///     stand in for. Every ordinary edit — a placed note, a dragged chord, a
    ///     nudged anchor — is one or a handful of items and takes that path.
    /// </summary>
    private const int BulkThreshold = 16;

    /// <summary>
    ///     <c>foreach (var item in items) Collection.Remove(item)</c> — without the
    ///     O(n) <c>IndexOf</c> scan each of those hides.
    ///     <para>
    ///         Notification-for-notification identical to the loop it replaces: the
    ///         same items in the same order, each raising ONE <c>Remove</c> carrying
    ///         the same index. It resolves that index from a map built in one pass
    ///         plus a Fenwick count of what has already gone, so the k-th removal is
    ///         O(log n) instead of O(n).
    ///     </para>
    ///     <para>
    ///         It refuses — before mutating anything — on any input the loop would
    ///         answer differently: an item the collection does not hold, or two
    ///         entries resolving to the same slot (a duplicate). Those fall back to
    ///         the loop, which is also what keeps the fast path free of
    ///         "what if it is not there" special cases.
    ///     </para>
    ///     <para>
    ///         Measured on the stress chart: deleting 5,000 sounds out of 18,000 and
    ///         undoing it alternated at a 989 ms median — nearly 7x the 150 ms
    ///         responsiveness budget for one Ctrl+Z, and the delete half is this
    ///         loop.
    ///     </para>
    /// </summary>
    /// <summary>
    ///     The subset of <paramref name="items" /> the collection currently
    ///     holds, in the given order. Notification-for-notification this is what
    ///     <c>foreach (item) Collection.Remove(item)</c> would touch, because
    ///     <c>Remove</c> of an absent item raises nothing.
    /// </summary>
    protected List<T> Present(List<T> items)
    {
        if (items.Count == 0) return [];
        if (items.Count < BulkThreshold)
            return items.Where(Collection.Contains).ToList();
        var held = new HashSet<T>(Collection, EqualityComparer<T>.Default);
        return items.Where(held.Contains).ToList();
    }

    protected void RemoveEach(List<T> items)
    {
        if (items.Count >= BulkThreshold && Collection.Count >= BulkThreshold && TryRemoveByIndex(items)) return;
        foreach (var item in items) Collection.Remove(item);
    }

    private bool TryRemoveByIndex(List<T> items)
    {
        var n = Collection.Count;
        // Same comparer Collection.Remove used, so "which slot does this item
        // name" is answered identically.
        var slotOf = new Dictionary<T, int>(n, EqualityComparer<T>.Default);
        for (var i = 0; i < n; i++) slotOf.TryAdd(Collection[i], i);

        var targets = new int[items.Count];
        var claimed = new HashSet<int>(items.Count);
        for (var j = 0; j < items.Count; j++)
        {
            if (!slotOf.TryGetValue(items[j], out var slot) || !claimed.Add(slot)) return false;
            targets[j] = slot;
        }

        // Fenwick over the ORIGINAL slots: how many already-removed items sat
        // before this one tells us what its index is now. 1-based internally.
        var gone = new int[n + 1];
        foreach (var slot in targets)
        {
            var before = 0;
            for (var i = slot; i > 0; i -= i & -i) before += gone[i];
            Collection.RemoveAt(slot - before);
            for (var i = slot + 1; i <= n; i += i & -i) gone[i]++;
        }

        return true;
    }

    /// <summary>
    ///     <c>foreach (var item in items) InsertSorted(item)</c> — without
    ///     <see cref="InsertSorted" />'s walk from index 0 per item.
    ///     <para>
    ///         On a collection already ordered by position the linear walk and a
    ///         binary lower-bound land on the SAME index by definition, and a
    ///         lower-bound insertion leaves the collection ordered, so one O(n)
    ///         sortedness check up front licenses every insertion in the batch.
    ///         An out-of-order collection falls back to the walk, whose result on
    ///         such a collection is the thing being preserved.
    ///     </para>
    ///     <para>
    ///         This is the UNDO half of a bulk delete (and the execute half of a
    ///         bulk paste): re-inserting 5,000 sounds walked ~3.75e7 positions,
    ///         each a delegate call and a <c>Fraction</c> comparison.
    ///     </para>
    /// </summary>
    protected void InsertEachSorted(List<T> items)
    {
        if (items.Count >= BulkThreshold && Collection.Count >= BulkThreshold && IsOrderedByPosition())
        {
            foreach (var item in items) InsertSortedBinary(item);
            return;
        }

        foreach (var item in items) InsertSorted(item);
    }

    private bool IsOrderedByPosition()
    {
        for (var i = 1; i < Collection.Count; i++)
        {
            if (GetPosition(Collection[i]) < GetPosition(Collection[i - 1]))
                return false;
        }

        return true;
    }

    private void InsertSortedBinary(T item)
    {
        var target = GetPosition(item);
        int lo = 0, hi = Collection.Count;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (GetPosition(Collection[mid]) < target) lo = mid + 1;
            else hi = mid;
        }

        Collection.Insert(lo, item);
    }

    /// <summary>
    ///     Puts the collection back in position order after a mutation moved
    ///     some of its elements — the STABLE sort of the current sequence, which
    ///     is exactly what the gnome sort this replaces produced (an insertion
    ///     sort is stable), so equal-position runs keep their relative order.
    ///     <para>
    ///         The gnome sort runs on EVERY
    ///         tick of a drag or wheel gesture (<c>HistoryService.Do</c> executes
    ///         before it merges); issuing one <c>Collection.Move</c> — one
    ///         <c>CollectionChanged</c> to every bound view — per SLOT the moved
    ///         element travelled would mean, for one note dragged across a 5,000-note level,
    ///         up to 4,999 notifications per tick, and a bulk reposition would be
    ///         O(n·displacement). So: one O(n) sortedness check (the common
    ///         tick, zero notifications, zero allocation), and on the rare
    ///         unsorted tick one <c>Move</c> per DISPLACED element, straight to
    ///         its final slot. Pinned by <c>MutateElementsActionSortTests</c>.
    ///     </para>
    /// </summary>
    protected void SortCollection()
    {
        // The common tick — nothing moved — must allocate nothing, so the
        // sort's closure lives in its own method: C# hoists a captured
        // variable's display class to the top of the scope that declares it,
        // and that was 24 B per tick here before the split (measured by
        // MutateElementsActionSortTests).
        if (Collection.Count < 2 || IsOrderedByPosition()) return;
        RestoreOrder();
    }

    private void RestoreOrder()
    {
        var n = Collection.Count;
        var positions = new FractionalPosition[n];
        for (var i = 0; i < n; i++) positions[i] = GetPosition(Collection[i]);

        // The stable permutation: by position, ties by original index.
        var order = new int[n];
        for (var i = 0; i < n; i++) order[i] = i;
        Array.Sort(order, (a, b) =>
        {
            var c = positions[a].CompareTo(positions[b]);
            return c != 0 ? c : a.CompareTo(b);
        });

        // rank[i] = where original element i ends up. The elements whose ranks
        // already form the longest increasing subsequence STAY; everything else
        // is displaced and gets exactly one Move — the minimum possible.
        var rank = new int[n];
        for (var t = 0; t < n; t++) rank[order[t]] = t;
        var keep = LongestIncreasingSubsequence(rank);

        // `current` mirrors the collection as original indices. Each displaced
        // element, in final order, is moved to sit right after its predecessor
        // in that order — which keeps everything of lower rank in relative
        // order, so the end state is the sorted sequence.
        var current = new List<int>(n);
        for (var i = 0; i < n; i++) current.Add(i);
        for (var t = 0; t < n; t++)
        {
            var element = order[t];
            if (keep[element]) continue;

            var from = current.IndexOf(element);
            var to = 0;
            if (t > 0)
            {
                var predecessor = current.IndexOf(order[t - 1]);
                to = from < predecessor ? predecessor : predecessor + 1;
            }

            if (from == to) continue;
            Collection.Move(from, to);
            current.RemoveAt(from);
            current.Insert(to, element);
        }
    }

    /// <summary>Marks the members of one longest strictly increasing
    /// subsequence of <paramref name="values" /> (a permutation). Patience
    /// sorting with predecessor links, O(n log n).</summary>
    private static bool[] LongestIncreasingSubsequence(int[] values)
    {
        var n = values.Length;
        var tailIndex = new int[n];
        var previous = new int[n];
        var length = 0;
        for (var i = 0; i < n; i++)
        {
            int lo = 0, hi = length;
            while (lo < hi)
            {
                var mid = lo + (hi - lo) / 2;
                if (values[tailIndex[mid]] < values[i]) lo = mid + 1;
                else hi = mid;
            }

            previous[i] = lo > 0 ? tailIndex[lo - 1] : -1;
            tailIndex[lo] = i;
            if (lo == length) length++;
        }

        var keep = new bool[n];
        for (var i = length > 0 ? tailIndex[length - 1] : -1; i >= 0; i = previous[i]) keep[i] = true;
        return keep;
    }
}

public class AddElementsAction<T>(
    ObservableCollection<T> collection,
    IEnumerable<T> items,
    Func<T, FractionalPosition> getPosition,
    Action? onPostExecute = null)
    : CollectionActionBase<T>(collection, getPosition, onPostExecute)
    where T : class
{
    private readonly List<T> _items = items.ToList();

    public override bool HasEffect => _items.Count > 0;

    public override void Execute()
    {
        InsertEachSorted(_items);
        ExecutePostPass(_items);
    }

    public override void Undo()
    {
        RemoveEach(_items);
        UndoPostPass();
    }
}

/// <summary>
///     Removes <paramref name="items" /> from the collection — and on Undo puts
///     back ONLY what Execute actually removed.
///     <para>
///         An item that is not in the collection when this runs is not
///         removed (the loop's <c>Collection.Remove</c> simply returns false), and
///         Undo must not re-insert it anyway. That would resurrect objects
///         the user had deleted one gesture earlier: right-click delete a hand
///         shape (which left it in <c>SelectedHandShapes</c>), press Delete over
///         the still-selected shape, Ctrl+Z — and the shape is back. The
///         selection sites are fixed too, but the ACTION must not depend on every
///         caller's hygiene: whatever it did not remove, it does not restore, and
///         a request naming nothing present has no effect and makes no undo node.
///     </para>
/// </summary>
public class RemoveElementsAction<T>(
    ObservableCollection<T> collection,
    IEnumerable<T> items,
    Func<T, FractionalPosition> getPosition,
    Action? onPostExecute = null)
    : CollectionActionBase<T>(collection, getPosition, onPostExecute)
    where T : class
{
    private readonly List<T> _items = items.ToList();

    /// <summary>What the last Execute actually took out; null until it has run.</summary>
    private List<T>? _removed;

    /// <summary>
    ///     Before Execute: would this remove anything? After: did it? The history
    ///     asks BOTH times (Do gates on it, then PushAction re-checks after the
    ///     run), so the answer after Execute must not be read off the collection —
    ///     the items are gone by then.
    /// </summary>
    public override bool HasEffect =>
        _items.Count > 0 && (_removed?.Count > 0 || (_removed is null && Present(_items).Count > 0));

    public override void Execute()
    {
        _removed = Present(_items);
        RemoveEach(_removed);
        ExecutePostPass(_removed);
    }

    /// <summary>
    ///     Restores what Execute removed. An action undone WITHOUT ever being
    ///     executed keeps the original contract and inserts every item — that is
    ///     the bulk-paste half <see cref="CollectionActionBase{T}.InsertEachSorted" />
    ///     documents, and <c>BulkCollectionEditEquivalenceTests</c> drives it.
    /// </summary>
    public override void Undo()
    {
        InsertEachSorted(_removed ?? _items);
        UndoPostPass();
    }
}

public class MutateElementsAction<T>(
    ObservableCollection<T> collection,
    IEnumerable<T> targets,
    IEnumerable<T> oldStates,
    IEnumerable<T> newStates,
    Action<T, T> copyFn,
    Func<T, T, bool> stateEquals,
    Func<T, FractionalPosition> getPosition,
    Action? onPostExecute = null,
    string mergeVerb = "")
    : CollectionActionBase<T>(collection, getPosition, onPostExecute), IMergeableAction
    where T : class
{
    private readonly List<T> _newStates = newStates.ToList();
    private readonly List<T> _oldStates = oldStates.ToList();
    private readonly List<T> _targets = targets.ToList();

    public override bool HasEffect
    {
        get
        {
            if (_targets.Count == 0) return false;
            if (_oldStates.Count != _targets.Count || _newStates.Count != _targets.Count) return true;

            for (var i = 0; i < _targets.Count; i++)
            {
                if (!stateEquals(_oldStates[i], _newStates[i]))
                    return true;
            }

            return false;
        }
    }

    public string MergeVerb { get; } = mergeVerb;

    /// <summary>
    ///     Takes over <paramref name="next" />'s destination while keeping our own
    ///     origin, which is exactly what makes a run of ticks one undo step: the
    ///     merged action reads "from where the gesture started to wherever it has
    ///     got to now".
    ///     <para>
    ///         The targets must be the SAME OBJECTS in the same order — reference
    ///         equality, not value equality. Two actions can hold equal-looking
    ///         elements at the same position and still belong to different chart
    ///         objects (a level's chords vs. a clipboard clone), and folding those
    ///         together would write one gesture's result onto the other's items.
    ///     </para>
    /// </summary>
    public bool CanAbsorb(IChartAction next)
    {
        if (next is not MutateElementsAction<T> other) return false;
        if (!ReferenceEquals(Collection, other.Collection)) return false;
        if (other._targets.Count != _targets.Count) return false;
        for (var i = 0; i < _targets.Count; i++)
        {
            if (!ReferenceEquals(_targets[i], other._targets[i]))
                return false;
        }

        return true;
    }

    public void Absorb(IChartAction next)
    {
        var other = (MutateElementsAction<T>)next;
        // Our _oldStates stay: they are the gesture's true starting point.
        _newStates.Clear();
        _newStates.AddRange(other._newStates);
        // …and so does everything the enforcer changed on our NEIGHBOURS, which is
        // recorded per instance and would otherwise be dropped with `other`.
        AbsorbCollateral(other);
    }

    public override void Execute()
    {
        for (var i = 0; i < _targets.Count; i++) copyFn(_targets[i], _newStates[i]);
        SortCollection();
        ExecutePostPass(_targets);
    }

    public override void Undo()
    {
        for (var i = 0; i < _targets.Count; i++) copyFn(_targets[i], _oldStates[i]);
        SortCollection();
        UndoPostPass();
    }
}

// There is deliberately no general "mutate a ChordTemplate in place" action
// here. A ChordTemplate is SHARED — one instance backs every chord AND hand
// shape with that shape — so rewriting one rewrites all of them, which is the
// v0.33.2 corruption. To change a CHORD's shape, resolve-or-create a template
// that already has the new shape (ChordTemplateResolver) and repoint the
// chord's TemplateId. The only place in-place mutation is correct is the
// template EDITOR itself, where the user is explicitly editing that template
// for everything using it: State/ChordTemplateActions.cs
// (EditTemplateGlobalAction, and CreateAndSwitchTemplateAction for its
// "edit selected only" isolation path).

public class AddChordTemplatesAction : IChartAction, ILinkPolicyAction
{
    /// <summary>Linked measures: an editing gesture, copied to every linked placement.</summary>
    public LinkPolicy LinkPolicy => LinkPolicy.Propagate;

    private readonly ObservableCollection<ChordTemplate> _collection;
    private readonly List<ChordTemplate> _templates;

    public AddChordTemplatesAction(ObservableCollection<ChordTemplate> collection,
        IEnumerable<ChordTemplate> templates)
    {
        _collection = collection;
        _templates = templates.ToList();
    }

    public bool HasEffect => _templates.Count > 0;

    // Identity, not value equality. ChordTemplate.Equals ignores Id and compares
    // name + arpeggio + frets + fingers, so a plain Contains/Remove hits the
    // first VALUE-EQUAL template rather than the instance this action minted:
    // Execute would skip adding a template the new chords already point at (a
    // dangling TemplateId, which SngFromDomainMapper silently drops the chord
    // for), and Undo would delete somebody else's still-referenced template.
    // See ChordTemplateCollectionOps.
    public void Execute()
    {
        foreach (var t in _templates)
        {
            if (!ChordTemplateCollectionOps.ContainsInstance(_collection, t))
                _collection.Add(t);
        }
    }

    public void Undo()
    {
        foreach (var t in _templates) ChordTemplateCollectionOps.RemoveInstance(_collection, t);
    }
}

public class AddNotesAction(
    ObservableCollection<GuitarNote> collection,
    IEnumerable<GuitarNote> notes,
    Action? onPostExecute = null)
    : AddElementsAction<GuitarNote>(collection, notes, n => n.Position, onPostExecute);

public class RemoveNotesAction(
    ObservableCollection<GuitarNote> collection,
    IEnumerable<GuitarNote> notes,
    Action? onPostExecute = null)
    : RemoveElementsAction<GuitarNote>(collection, notes, n => n.Position, onPostExecute);

public class MutateNotesAction : MutateElementsAction<GuitarNote>
{
    public MutateNotesAction(ObservableCollection<GuitarNote> collection, IEnumerable<GuitarNote> targets,
        IEnumerable<GuitarNote> oldStates, IEnumerable<GuitarNote> newStates, Action? onPostExecute = null,
        string mergeVerb = "")
        : base(collection, targets, oldStates, newStates, (t, s) => t.CopyFrom(s),
            ChartActionStateComparer.GuitarNotesEqual, n => n.Position, onPostExecute, mergeVerb)
    {
    }

    public MutateNotesAction(
        ObservableCollection<GuitarNote> collection,
        IReadOnlyList<GuitarNote> targets, // <-- Changed here
        Action<GuitarNote> mutateFn,
        Action? onPostExecute = null)
        : base(
            collection,
            targets,
            targets.Select(t => t.Clone()),
            targets.Select(t =>
            {
                var c = t.Clone();
                mutateFn(c);
                return c;
            }),
            (t, s) => t.CopyFrom(s),
            ChartActionStateComparer.GuitarNotesEqual,
            n => n.Position,
            onPostExecute)
    {
    }
}

public class AddChordsAction(
    ObservableCollection<Chord> collection,
    IEnumerable<Chord> items,
    Action? onPostExecute = null)
    : AddElementsAction<Chord>(collection, items, c => c.Position, onPostExecute);

public class RemoveChordsAction(
    ObservableCollection<Chord> collection,
    IEnumerable<Chord> items,
    Action? onPostExecute = null)
    : RemoveElementsAction<Chord>(collection, items, c => c.Position, onPostExecute);

public class MutateChordsAction(
    ObservableCollection<Chord> collection,
    IEnumerable<Chord> targets,
    IEnumerable<Chord> oldStates,
    IEnumerable<Chord> newStates,
    Action? onPostExecute = null,
    string mergeVerb = "")
    : MutateElementsAction<Chord>(collection, targets, oldStates, newStates, (t, s) => t.CopyFrom(s),
        ChartActionStateComparer.ChordsEqual, c => c.Position, onPostExecute, mergeVerb);

public class AddHandShapesAction(
    ObservableCollection<HandShape> collection,
    IEnumerable<HandShape> items,
    Action? onPostExecute = null)
    : AddElementsAction<HandShape>(collection, items, h => h.Position, onPostExecute);

public class RemoveHandShapesAction(
    ObservableCollection<HandShape> collection,
    IEnumerable<HandShape> items,
    Action? onPostExecute = null)
    : RemoveElementsAction<HandShape>(collection, items, h => h.Position, onPostExecute);

public class MutateHandShapesAction(
    ObservableCollection<HandShape> collection,
    IEnumerable<HandShape> targets,
    IEnumerable<HandShape> oldStates,
    IEnumerable<HandShape> newStates,
    Action? onPostExecute = null,
    string mergeVerb = "")
    : MutateElementsAction<HandShape>(collection, targets, oldStates, newStates, (t, s) => t.CopyFrom(s),
        ChartActionStateComparer.HandShapesEqual, h => h.Position, onPostExecute, mergeVerb);

public class AddEventPointsAction(ObservableCollection<EventPoint> collection, IEnumerable<EventPoint> items)
    : AddElementsAction<EventPoint>(collection, items, e => e.Position);

public class RemoveEventPointsAction(ObservableCollection<EventPoint> collection, IEnumerable<EventPoint> items)
    : RemoveElementsAction<EventPoint>(collection, items, e => e.Position);

public class MutateEventPointsAction(
    ObservableCollection<EventPoint> collection,
    IEnumerable<EventPoint> targets,
    IEnumerable<EventPoint> oldStates,
    IEnumerable<EventPoint> newStates)
    : MutateElementsAction<EventPoint>(collection, targets, oldStates, newStates, (t, s) =>
    {
        t.Position = s.Position;
        t.Phrase = s.Phrase;
        t.Section = s.Section;
    }, ChartActionStateComparer.EventPointsEqual, e => e.Position);

/// <summary>
///     Sets or clears <see cref="PhraseInfo.Solo" /> for one phrase NAME.
///     <para>
///     <c>Arrangement.PhraseInfos</c> is a plain dictionary keyed by name, not an
///     observable collection, so this is not an <c>AddElementsAction</c>: the entry
///     may have to be CREATED — and removed again on undo, or undo leaves behind a
///     default-valued row that the RS-XML writer then emits as a phrase property the
///     user never authored.
///     </para>
///     The old value is captured at construction, like every other mutate action here.
/// </summary>
public class SetPhraseSoloAction : IChartAction, ILinkPolicyAction
{
    /// <summary>Linked measures: cannot change linked content in beats.</summary>
    public LinkPolicy LinkPolicy => LinkPolicy.Retain;

    private readonly Arrangement _arrangement;
    private readonly bool _hadEntry;
    private readonly bool _oldSolo;
    private readonly string _phraseName;
    private readonly bool _solo;

    public SetPhraseSoloAction(Arrangement arrangement, string phraseName, bool solo)
    {
        _arrangement = arrangement;
        _phraseName = phraseName;
        _solo = solo;
        _hadEntry = arrangement.PhraseInfos.TryGetValue(phraseName, out var existing);
        _oldSolo = existing?.Solo ?? false;
    }

    public bool HasEffect => !string.IsNullOrWhiteSpace(_phraseName) && _oldSolo != _solo;

    public void Execute()
    {
        if (string.IsNullOrWhiteSpace(_phraseName)) return;
        if (_arrangement.PhraseInfos.TryGetValue(_phraseName, out var info)) info.Solo = _solo;
        else _arrangement.PhraseInfos[_phraseName] = new PhraseInfo(0, _solo);
    }

    public void Undo()
    {
        if (string.IsNullOrWhiteSpace(_phraseName)) return;
        if (!_hadEntry)
        {
            _arrangement.PhraseInfos.Remove(_phraseName);
            return;
        }

        if (_arrangement.PhraseInfos.TryGetValue(_phraseName, out var info)) info.Solo = _oldSolo;
    }
}

public class AddToneChangesAction(ObservableCollection<ToneChange> collection, IEnumerable<ToneChange> items)
    : AddElementsAction<ToneChange>(collection, items, t => t.Position);

public class RemoveToneChangesAction(ObservableCollection<ToneChange> collection, IEnumerable<ToneChange> items)
    : RemoveElementsAction<ToneChange>(collection, items, t => t.Position);

public class MutateBeatsAction(
    IList<BeatMarker> beats,
    IEnumerable<BeatMarker> oldBeats,
    IEnumerable<BeatMarker> newBeats,
    Action? onPostExecute = null)
    : IChartAction, ILinkPolicyAction
{
    /// <summary>Linked measures: cannot change linked content in beats.</summary>
    public LinkPolicy LinkPolicy => LinkPolicy.Retain;

    private readonly IList<BeatMarker> _beats = beats;
    private readonly List<BeatMarker> _newBeats = newBeats.Select(beat => beat.Clone()).ToList();
    private readonly List<BeatMarker> _oldBeats = oldBeats.Select(beat => beat.Clone()).ToList();

    public bool HasEffect
    {
        get
        {
            if (_oldBeats.Count != _newBeats.Count) return true;
            for (var i = 0; i < _oldBeats.Count; i++)
            {
                if (!ChartActionStateComparer.BeatsEqual(_oldBeats[i], _newBeats[i]))
                    return true;
            }

            return false;
        }
    }

    public void Execute() => Apply(_newBeats);

    public void Undo() => Apply(_oldBeats);

    private void Apply(IReadOnlyList<BeatMarker> states)
    {
        for (var i = 0; i < _beats.Count && i < states.Count; i++)
        {
            _beats[i].Time = states[i].Time;
            _beats[i].Bpm = states[i].Bpm;
            _beats[i].IsAnchor = states[i].IsAnchor;
            _beats[i].IsMeasure = states[i].IsMeasure;
            _beats[i].BeatsInMeasure = states[i].BeatsInMeasure;
            _beats[i].NoteDenominator = states[i].NoteDenominator;
        }

        onPostExecute?.Invoke();
    }
}

public class AddAnchorsAction(ObservableCollection<FretAnchor> collection, IEnumerable<FretAnchor> items)
    : AddElementsAction<FretAnchor>(collection, items, a => a.Position);

public class RemoveAnchorsAction(ObservableCollection<FretAnchor> collection, IEnumerable<FretAnchor> items)
    : RemoveElementsAction<FretAnchor>(collection, items, a => a.Position);

/// <summary>
///     Changes existing anchors in place. Carries a <paramref name="mergeVerb" />
///     like the other mutate actions so a nudge RUN — holding the key to walk an
///     anchor down four frets — lands as one undo step.
/// </summary>
public class MutateAnchorsAction(
    ObservableCollection<FretAnchor> collection,
    IEnumerable<FretAnchor> targets,
    IEnumerable<FretAnchor> oldStates,
    IEnumerable<FretAnchor> newStates,
    Action? onPostExecute = null,
    string mergeVerb = "")
    : MutateElementsAction<FretAnchor>(collection, targets, oldStates, newStates, (t, s) => t.CopyFrom(s),
        ChartActionStateComparer.AnchorsEqual, a => a.Position, onPostExecute, mergeVerb);

public class RenameStringFieldAction<T> : IChartAction, ILinkPolicyAction where T : class
{
    /// <summary>Linked measures: cannot change linked content in beats.</summary>
    public LinkPolicy LinkPolicy => LinkPolicy.Retain;

    private readonly List<T> _items;
    private readonly string _newValue;
    private readonly string _oldValue;
    private readonly Action<T, string> _setter;

    public RenameStringFieldAction(IEnumerable<T> items, Action<T, string> setter, string oldValue, string newValue)
    {
        _items = items.ToList();
        _setter = setter;
        _oldValue = oldValue;
        _newValue = newValue;
    }

    public bool HasEffect => _items.Count > 0 && _oldValue != _newValue;

    public void Execute()
    {
        foreach (var i in _items) _setter(i, _newValue);
    }

    public void Undo()
    {
        foreach (var i in _items) _setter(i, _oldValue);
    }
}

public class SetKeySignatureAction(ChartData chart, int newAccidentals, bool newIsMinor) : IChartAction, ILinkPolicyAction
{
    /// <summary>Linked measures: cannot change linked content in beats.</summary>
    public LinkPolicy LinkPolicy => LinkPolicy.Retain;

    private readonly int _oldAccidentals = chart.KeyAccidentals;
    private readonly bool _oldIsMinor = chart.KeyIsMinor;

    public bool HasEffect => _oldAccidentals != newAccidentals || _oldIsMinor != newIsMinor;

    public void Execute()
    {
        chart.KeyAccidentals = newAccidentals;
        chart.KeyIsMinor = newIsMinor;
    }

    public void Undo()
    {
        chart.KeyAccidentals = _oldAccidentals;
        chart.KeyIsMinor = _oldIsMinor;
    }
}

public static class ChartActionStateComparer
{
    public static bool GuitarNotesEqual(GuitarNote a, GuitarNote b)
    {
        return a.Position == b.Position &&
               a.Sustain == b.Sustain &&
               a.StringIndex == b.StringIndex &&
               a.Fret == b.Fret &&
               a.BassPicking == b.BassPicking &&
               a.Mute == b.Mute &&
               a.Hopo == b.Hopo &&
               a.Harmonic == b.Harmonic &&
               a.Vibrato == b.Vibrato &&
               EffectiveVibratoStrength(a) == EffectiveVibratoStrength(b) &&
               a.Tremolo == b.Tremolo &&
               a.LinkNext == b.LinkNext &&
               a.SlideTo == b.SlideTo &&
               a.UnpitchedSlide == b.UnpitchedSlide &&
               a.Accent == b.Accent &&
               a.Ignore == b.Ignore &&
               // The PROVENANCE half of Ignore, and it must be compared for the
               // same reason Ignore is: MutateNotesAction transfers it (CopyFrom
               // assigns it), so a mutation that moves only IgnoreDerived was
               // performed by Execute and reported as a no-op by HasEffect — the
               // action got dropped, and the "authored wins" clear that
               // InspectorViewModel.OnNoteIgnoreChanged does IN THE SAME
               // mutation went with it. The next save's ChartSpacingEnforcer
               // then reads a stale "we set this" flag and retracts the
               // charter's own Ignore. ArrangementSoundSync uses this comparer
               // to decide whether a twin arrangement needs the note at all, so
               // there the divergence just persists.
               a.IgnoreDerived == b.IgnoreDerived &&
               a.PassOtherNotes == b.PassOtherNotes &&
               // Notation-only authoring hints (sheet-music duration/grace edits).
               // Without these a duration-only mutation reads as a no-op and
               // MutateElementsAction.HasEffect drops it — sheet duration editing
               // would silently do nothing and record no undo step.
               a.NotationDuration.Equals(b.NotationDuration) &&
               a.GraceFret == b.GraceFret &&
               a.LeftHandFinger == b.LeftHandFinger &&
               a.PickDirection == b.PickDirection &&
               // Import-transient (GpPostProcessor.MaterializeGraceSlides clears
               // it, and no format persists it), so in practice it is false on
               // both sides — but CopyFrom TRANSFERS it, and the rule this whole
               // comparer lives by is that it must see exactly what the action
               // can transfer. A field that Execute writes and HasEffect cannot
               // see is a silently dropped edit; keeping the two sets identical
               // is what makes ChartActionFieldCoverageTests able to prove it.
               a.GraceSlide == b.GraceSlide &&
               BendValuesEqual(a.BendValues, b.BendValues);
    }

    public static bool ChordsEqual(Chord a, Chord b)
    {
        if (a.Position != b.Position ||
            a.TemplateId != b.TemplateId ||
            a.SplitIntoNotes != b.SplitIntoNotes ||
            a.ForceNoNotes != b.ForceNoNotes ||
            a.Accent != b.Accent ||
            a.Ignore != b.Ignore ||
            // Ignore's provenance discriminator — see GuitarNotesEqual. Same
            // trap: MutateChordsAction transfers it, so leaving it out made an
            // IgnoreDerived-only mutation read as a no-op and get dropped.
            a.IgnoreDerived != b.IgnoreDerived ||
            a.PassOtherNotes != b.PassOtherNotes ||
            // Sheet-music authored duration hint — see GuitarNotesEqual.
            !a.NotationDuration.Equals(b.NotationDuration) ||
            a.Strum != b.Strum ||
            a.ChordNotes.Count != b.ChordNotes.Count)
        {
            return false;
        }

        for (var i = 0; i < a.ChordNotes.Count; i++)
        {
            if (!ChordNotesEqual(a.ChordNotes[i], b.ChordNotes[i]))
                return false;
        }

        return true;
    }

    public static bool HandShapesEqual(HandShape a, HandShape b)
    {
        return a.Position == b.Position &&
               a.EndPosition == b.EndPosition &&
               a.TemplateId == b.TemplateId &&
               a.Name == b.Name;
    }

    public static bool EventPointsEqual(EventPoint a, EventPoint b)
    {
        return a.Position == b.Position &&
               a.Phrase == b.Phrase &&
               a.Section == b.Section;
    }

    public static bool AnchorsEqual(FretAnchor a, FretAnchor b)
    {
        return a.Position == b.Position &&
               a.Fret == b.Fret &&
               a.Width == b.Width;
    }

    public static bool BeatsEqual(BeatMarker a, BeatMarker b)
    {
        return a.Time.Equals(b.Time) &&
               a.Bpm.Equals(b.Bpm) &&
               a.IsAnchor == b.IsAnchor &&
               a.IsMeasure == b.IsMeasure &&
               a.BeatsInMeasure == b.BeatsInMeasure &&
               a.NoteDenominator == b.NoteDenominator;
    }

    private static bool ChordNotesEqual(ChordNote a, ChordNote b)
    {
        return a.StringIndex == b.StringIndex &&
               a.Fret == b.Fret &&
               // Per-string accent and ignore. Both are real ChordNote fields
               // the rscp round-trips (accent also reaches RS-XML and the SNG
               // mask), and the inspector's per-string chips already write
               // Ignore — but neither was compared, so a mutation touching only
               // one of them read as a no-op and MutateElementsAction.HasEffect
               // dropped the whole edit with no history node. Same trap the
               // GraceFret comment below records.
               a.Accent == b.Accent &&
               a.Ignore == b.Ignore &&
               // Notation-only, but it MUST be compared: MutateElementsAction
               // .HasEffect uses this to decide whether an edit changed
               // anything, so omitting a field makes a mutation that touches
               // only that field read as a no-op and get silently dropped.
               a.GraceFret == b.GraceFret &&
               a.Sustain == b.Sustain &&
               // EOF's explicit sustain STATUS, which is what holds a repeated
               // chord at its full panel on export - see ChordNote.ExplicitSustain.
               // Same trap as the fields above: uncompared means an edit that
               // toggles only this reads as a no-op and gets dropped.
               a.ExplicitSustain == b.ExplicitSustain &&
               a.BassPicking == b.BassPicking &&
               a.Mute == b.Mute &&
               a.Hopo == b.Hopo &&
               a.Harmonic == b.Harmonic &&
               a.Vibrato == b.Vibrato &&
               EffectiveVibratoStrength(a) == EffectiveVibratoStrength(b) &&
               a.Tremolo == b.Tremolo &&
               a.LinkNext == b.LinkNext &&
               a.SlideTo == b.SlideTo &&
               a.UnpitchedSlide == b.UnpitchedSlide &&
               BendValuesEqual(a.BendValues, b.BendValues);
    }

    private static bool BendValuesEqual(IReadOnlyList<BendValue> a, IReadOnlyList<BendValue> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].Position != b[i].Position || !a[i].Step.Equals(b[i].Step))
                return false;
        }

        return true;
    }

    private static int EffectiveVibratoStrength(IGuitarSound sound) => sound.Vibrato ? Math.Max(0, sound.VibratoStrength) : 0;

}
