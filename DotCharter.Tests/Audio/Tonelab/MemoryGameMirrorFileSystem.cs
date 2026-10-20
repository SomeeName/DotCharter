using DotCharter.Tone.Audio.Tonelab;

namespace DotCharter.Tests.Audio.Tonelab;

/// <summary>
///     Files, hardlinks and junctions in memory. A path's volume is its drive letter; a file is a
///     node (an identity plus bytes) that several names can share, as hardlinks do. Writes only ever
///     create NEW names, and every operation is recorded, so a test can prove nothing was written
///     through an existing name.
/// </summary>
internal sealed class MemoryGameMirrorFileSystem : IGameMirrorFileSystem
{
    private readonly Dictionary<string, FileNode> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _junctions = new(StringComparer.OrdinalIgnoreCase);
    private ulong _nextIndex = 1;

    public List<string> Log { get; } = [];

    /// <summary>Folders (and everything under them) that refuse to be created.</summary>
    public HashSet<string> Unwritable { get; } = new(StringComparer.OrdinalIgnoreCase);

    public sealed class FileNode(GameFileId id, byte[] bytes)
    {
        public GameFileId Id { get; } = id;

        public byte[] Bytes { get; } = bytes;
    }

    /// <summary>A new file (a new identity) at <paramref name="path" />, replacing the name as Steam's update does.</summary>
    public void Put(string path, string text = "x")
    {
        _files[path] = new FileNode(new GameFileId(Volume(path), _nextIndex++), System.Text.Encoding.UTF8.GetBytes(text));
        AddParents(path);
    }

    public void PutDirectory(string path)
    {
        _directories.Add(path);
        AddParents(path);
    }

    public FileNode Node(string path) => _files[path];

    public string Text(string path) => System.Text.Encoding.UTF8.GetString(_files[path].Bytes);

    public bool FileExists(string path) => _files.ContainsKey(path);

    public bool DirectoryExists(string path) => _directories.Contains(path) || _junctions.ContainsKey(path);

    public string? JunctionTarget(string path) => _junctions.GetValueOrDefault(path);

    public GameFileId? FileId(string path) => _files.TryGetValue(path, out var node) ? node.Id : null;

    public IReadOnlyList<string> FileNames(string directory, string pattern)
    {
        var extension = pattern.TrimStart('*');
        return _files.Keys
            .Where(p => string.Equals(Path.GetDirectoryName(p), directory, StringComparison.OrdinalIgnoreCase) &&
                        p.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            .Select(p => Path.GetFileName(p))
            .ToList();
    }

    public void CreateDirectory(string path)
    {
        if (Unwritable.Any(u => path.StartsWith(u, StringComparison.OrdinalIgnoreCase))) throw new UnauthorizedAccessException($"{path} (test)");
        if (!_directories.Contains(path)) Log.Add("mkdir " + path);
        PutDirectory(path);
    }

    public void CreateHardLink(string link, string existing)
    {
        if (Volume(link) != Volume(existing)) throw new GameMirrorCrossVolumeException($"{link} / {existing} (test)");
        if (_files.ContainsKey(link)) throw new IOException($"{link} exists (test)");
        Log.Add("link " + link);
        _files[link] = _files[existing];
    }

    public void CreateJunction(string link, string target)
    {
        if (DirectoryExists(link)) throw new IOException($"{link} exists (test)");
        Log.Add("junction " + link);
        _junctions[link] = target;
    }

    public void RemoveJunction(string path)
    {
        Log.Add("unjunction " + path);
        _junctions.Remove(path);
    }

    public void DeleteFile(string path)
    {
        Log.Add("delete " + path);
        _files.Remove(path);
    }

    public void CopyToNewFile(string source, string destination)
    {
        if (_files.ContainsKey(destination)) throw new IOException($"{destination} exists: a write through an existing name (test)");
        Log.Add("copy " + destination);
        _files[destination] = new FileNode(new GameFileId(Volume(destination), _nextIndex++), [.. _files[source].Bytes]);
    }

    public void WriteNewFile(string path, byte[] bytes)
    {
        if (_files.ContainsKey(path)) throw new IOException($"{path} exists: a write through an existing name (test)");
        Log.Add("write " + path);
        _files[path] = new FileNode(new GameFileId(Volume(path), _nextIndex++), [.. bytes]);
    }

    private static uint Volume(string path) => char.ToUpperInvariant(path[0]);

    private void AddParents(string path)
    {
        for (var dir = Path.GetDirectoryName(path); !string.IsNullOrEmpty(dir) && Path.GetPathRoot(dir) != dir; dir = Path.GetDirectoryName(dir))
            _directories.Add(dir);
    }
}
