using DotCharter.Services.Audio.StemSeparation.Cache;
using DotCharter.Services.Gameplay.Profiles;

namespace DotCharter.Services.Audio.LivePlay.Takes;

/// <summary>
///     Owns the take files of the ACTIVE player profile: starts recorders
///     into the profile's <c>takes/</c> data directory, finalizes them
///     (Opus via the ffmpeg stem-encoder seam when available, WAV otherwise)
///     and cleans up orphaned / over-cap takes. Opt-in via
///     <see cref="AppConfig.LivePlayTakeRecorderEnabled"/> (default OFF;
///     Settings → Audio → "Listen back"). Play Mode plays takes back from the
///     results screen and the session history — see
///     <see cref="TakesDirectory"/> for how a stored name resolves to a file.
/// </summary>
public interface ILiveTakeStore
{
    /// <summary>True when the config flag enables take recording.</summary>
    bool IsEnabled { get; }

    /// <summary>The ACTIVE profile's takes directory. A stored
    /// <see cref="PracticeSessionRecord.TakeFile" /> is a bare file name
    /// relative to this — that is how listen-back resolves it back to a path.
    /// The file may be gone (the cleanup sweep enforces a size cap), so every
    /// consumer must tolerate a dangling name.</summary>
    string TakesDirectory { get; }

    /// <summary>Starts a new take recording, or null when disabled / no input.</summary>
    LiveTakeRecorder? TryStartTake(int sampleRate);

    /// <summary>Stops the recorder, encodes/persists the take and returns its
    /// file name (relative to the profile's takes directory), or null when the
    /// take was empty or persisting failed. Never throws.</summary>
    Task<string?> FinalizeTakeAsync(LiveTakeRecorder recorder);

    /// <summary>Deletes takes no session record references anymore and
    /// enforces the total-size cap (oldest first). Never throws.</summary>
    Task CleanupAsync();
}

public sealed class LiveTakeStore : ILiveTakeStore
{
    public const string TakesDirectoryName = "takes";

    private readonly Func<AppConfig> _config;
    private readonly IStemEncoder _encoder;
    private readonly string? _fixedDirectory;
    private readonly IAppLogger? _logger;
    private readonly IPlayerProfileService? _playerProfiles;
    private readonly IPracticeProfileService? _practiceProfiles;

    /// <summary>Profile-scoped store (production wiring).</summary>
    public LiveTakeStore(IPlayerProfileService playerProfiles, IPracticeProfileService practiceProfiles,
        IStemEncoder encoder, Func<AppConfig> config, IAppLogger? logger = null)
    {
        _playerProfiles = playerProfiles;
        _practiceProfiles = practiceProfiles;
        _encoder = encoder;
        _config = config;
        _logger = logger;
    }

    /// <summary>Fixed-directory store (tests, tools).</summary>
    public LiveTakeStore(string directory, IStemEncoder encoder, Func<AppConfig> config,
        IPracticeProfileService? practiceProfiles = null, IAppLogger? logger = null)
    {
        _fixedDirectory = directory;
        _practiceProfiles = practiceProfiles;
        _encoder = encoder;
        _config = config;
        _logger = logger;
    }

    public bool IsEnabled
    {
        get
        {
            try
            {
                return _config().LivePlayTakeRecorderEnabled;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>The active profile's takes directory (created on demand).</summary>
    public string TakesDirectory
    {
        get
        {
            if (_fixedDirectory != null) return _fixedDirectory;
            var profiles = _playerProfiles!;
            return Path.Combine(profiles.GetDataDirectory(profiles.ActiveProfile.Id), TakesDirectoryName);
        }
    }

    public LiveTakeRecorder? TryStartTake(int sampleRate)
    {
        if (!IsEnabled || sampleRate <= 0) return null;
        try
        {
            var config = _config();
            var maxMinutes = Math.Clamp(config.LivePlayTakeMaxMinutes, 1, 60);
            var name = $"take-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..38] + ".wav";
            var path = Path.Combine(TakesDirectory, name);
            return new LiveTakeRecorder(path, sampleRate, TimeSpan.FromMinutes(maxMinutes));
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Audio, $"Take recorder could not start: {ex.Message}");
            return null;
        }
    }

    public async Task<string?> FinalizeTakeAsync(LiveTakeRecorder recorder)
    {
        try
        {
            var result = await recorder.StopAsync().ConfigureAwait(false);
            if (result.SamplesWritten <= 0)
            {
                TryDelete(result.FilePath);
                return null;
            }

            if (result.WriteFailure != null)
            {
                _logger?.Warning(AppLogCategory.Audio,
                    $"Take is TRUNCATED at {result.DurationSeconds(recorder.SampleRate):0.0} s: " +
                    $"the writer failed ({result.WriteFailure.Message}).");
            }

            if (result.DroppedSamples > 0)
            {
                _logger?.Warning(AppLogCategory.Audio,
                    $"Take recorded with {result.DroppedSamples} dropped samples (writer starved).");
            }

            var finalPath = result.FilePath;
            if (_encoder.IsExternalEncoderAvailable)
            {
                var withoutExt = Path.Combine(
                    Path.GetDirectoryName(result.FilePath)!,
                    Path.GetFileNameWithoutExtension(result.FilePath));
                var encoded = await _encoder.EncodeAsync(result.FilePath, withoutExt, StemCodec.Opus)
                    .ConfigureAwait(false);
                if (!string.Equals(encoded, result.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(result.FilePath);
                    finalPath = encoded;
                }
            }

            _logger?.Info(AppLogCategory.Audio,
                $"Take saved: '{Path.GetFileName(finalPath)}' " +
                $"({result.DurationSeconds(recorder.SampleRate):0.0} s{(result.WasCapped ? ", capped" : "")}).");
            return Path.GetFileName(finalPath);
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Audio, $"Take could not be finalized: {ex.Message}");
            return null;
        }
        finally
        {
            recorder.Dispose();
        }
    }

    public Task CleanupAsync() => Task.Run(() =>
    {
        try
        {
            var dir = TakesDirectory;
            if (!Directory.Exists(dir)) return;

            // The sweep deletes on the strength of what the practice store does
            // NOT mention, so it is only safe against a store we actually READ.
            // A degraded load (IOException, sharing violation, an antivirus or
            // cloud-sync hold) hands back an EMPTY snapshot, and against that
            // every take on disk reads as orphaned — starred ones included, the
            // star being consulted only by the cap-eviction pass below. This runs
            // after every run and on every take delete, so one locked file would
            // have wiped the whole listen-back library.
            if (_practiceProfiles is { IsStoreAuthoritative: false })
            {
                _logger?.Warning(AppLogCategory.Audio,
                    "Take cleanup skipped: the practice profile store could not be read, so every take " +
                    "would look orphaned. Nothing was deleted; the next sweep retries.");
                return;
            }

            var (referenced, pinned) = CollectReferencedTakeFiles();
            var files = new List<TakeFileInfo>();
            foreach (var path in Directory.EnumerateFiles(dir))
            {
                var info = new FileInfo(path);
                files.Add(new TakeFileInfo(info.Name, info.Length, info.LastWriteTimeUtc));
            }

            long maxBytes;
            try
            {
                maxBytes = Math.Max(16, _config().LivePlayTakeCacheMaxMB) * 1024L * 1024L;
            }
            catch
            {
                maxBytes = 512L * 1024L * 1024L;
            }

            foreach (var name in SelectDeletions(files, referenced, maxBytes, pinned))
                TryDelete(Path.Combine(dir, name));
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Audio, $"Take cleanup failed: {ex.Message}");
        }
    });

    /// <summary>One take file on disk, as fed to <see cref="SelectDeletions"/>.</summary>
    public readonly record struct TakeFileInfo(string Name, long SizeBytes, DateTime LastWriteUtc);

    /// <summary>
    ///     Pure cleanup policy: every unreferenced (orphaned) file is deleted
    ///     (that includes re-amp renders whose take record was detached, and
    ///     interrupted <c>.tmp</c> writes); afterwards, if the referenced
    ///     remainder still exceeds <paramref name="maxTotalBytes"/>, the
    ///     OLDEST referenced takes go next until the total fits (their
    ///     session records keep a dangling name — listen-back consumers must
    ///     tolerate a missing file). Files in
    ///     <paramref name="pinnedFileNames"/> (starred takes + their re-amps,
    ///     v0.27) are NEVER evicted for the cap — the player's keepers
    ///     outrank the cache budget, so an all-starred store may sit over it.
    ///     <para>
    ///         <b>An empty reference set with files present deletes nothing.</b>
    ///         "No take is referenced" and "the store that names them could not be
    ///         read" are indistinguishable here, and the two outcomes are a few
    ///         stale files versus the player's whole listen-back library. The
    ///         caller checks <see cref="IPracticeProfileService.IsStoreAuthoritative" />
    ///         before ever getting here; this is the belt to that braces, and it
    ///         holds for every other caller too. Cost when the store really is
    ///         empty: the orphans wait for the first sweep that sees one
    ///         referenced take.
    ///     </para>
    /// </summary>
    public static IReadOnlyList<string> SelectDeletions(IReadOnlyList<TakeFileInfo> files,
        IReadOnlySet<string> referencedFileNames, long maxTotalBytes,
        IReadOnlySet<string>? pinnedFileNames = null)
    {
        if (referencedFileNames.Count == 0 && files.Count > 0) return [];

        var deletions = new List<string>();
        var kept = new List<TakeFileInfo>();
        foreach (var file in files)
        {
            if (referencedFileNames.Contains(file.Name)) kept.Add(file);
            else deletions.Add(file.Name);
        }

        var total = 0L;
        foreach (var file in kept) total += file.SizeBytes;
        if (total <= maxTotalBytes) return deletions;

        kept.Sort(static (a, b) => a.LastWriteUtc.CompareTo(b.LastWriteUtc));
        foreach (var file in kept)
        {
            if (total <= maxTotalBytes) break;
            if (pinnedFileNames?.Contains(file.Name) == true) continue;
            deletions.Add(file.Name);
            total -= file.SizeBytes;
        }

        return deletions;
    }

    /// <summary>Referenced = every session's take + attached re-amp renders;
    /// pinned = the same for STARRED sessions (cap-eviction exempt).</summary>
    private (IReadOnlySet<string> Referenced, IReadOnlySet<string> Pinned) CollectReferencedTakeFiles()
    {
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pinned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_practiceProfiles == null) return (referenced, pinned);
        foreach (var profile in _practiceProfiles.GetAll().Values)
        {
            foreach (var session in profile.Sessions)
            {
                if (string.IsNullOrEmpty(session.TakeFile)) continue;
                var starred = session.TakeStarred == true;
                referenced.Add(session.TakeFile);
                if (starred) pinned.Add(session.TakeFile);
                if (session.ReampFiles is not { Count: > 0 } reamps) continue;
                foreach (var reamp in reamps)
                {
                    if (string.IsNullOrEmpty(reamp)) continue;
                    referenced.Add(reamp);
                    if (starred) pinned.Add(reamp);
                }
            }
        }

        return (referenced, pinned);
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger?.Debug(AppLogCategory.Audio, $"Take file not deletable ({path}): {ex.Message}");
        }
    }
}
