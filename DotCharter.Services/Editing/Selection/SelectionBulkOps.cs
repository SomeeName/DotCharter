using System.Collections.ObjectModel;

namespace DotCharter.Services.Editing.Selection;

/// <summary>
///     The ONE way a gesture writes many items into a selection collection.
///     <para>
///         Every <c>Selected*</c> collection on <see cref="SelectionService" /> is
///         a <see cref="BulkObservableCollection{T}" />, and every one of them has
///         a <c>CollectionChanged</c> handler that re-reads the WHOLE selection:
///         the service itself re-derives <c>SelectedNote</c>/<c>SelectedChord</c>
///         and raises <c>SelectionChanged</c>, and <c>InspectorViewModel</c> — a
///         DI singleton, alive from boot whether or not its pane is shown —
///         answers with <c>SyncNoteProperties</c>/<c>SyncChordProperties</c>,
///         which snapshot the selection and run seven full scans over it.
///     </para>
///     <para>
///         So adding N items one at a time is O(N²) in element copies and O(N)
///         in whole-selection rebuilds. Shift-clicking a 5,000-note level cost
///         ~12.5 M element copies across 5,000 list allocations. Adding items in
///         a loop is therefore not a style question — it is the fault, and it has
///         been reintroduced three times.
///     </para>
/// </summary>
public static class SelectionBulkOps
{
    /// <summary>
    ///     Makes <paramref name="target" /> hold exactly <paramref name="items" />,
    ///     in that order, with ONE notification. The
    ///     <see cref="BulkObservableCollection{T}" /> fast path is the whole point;
    ///     the fallback exists only so the helper works against a plain
    ///     <see cref="ObservableCollection{T}" /> in a test double.
    /// </summary>
    public static void ReplaceWith<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(items);

        if (target is BulkObservableCollection<T> bulk)
        {
            bulk.ResetWith(items);
            return;
        }

        target.Clear();
        foreach (var item in items)
            target.Add(item);
    }

    /// <summary>
    ///     Appends everything in <paramref name="additions" /> that
    ///     <paramref name="target" /> does not already hold, in the order the
    ///     source yields it, with ONE notification — the bulk equivalent of the
    ///     <c>if (!target.Contains(x)) target.Add(x)</c> loop it replaces.
    ///     Returns how many items were appended.
    ///     <para>
    ///         The membership test is <see cref="EqualityComparer{T}.Default" />,
    ///         which is precisely what <see cref="Collection{T}.Contains" /> used,
    ///         so the result is item-for-item what the loop produced. Nothing new
    ///         ⇒ nothing raised, which is also what the loop did.
    ///     </para>
    /// </summary>
    public static int AddRange<T>(ObservableCollection<T> target, IEnumerable<T> additions)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(additions);

        var present = new HashSet<T>(target);
        List<T>? union = null;

        foreach (var item in additions)
        {
            // HashSet.Add is the dedupe the loop got from re-testing Contains
            // after each Add: a source that yields the same item twice appends
            // it once.
            if (!present.Add(item)) continue;
            (union ??= [.. target]).Add(item);
        }

        if (union == null) return 0;

        var added = union.Count - target.Count;
        ReplaceWith(target, union);
        return added;
    }
}
