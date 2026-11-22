using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace DotCharter.Services.Export.Rocksmith;

/// <summary>
///     Keeps test-package receipts under the saved project, even when packages are written to a game folder.
///     Exact paths and SHA-256 digests authorize replacement and cleanup.
///     An unsaved scope remembers its own publications only in memory and never cleans earlier packages.
/// </summary>
public sealed class RocksmithTestPackageOwnership
{
    private readonly string? _projectDirectory;
    private readonly string? _ledgerPath;
    private readonly string _folder;
    private readonly Action<string> _recycle;
    private readonly Func<string, FileAttributes> _attributes;
    private readonly ConcurrentDictionary<string, byte> _approved = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _transient = new(StringComparer.OrdinalIgnoreCase);
    private int _building;

    public RocksmithTestPackageOwnership(string? projectPath, string projectId, string testFolder)
        : this(projectPath, projectId, testFolder, RecycleBin.Remove) { }

    internal RocksmithTestPackageOwnership(string? projectPath, string projectId, string testFolder, Action<string> recycle,
        Func<string, FileAttributes>? attributes = null)
    {
        if (!ProjectIdentity.IsWellFormed(projectId)) throw new ArgumentException("Invalid project identity.", nameof(projectId));
        ArgumentException.ThrowIfNullOrWhiteSpace(testFolder);
        ArgumentNullException.ThrowIfNull(recycle);
        _folder = Path.GetFullPath(testFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        _recycle = recycle;
        _attributes = attributes ?? File.GetAttributes;
        // A destination or an audio path must never become the ownership anchor.
        if (string.IsNullOrWhiteSpace(projectPath) || !File.Exists(projectPath)) return;
        _projectDirectory = ProjectFolderLayout.ProjectDirectory(projectPath);
        _ledgerPath = Path.Combine(_projectDirectory, ".dotcharter", "test-packages", projectId + ".json");
    }

    /// <summary>Refuses an existing destination without an unchanged receipt, before any platform is published.</summary>
    public Task BeforePublishAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var full = Candidate(path) ?? throw new RocksmithTestPackageOwnershipException("The test package is outside the selected test folder.");
        var exists = false;
        var directory = false;
        try
        {
            directory = (_attributes(full) & FileAttributes.Directory) != 0;
            exists = true;
        }
        catch (FileNotFoundException) { /* Positive absence, unlike File.Exists masking an access error. */ }
        catch (DirectoryNotFoundException) { /* The new destination's parent does not exist yet. */ }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw new RocksmithTestPackageOwnershipException("The test package destination could not be verified.", ex);
        }
        if (directory) throw new RocksmithTestPackageOwnershipException("The test package destination is a directory.");
        if (exists)
        {
            var stamps = ReadStamps();
            if (!stamps.TryGetValue(full, out var recorded) || HashOf(full) is not { } actual || actual != recorded)
                throw new RocksmithTestPackageOwnershipException("The test package destination is unowned, changed, or unreadable.");
        }
        _approved[full] = 0;
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Stamps only outputs whose publication was approved in this build. Cleanup follows a complete successful
    ///     publication and a durable project-owned receipt; a refused recycle keeps the old receipt for retry.
    /// </summary>
    public async Task<RocksmithBuildResult> BuildAndCleanUpAsync(Func<Task<RocksmithBuildResult>> build,
        IReadOnlyList<string> superseded)
    {
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(superseded);
        if (Interlocked.CompareExchange(ref _building, 1, 0) != 0) throw new InvalidOperationException("This test publication is already running.");
        try
        {
            _approved.Clear();
            var result = await build().ConfigureAwait(false);
            var published = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var stamps = ReadStamps();
            foreach (var path in result.PackagePaths)
            {
                var full = Candidate(path);
                // A returned pathname alone cannot adopt bytes that the builder never approved for publication.
                if (full == null || !_approved.ContainsKey(full) || HashOf(full) is not { } hash) return result;
                published.Add(full);
                stamps[full] = hash;
            }
            if (published.Count == 0) return result;
            if (_ledgerPath == null)
            {
                foreach (var path in published) _transient[path] = stamps[path];
                return result;
            }
            if (!TryWriteStamps(stamps)) return result;

            var changed = false;
            foreach (var path in superseded)
            {
                var full = Candidate(path);
                if (full == null || published.Contains(full) || !stamps.TryGetValue(full, out var hash) || HashOf(full) != hash)
                    continue;
                try { _recycle(full); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
                {
                    Console.Error.WriteLine($"Test package cleanup left '{Path.GetFileName(full)}' in place: {ex.Message}");
                    continue;
                }
                if (File.Exists(full) || Directory.Exists(full)) continue;
                changed |= stamps.Remove(full);
            }
            if (changed) TryWriteStamps(stamps);
            return result;
        }
        finally { Volatile.Write(ref _building, 0); }
    }

    private string? Candidate(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            return FileReferencePolicy.IsBareFileName(Path.GetFileName(full)) &&
                Path.GetExtension(full).Equals(".psarc", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Path.GetDirectoryName(full)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    _folder, StringComparison.OrdinalIgnoreCase) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return null; }
    }

    private Dictionary<string, string> ReadStamps()
    {
        if (_ledgerPath == null) return new Dictionary<string, string>(_transient, StringComparer.OrdinalIgnoreCase);
        var stamps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(_ledgerPath)) return stamps;
            var recorded = JsonSerializer.Deserialize<Dictionary<string, string>>(AtomicFile.ReadAllText(_ledgerPath));
            if (recorded == null) return stamps;
            foreach (var (path, hash) in recorded)
            {
                if (hash is { Length: 64 } && hash.All(char.IsAsciiHexDigitLower)) stamps[path] = hash;
            }
        }
        catch (Exception) { /* Missing or unreadable evidence never grants ownership. */ }
        return stamps;
    }

    private bool TryWriteStamps(Dictionary<string, string> stamps)
    {
        try
        {
            ProjectFolderLayout.MetadataDirectory(_projectDirectory!);
            Directory.CreateDirectory(Path.GetDirectoryName(_ledgerPath!)!);
            AtomicFile.WriteAllText(_ledgerPath!, JsonSerializer.Serialize(stamps));
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Test package ownership could not be saved; cleanup was skipped: {ex.Message}");
            return false;
        }
    }

    private static string? HashOf(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        catch (Exception) { return null; }
    }
}
