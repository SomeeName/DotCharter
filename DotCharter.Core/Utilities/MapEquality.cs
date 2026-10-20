namespace DotCharter.Core.Utilities;

/// <summary>
///     Order-independent equality of two maps: the same keys, each with an equal
///     value. A chord shape is a map from string to fret (or finger), so this is
///     the one place "same shape" is decided for every grip comparison.
/// </summary>
public static class MapEquality
{
    public static bool SameEntries<TKey, TValue>(
        IReadOnlyDictionary<TKey, TValue> left,
        IReadOnlyDictionary<TKey, TValue> right)
        where TKey : notnull
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        var comparer = EqualityComparer<TValue>.Default;
        foreach (var (key, value) in left)
        {
            if (!right.TryGetValue(key, out var other) || !comparer.Equals(value, other))
            {
                return false;
            }
        }

        return true;
    }
}
