using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DotCharter.Localization;

/// <summary>A language-switch callback that does not keep its owner alive: the localization service outlives
/// every app container, so a strong handler would pin the owner.
/// <para>Every subscription to one localization shares ONE handler on it and sits in that localization's
/// registry. A dead owner's entry is dropped on the next change, and also while subscribing: once the registry
/// has doubled since its last sweep, the add sweeps it first, so a session that rebuilds its subscribers
/// without ever switching the language holds at most about twice its live subscriptions (amortised O(1) per
/// add).</para></summary>
public static class WeakLanguageSubscription
{
    private static readonly ConditionalWeakTable<ILocalizationService, Registry> Registries = [];

    /// <summary>Calls <paramref name="changed" /> with the owner on every change; it must not capture the owner itself.</summary>
    public static void Subscribe<TOwner>(ILocalizationService localization, TOwner owner, Action<TOwner> changed)
        where TOwner : class
    {
        ArgumentNullException.ThrowIfNull(localization);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(changed);
        Registries.GetValue(localization, static loc => new Registry(loc)).Add(new Entry<TOwner>(owner, changed));
    }

    /// <summary>The entries held for <paramref name="localization" />, dead ones not yet swept included.</summary>
    internal static int CountFor(ILocalizationService localization) =>
        Registries.TryGetValue(localization, out var registry) ? registry.Count : 0;

    private interface IEntry
    {
        bool IsAlive { get; }

        void Invoke();
    }

    private sealed class Entry<TOwner>(TOwner owner, Action<TOwner> changed) : IEntry
        where TOwner : class
    {
        private readonly WeakReference<TOwner> _owner = new(owner);

        public bool IsAlive => _owner.TryGetTarget(out _);

        public void Invoke()
        {
            if (_owner.TryGetTarget(out var target)) changed(target);
        }
    }

    private sealed class Registry
    {
        private const int MinSweepAt = 16;
        private readonly object _gate = new();
        private readonly List<IEntry> _entries = [];
        private int _sweepAt = MinSweepAt;

        public Registry(ILocalizationService localization) => localization.PropertyChanged += OnChanged;

        public int Count
        {
            get
            {
                lock (_gate) return _entries.Count;
            }
        }

        public void Add(IEntry entry)
        {
            lock (_gate)
            {
                if (_entries.Count >= _sweepAt) Sweep();
                _entries.Add(entry);
            }
        }

        private void OnChanged(object? sender, PropertyChangedEventArgs e)
        {
            IEntry[] live;
            lock (_gate)
            {
                Sweep();
                live = [.. _entries];
            }

            foreach (var entry in live) entry.Invoke();
        }

        private void Sweep()
        {
            _entries.RemoveAll(static entry => !entry.IsAlive);
            _sweepAt = Math.Max(MinSweepAt, _entries.Count * 2);
        }
    }
}
