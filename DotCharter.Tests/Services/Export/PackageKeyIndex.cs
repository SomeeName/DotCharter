using DotCharter.Services.Library.Index;

namespace DotCharter.Tests.Services.Export;

/// <summary>A library index that knows a fixed set of package song keys (case-insensitive) and nothing else.</summary>
internal sealed class PackageKeyIndex(params string[] keys) : ILibraryIndex
{
    private readonly Dictionary<string, List<string>> _paths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Records that the package at <paramref name="path" /> holds <paramref name="key" />.</summary>
    public PackageKeyIndex WithPackage(string key, string path)
    {
        if (!_paths.TryGetValue(key, out var list)) _paths[key] = list = [];
        list.Add(path);
        return this;
    }

    public IReadOnlyList<string> PackagePathsForSongKey(string songKey) =>
        _paths.TryGetValue(songKey, out var list) ? list : [];

    public ILibraryIndexScan? BeginScan() => null;
    public void TrySetSongRating(string psarcPath, string songKey, double rating) { }
    public LibraryIndexFacts ReadFacts() => LibraryIndexFacts.Empty;

    public bool HasSongKey(string songKey) => keys.Contains(songKey, StringComparer.OrdinalIgnoreCase) || _paths.ContainsKey(songKey);
}
