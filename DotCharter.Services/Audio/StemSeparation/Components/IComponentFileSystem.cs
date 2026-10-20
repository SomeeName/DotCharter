using System.Security.Cryptography;

namespace DotCharter.Services.Audio.StemSeparation.Components;

/// <summary>
/// Narrow filesystem seam the pure <see cref="ComponentInventory"/> scans
/// through. Keeping every disk touch behind this interface lets the inventory
/// logic be unit-tested against an in-memory fake with no real files.
/// </summary>
public interface IComponentFileSystem
{
    /// <summary>True if a regular file exists at <paramref name="path"/>.</summary>
    bool FileExists(string path);

    /// <summary>Length of the file in bytes, or -1 when it does not exist / is unreadable.</summary>
    long GetFileSize(string path);

    /// <summary>
    /// Lowercase hex SHA-256 of the file, or null when the file is missing,
    /// unreadable, or hashing was not requested. Optional — a fake may return a
    /// canned value.
    /// </summary>
    string? ComputeSha256(string path);

    /// <summary>Directories on the process PATH, in order (may be empty).</summary>
    IReadOnlyList<string> PathDirectories { get; }
}

/// <summary>
/// Real, process-backed <see cref="IComponentFileSystem"/>. The only place in
/// the component subsystem that touches the actual disk / environment.
/// </summary>
public sealed class SystemComponentFileSystem : IComponentFileSystem
{
    public static SystemComponentFileSystem Instance { get; } = new();

    public bool FileExists(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { return File.Exists(path); }
        catch { return false; }
    }

    public long GetFileSize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return -1;
        try
        {
            var fi = new FileInfo(path);
            return fi.Exists ? fi.Length : -1;
        }
        catch { return -1; }
    }

    public string? ComputeSha256(string path)
    {
        if (!FileExists(path)) return null;
        try
        {
            using var stream = File.OpenRead(path);
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(stream);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch { return null; }
    }

    public IReadOnlyList<string> PathDirectories =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
