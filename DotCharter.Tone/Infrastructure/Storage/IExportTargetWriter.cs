namespace DotCharter.Tone.Infrastructure.Storage;

/// <summary>
///     The file access an export or import dialog needs around the work its
///     service does: whether a chosen or derived path exists, copying the song
///     audio into a new project folder, writing the cover art or a screenshot,
///     reading an import source, and the default export folder — and the
///     existence checks other view models make (Open Recent, Play Mode's own charts
///     and listen-back takes). The caller keeps the CHOICE of paths; this does the
///     touching. <see cref="ExportTargetWriter" /> is the disk implementation.
/// </summary>
public interface IExportTargetWriter
{
    bool FileExists(string path);
    bool DirectoryExists(string path);

    /// <summary>Copies <paramref name="source" /> to <paramref name="destination" />,
    /// overwriting it.</summary>
    void CopyFile(string source, string destination);

    void WriteAllBytes(string path, byte[] bytes);
    byte[] ReadAllBytes(string path);
    Task<string> ReadAllTextAsync(string path);
    Task WriteAllTextAsync(string path, string text);

    /// <summary>Creates the parent folder when missing, then creates (or
    /// truncates) <paramref name="path" /> for writing.</summary>
    Stream Create(string path);

    /// <summary>Deletes <paramref name="path" /> when it exists.</summary>
    void DeleteIfExists(string path);

    /// <summary>Every file under <paramref name="folder" /> matching
    /// <paramref name="pattern" />, recursively.</summary>
    string[] FindFiles(string folder, string pattern);

    /// <summary>The user's Documents folder: where an export goes when nothing
    /// better is known.</summary>
    string DocumentsFolder { get; }
}

/// <inheritdoc />
public sealed class ExportTargetWriter : IExportTargetWriter
{
    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public void CopyFile(string source, string destination) => File.Copy(source, destination, true);

    public void WriteAllBytes(string path, byte[] bytes) => File.WriteAllBytes(path, bytes);

    public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);

    public Task<string> ReadAllTextAsync(string path) => File.ReadAllTextAsync(path);

    public Task WriteAllTextAsync(string path, string text) => File.WriteAllTextAsync(path, text);

    public Stream Create(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        return File.Create(path);
    }

    public void DeleteIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    public string[] FindFiles(string folder, string pattern) =>
        Directory.GetFiles(folder, pattern, SearchOption.AllDirectories);

    public string DocumentsFolder => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
}
