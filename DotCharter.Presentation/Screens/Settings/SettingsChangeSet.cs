using System.ComponentModel;

namespace DotCharter.Presentation.Screens.Settings;

/// <summary>
///     A tab's settings compared against the values it last loaded or saved.
///     The set is explicit — a tab names each setting it tracks — so a status
///     field can never be counted as an edit.
/// </summary>
public sealed class SettingsChangeSet
{
    private readonly List<(string Key, Func<object?> Read)> _entries = [];
    private readonly Dictionary<string, object?> _baseline = new(StringComparer.Ordinal);

    public void Track(string key, Func<object?> read)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        _entries.Add((key, read));
    }

    /// <summary>Tracks each named public property of <paramref name="owner" /> as <c>prefix + name</c>,
    /// and runs <paramref name="onTrackedChange" /> whenever one of them changes.</summary>
    public void TrackProperties(object owner, string prefix, IEnumerable<string> names,
        Action? onTrackedChange = null)
    {
        var type = owner.GetType();
        var tracked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var property = type.GetProperty(name)
                           ?? throw new ArgumentException($"{type.Name} has no property '{name}' to track.", nameof(names));
            Track(prefix + name, () => property.GetValue(owner));
            tracked.Add(name);
        }

        if (onTrackedChange is null) return;
        if (owner is not INotifyPropertyChanged notifier)
            throw new ArgumentException($"{type.Name} raises no property changes to follow.", nameof(owner));

        notifier.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is { } changed && tracked.Contains(changed)) onTrackedChange();
        };
    }

    public void Rebaseline()
    {
        _baseline.Clear();
        foreach (var (key, read) in _entries) _baseline[key] = read();
    }

    public IReadOnlyList<string> Differences()
    {
        var differing = new List<string>();
        foreach (var (key, read) in _entries)
        {
            if (_baseline.TryGetValue(key, out var saved) && !Equals(saved, read()))
                differing.Add(key);
        }

        return differing;
    }
}
