using System.Runtime.CompilerServices;
using DotCharter.Core.Domain.Guitar;

namespace DotCharter.Core.Utilities;

/// <summary>
///     Clone-related collections share identity tokens, not mutable state. Each collection maps
///     those tokens to its own instances so snapshot undo can reattach removed objects.
///     Weak keys keep spare copies and discarded history from retaining one another indefinitely.
/// </summary>
internal static class ChartSnapshotIdentities
{
    private static readonly ConditionalWeakTable<object, CollectionIdentity> Collections = [];
    private static readonly ConditionalWeakTable<object, object> NestedItems = [];
    [ThreadStatic] private static bool _capturing;
    [ThreadStatic] private static bool _snapshotDestinations;
    [ThreadStatic] private static bool _detached;

    public static IDisposable BeginCapture(bool snapshotDestinations = true)
    {
        var scope = new CaptureScope(_capturing, _snapshotDestinations, _detached);
        _capturing = true;
        _snapshotDestinations = snapshotDestinations;
        _detached = false;
        return scope;
    }

    public static IDisposable BeginDetachedCopy()
    {
        var scope = new CaptureScope(_capturing, _snapshotDestinations, _detached);
        _capturing = false;
        _detached = true;
        return scope;
    }

    // A child clone can move to a replacement working collection without losing its token.
    // Tokens are shared, never mutable objects. Top-level note/chord/template clones remain
    // distinct items so pasting a clone beside its source cannot duplicate a collection key.
    public static T RememberNestedClone<T>(T source, T clone) where T : class
    {
        if (!_detached) NestedItems.Add(clone, NestedItems.GetValue(source, _ => new object()));
        return clone;
    }

    private static bool IsNested<T>() => typeof(T) == typeof(ChordNote) || typeof(T) == typeof(BendValue);

    private static object TokenFor(FamilyIdentity family, object item) =>
        item is ChordNote or BendValue
            ? NestedItems.GetValue(item, _ => new object())
            : family.Items.GetValue(item, _ => new object());

    private static void Associate(FamilyIdentity family, object item, object token)
    {
        var table = item is ChordNote or BendValue ? NestedItems : family.Items;
        if (!table.TryGetValue(item, out _)) table.Add(item, token);
    }

    private static void Register<T>(IList<T> items, CollectionIdentity collection) where T : class
    {
        var present = IsNested<T>() && items.Count > 1
            ? new HashSet<object>(items, ReferenceEqualityComparer.Instance)
            : null;
        var seen = present == null ? null : new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var item in items)
        {
            var identity = TokenFor(collection.Family, item);
            // A clone inserted beside its source is a NEW child, rather than a second
            // occurrence of one identity. Keep the existing member's token.
            if (present != null && seen != null &&
                ((collection.Instances.TryGetValue(identity, out var member) &&
                  !ReferenceEquals(member, item) && present.Contains(member)) || !seen.Add(identity)))
            {
                NestedItems.Remove(item);
                identity = new object();
                NestedItems.Add(item, identity);
                seen.Add(identity);
            }

            if (!collection.Instances.TryGetValue(identity, out _)) collection.Instances.Add(identity, item);
        }
    }

    public static bool Reconcile<T>(IList<T> live, IList<T> snapshot, Action<T, T> restoreInto)
        where T : class, new()
    {
        if (_detached) return false;
        // Empty child collections need no tables. Existing mappings are retained when a
        // collection becomes empty, because undo may still need its detached originals.
        if (live.Count == 0 && snapshot.Count == 0 &&
            !Collections.TryGetValue(live, out _) && !Collections.TryGetValue(snapshot, out _))
        {
            return true;
        }
        if (!Collections.TryGetValue(snapshot, out var source))
        {
            if (!_capturing) return false;
            source = new CollectionIdentity(new FamilyIdentity());
            Collections.Add(snapshot, source);
        }

        // An empty new owner must adopt the source family on first materialization.
        // Unrelated populated graphs keep their positional field-copy contract.
        if (!_capturing && live.Count != 0 && !Collections.TryGetValue(live, out _)) return false;
        var family = source.Family;
        // Register the source before creating the destination: a clone is also the point at
        // which the original graph's identities are captured, before a tool can remove them.
        Register(snapshot, source);

        if (Collections.TryGetValue(live, out var destination))
        {
            // Independently populated graphs still use the positional field-copy contract.
            if (!ReferenceEquals(destination.Family, family) && !IsNested<T>()) return false;
        }
        else
        {
            destination = new CollectionIdentity(family, _snapshotDestinations);
            Collections.Add(live, destination);
            // Restore into an unrelated, populated destination keeps its existing prefix.
            // Empty destinations (clones) acquire fresh instances below.
            for (var i = 0; i < Math.Min(live.Count, snapshot.Count); i++)
            {
                var identity = TokenFor(family, snapshot[i]);
                if (!destination.Instances.TryGetValue(identity, out _)) destination.Instances.Add(identity, live[i]);
                Associate(family, live[i], identity);
            }
        }

        // A clone is a working snapshot until it is used as a live restore target.
        if (!_capturing || !_snapshotDestinations) destination.IsSnapshot = false;

        var restored = new T[snapshot.Count];
        for (var i = 0; i < snapshot.Count; i++)
        {
            var identity = TokenFor(family, snapshot[i]);
            var materializing = !destination.Instances.TryGetValue(identity, out var instance);
            if (materializing)
            {
                instance = new T();
                destination.Instances.Add(identity, instance);
                Associate(family, instance, identity);
            }

            var item = (T)instance!;
            // A newly created parent has unregistered child collections. Bind those
            // recursively now, before CopyFrom or another history action captures them.
            using var materialization = materializing
                ? BeginCapture(snapshotDestinations: _capturing && _snapshotDestinations)
                : null;
            restoreInto(item, snapshot[i]);
            restored[i] = item;
        }

        ApplyRestoredItems(live, restored);
        return true;
    }

    private static void ApplyRestoredItems<T>(IList<T> live, T[] restored) where T : class
    {
        // Reassign slots only after resolving the whole sequence, including objects which
        // moved from another slot. Identity comparisons are essential for ChordTemplate.
        var shared = Math.Min(live.Count, restored.Length);
        for (var i = 0; i < shared; i++)
        {
            if (!ReferenceEquals(live[i], restored[i])) live[i] = restored[i];
        }

        for (var i = live.Count - 1; i >= restored.Length; i--) live.RemoveAt(i);
        for (var i = live.Count; i < restored.Length; i++) live.Add(restored[i]);
    }

    /// <summary>
    /// Counts metadata and detached objects which this collection still owns. Working
    /// snapshots never need their removed clones again: only live owners reattach objects.
    /// Prune those mappings before a history action seals and weighs its snapshots.
    /// </summary>
    public static long EstimateBytes<T>(IList<T> items, Func<T, long> estimate) where T : class
    {
        if (!Collections.TryGetValue(items, out var collection))
        {
            if (!_capturing || items.Count == 0) return items.Sum(estimate);
            collection = new CollectionIdentity(new FamilyIdentity(), isSnapshot: true);
            Collections.Add(items, collection);
        }
        // History accounting explicitly seals a working snapshot, including replacement
        // collections first encountered as a CopyFrom source.
        if (_capturing && _snapshotDestinations) collection.IsSnapshot = true;
        var current = new HashSet<object>(items, ReferenceEqualityComparer.Instance);
        Register(items, collection);
        if (collection.IsSnapshot)
        {
            foreach (var (identity, instance) in collection.Instances)
            {
                if (!current.Contains(instance)) collection.Instances.Remove(identity);
            }
        }

        // Includes the collection/family tables, tokens and entries in both directions.
        // Conservative structural costs, independent of GC timing or process memory.
        var bytes = 512L;
        foreach (var (_, instance) in collection.Instances)
        {
            bytes += 192;
            if (current.Add(instance)) bytes += estimate((T)instance);
        }

        foreach (var item in items) bytes += estimate(item);
        return bytes;
    }

    private sealed class FamilyIdentity
    {
        public ConditionalWeakTable<object, object> Items { get; } = [];
    }

    private sealed class CollectionIdentity(FamilyIdentity family, bool isSnapshot = false)
    {
        public FamilyIdentity Family { get; } = family;

        public bool IsSnapshot { get; set; } = isSnapshot;

        public ConditionalWeakTable<object, object> Instances { get; } = [];
    }

    private sealed class CaptureScope(bool previous, bool snapshotDestinations, bool detached) : IDisposable
    {
        public void Dispose()
        {
            _capturing = previous;
            _snapshotDestinations = snapshotDestinations;
            _detached = detached;
        }
    }
}
