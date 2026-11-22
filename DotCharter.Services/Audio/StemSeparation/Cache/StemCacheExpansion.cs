using System.Globalization;
using System.Security.Cryptography;

namespace DotCharter.Services.Audio.StemSeparation.Cache;

/// <summary>Publishes fully decoded sets and recognises a completed commit after
/// a crash left its pending marker behind. Incomplete sets stay hidden from recovery.</summary>
internal static class StemCacheExpansion
{
    internal const string PendingFileName = ".stemcache-publishing";
    internal const int BusyRetries = 40;
    private const int SharingViolation = unchecked((int)0x80070020);
    private const int LockViolation = unchecked((int)0x80070021);

    internal static readonly TimeSpan BusyRetryDelay = TimeSpan.FromMilliseconds(50);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> ReportedUnlockable
        = new(StringComparer.OrdinalIgnoreCase);

    internal enum Stage { PendingWritten, WavPublished, CompletionWritten, Completed }

    public static IReadOnlyList<StemTrack> Publish(IReadOnlyList<StemTrack> staged, string destination, string token,
        Action<Stage>? publishedHook = null)
    {
        using var exclusion = Acquire(destination);
        return PublishLocked(staged, destination, token, publishedHook);
    }

    /// <summary>The commit itself. The caller holds this folder's exclusion until it returns.</summary>
    internal static IReadOnlyList<StemTrack> PublishLocked(IReadOnlyList<StemTrack> staged, string destination, string token,
        Action<Stage>? publishedHook = null)
    {
        var pending = Path.Combine(destination, PendingFileName);
        var marker = Path.Combine(destination, StemCacheStore.ExpandedMarkerFileName);
        // Invalidate the prior commit before recording a new publication's proof.
        File.Delete(marker);
        var proof = new List<string>(staged.Count + 1) { token };
        proof.AddRange(staged.Select(stem => DescribeFile(stem.Path)));
        AtomicFile.WriteAllText(pending, string.Join("\n", proof));
        publishedHook?.Invoke(Stage.PendingWritten);
        var published = new List<StemTrack>(staged.Count);
        // No cancellation point inside this commit: cancellation before it publishes
        // nothing, and cancellation during it leaves a complete recoverable set.
        foreach (var stem in staged)
        {
            var path = Path.Combine(destination, Path.GetFileName(stem.Path));
            AtomicFile.Publish(stem.Path, path);
            published.Add(new StemTrack { Name = stem.Name, Path = path });
            publishedHook?.Invoke(Stage.WavPublished);
        }

        AtomicFile.WriteAllText(marker, token);
        publishedHook?.Invoke(Stage.CompletionWritten);
        File.Delete(pending);
        publishedHook?.Invoke(Stage.Completed);
        return published;
    }

    /// <summary>A matching completion marker was written LAST. New pending records
    /// also prove every WAV's length and hash; legacy token-only commits retain their
    /// original write-order contract. Verification runs on the mixer's worker.</summary>
    internal static bool CanRecover(string directory, Action? validated = null)
    {
        using var exclusion = TryAcquire(directory, out _);
        return exclusion != null && CanRecoverLocked(directory, validated);
    }

    /// <summary>The publisher's side: a probe holds the exclusion for a moment, or for
    /// seconds while it hashes a stale pending set, so a sharing violation is retried
    /// <see cref="BusyRetries" /> times. Every other failure, and a holder that outlasts
    /// the bound, surfaces unchanged.</summary>
    internal static async Task<FileStream> AcquireAsync(string directory,
        Func<TimeSpan, CancellationToken, Task>? delay, CancellationToken ct)
    {
        delay ??= Task.Delay;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return Acquire(directory);
            }
            catch (IOException ex) when (IsBusy(ex) && attempt < BusyRetries)
            {
                await delay(BusyRetryDelay, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>ERROR_SHARING_VIOLATION or ERROR_LOCK_VIOLATION: another handle owns the
    /// lock file right now. Nothing else means "busy".</summary>
    internal static bool IsBusy(Exception ex)
        => ex is IOException && ex.HResult is SharingViolation or LockViolation;

    /// <summary>The exclusion cannot be opened at all (a read-only folder, medium or lock
    /// file). The folder is then read without it: a pending marker still hides it,
    /// nothing is validated and nothing is deleted.</summary>
    internal static bool CanReadUnlocked(string directory, Exception failure, IAppLogger? logger)
    {
        if (failure is DirectoryNotFoundException) return false;
        // The reason only: the exception's message carries the folder's full path.
        if (ReportedUnlockable.TryAdd(directory, 0))
        {
            logger?.Warning(AppLogCategory.Audio,
                $"Stem folder exclusion unavailable ({failure.GetType().Name}, 0x{failure.HResult:X8}); reading the folder without it.");
        }

        return !File.Exists(Path.Combine(directory, PendingFileName));
    }

    // Keep this handle through the caller's WAV snapshot too. Never unlink the lock
    // file: independent processes must always open the same OS-owned exclusion.
    // An exclusive handle needs no write access, so an existing read-only lock file (a
    // copied folder keeps the attribute) is opened for reading, still unshared.
    internal static FileStream Acquire(string directory)
    {
        var path = Path.Combine(directory, ".stemcache-lock");
        try
        {
            return File.Open(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (UnauthorizedAccessException) when (File.Exists(path))
        {
            return File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
        }
    }

    /// <summary>Null with no <paramref name="failure" /> is BUSY: another handle holds the
    /// exclusion. Null with a failure means the lock file cannot be opened at all.</summary>
    internal static FileStream? TryAcquire(string directory, out Exception? failure)
    {
        failure = null;
        try
        {
            return Acquire(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!IsBusy(ex)) failure = ex;
            return null;
        }
    }

    internal static bool CanRecoverLocked(string directory, Action? validated = null)
    {
        var pending = Path.Combine(directory, PendingFileName);
        if (!File.Exists(pending)) return true;
        try
        {
            var proof = ReadProof(pending);
            if (proof.Length == 0) return false;
            var token = proof[0].Trim();
            if (token.Length != 32 || !token.All(Uri.IsHexDigit)) return false;
            var marker = Path.Combine(directory, StemCacheStore.ExpandedMarkerFileName);
            if (!File.Exists(marker) || AtomicFile.ReadAllText(marker).Trim() != token) return false;
            var complete = proof.Length == 1
                ? Directory.EnumerateFiles(directory, "*.wav", SearchOption.TopDirectoryOnly).Any()
                : proof.Skip(1).All(line => MatchesFile(directory, line));
            if (!complete) return false;
            validated?.Invoke();
            if (!proof.SequenceEqual(ReadProof(pending))) return false;
            BestEffortDelete.File(pending);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // The share-delete read: a plain File.Read* blocks a concurrent publisher's rename.
    private static string[] ReadProof(string pending)
    {
        var lines = new List<string>();
        using var reader = new StringReader(AtomicFile.ReadAllText(pending));
        while (reader.ReadLine() is { } line) lines.Add(line);
        return [.. lines];
    }

    private static string DescribeFile(string path)
        => string.Join("\t", Path.GetFileName(path), new FileInfo(path).Length.ToString(CultureInfo.InvariantCulture), HashFile(path));

    private static bool MatchesFile(string directory, string proof)
    {
        var parts = proof.Split('\t');
        if (parts.Length != 3) return false;
        var name = parts[0];
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        if (Path.GetFileName(name) != name || !name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) return false;
        if (!long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes)) return false;
        var path = Path.Combine(directory, name);
        var file = new FileInfo(path);
        if (!file.Exists || file.Length != bytes) return false;
        return string.Equals(HashFile(path), parts[2], StringComparison.OrdinalIgnoreCase);
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
