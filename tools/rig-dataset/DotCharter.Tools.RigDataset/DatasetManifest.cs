using System.Text.Json;
using System.Text.Json.Serialization;
using DotCharter.Services.Ai.Match;

namespace DotCharter.Tools.RigDataset;

/// <summary>
///     <c>manifest.jsonl</c>: one <see cref="RigDatasetRow" /> per line, paths
///     relative to the manifest.
///     <para>
///         <b>Rewritten sorted on every close.</b> Four threads finish clips out of
///         order, so append order is scheduling order — and "the same seed reproduces
///         the manifest" is a property the model lane will rely on for a train/val
///         split. Sorting by clip index at the end makes the FILE deterministic even
///         though the WORK is not.
///     </para>
/// </summary>
public sealed class DatasetManifest : IDisposable
{
    public const string FileName = "manifest.jsonl";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    private readonly string _path;
    private readonly Lock _gate = new();
    private readonly SortedDictionary<int, string> _rows = [];

    /// <summary>Opens (or resumes) a manifest. <paramref name="writeTo" /> is the file
    /// this instance owns; every file in <paramref name="readAlso" /> is loaded for
    /// the RESUME check but never rewritten — which is how a shard knows what the
    /// merged manifest already has without owning it.</summary>
    public DatasetManifest(string writeTo, IEnumerable<string>? readAlso = null)
    {
        _path = writeTo;
        Load(writeTo, _rows);
        foreach (var file in readAlso ?? [])
        {
            if (string.Equals(file, writeTo, StringComparison.OrdinalIgnoreCase)) continue;
            var other = new SortedDictionary<int, string>();
            Load(file, other);
            foreach (var i in other.Keys) _known.Add(i);
        }
    }

    private static void Load(string file, IDictionary<int, string> into)
    {
        if (!File.Exists(file)) return;
        foreach (var line in File.ReadLines(file))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var index = IndexOfClipPath(TryReadClipPath(line));
            if (index >= 0) into[index] = line;
        }
    }

    /// <summary>Indices another file already has. Kept apart from <c>_rows</c> so a
    /// shard does not rewrite the merged manifest's rows into its own file — at 50,000
    /// clips that is ~75 MB of JSON copied per shard for nothing.</summary>
    private readonly HashSet<int> _known = [];

    public static string ShardFileName(int shard) => $"manifest.shard{shard}.jsonl";

    /// <summary>Clip indices already on disk — what makes a run RESUMABLE. A render
    /// interrupted at 31,000 of 50,000 clips must not redo the 31,000, and must not
    /// renumber them either: the index IS part of the seed.</summary>
    public IReadOnlySet<int> ExistingIndices
    {
        get
        {
            lock (_gate)
            {
                var set = _rows.Keys.ToHashSet();
                set.UnionWith(_known);
                return set;
            }
        }
    }

    /// <summary>Folds every <c>manifest.shard*.jsonl</c> under <paramref name="root" />
    /// into the one <c>manifest.jsonl</c> the contract names, sorted by clip index, and
    /// deletes the shards. Four processes finish out of order, so this is what makes
    /// the FILE reproducible when the WORK is not.</summary>
    public static int Merge(string root)
    {
        var merged = new SortedDictionary<int, string>();
        Load(Path.Combine(root, FileName), merged);
        var shards = Directory.GetFiles(root, "manifest.shard*.jsonl");
        foreach (var s in shards) Load(s, merged);

        var tmp = Path.Combine(root, FileName + ".tmp");
        File.WriteAllLines(tmp, merged.Values);
        File.Move(tmp, Path.Combine(root, FileName), overwrite: true);
        foreach (var s in shards) File.Delete(s);
        return merged.Count;
    }

    public int Count
    {
        get
        {
            lock (_gate) return _rows.Count;
        }
    }

    public void Add(int index, RigDatasetRow row)
    {
        var line = JsonSerializer.Serialize(row, Json);
        lock (_gate) _rows[index] = line;
    }

    /// <summary>Writes the whole manifest, sorted, through a temp file and a rename —
    /// a half-written manifest is worse than none, because it reads as a complete
    /// smaller dataset.</summary>
    public void Flush()
    {
        string[] lines;
        lock (_gate) lines = _rows.Values.ToArray();
        var tmp = _path + ".tmp";
        File.WriteAllLines(tmp, lines);
        File.Move(tmp, _path, overwrite: true);
    }

    public void Dispose() => Flush();

    private static string? TryReadClipPath(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.TryGetProperty(nameof(RigDatasetRow.ClipPath), out var p)
                ? p.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Clip paths are <c>clips/&lt;bucket&gt;/&lt;index:D8&gt;.wav</c>, so the
    /// index is recoverable from the name and the manifest needs no extra column for
    /// it.</summary>
    public static int IndexOfClipPath(string? clipPath)
    {
        if (string.IsNullOrEmpty(clipPath)) return -1;
        var name = Path.GetFileNameWithoutExtension(clipPath);
        return int.TryParse(name, out var i) ? i : -1;
    }

    public static string ClipPathFor(int index) =>
        $"clips/{index / 1000:D5}/{index:D8}.wav";
}
