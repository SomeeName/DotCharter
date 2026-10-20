using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DotCharter.Services.Library.Cache;

/// <summary>What the PSARC audio cache currently occupies on disk.</summary>
/// <param name="Songs">Number of cached folders (one per archive revision).</param>
/// <param name="Bytes">Total size of everything under them.</param>
public readonly record struct LibraryAudioCacheUsage(int Songs, long Bytes)
{
    public static readonly LibraryAudioCacheUsage Empty = new(0, 0);
}

/// <summary>
/// The one owner of <c>%LocalAppData%/DotCharter/PsarcAudioCache</c>: it names
/// the folders, records what each one came from, measures the whole thing, and
/// is the ONLY thing that removes anything from it.
/// <para>
/// Layout is unchanged from before this type existed —
/// <c>&lt;root&gt;/{psarcName}_{pathHash16}_{lastWriteTicks}/</c> holding the
/// extracted <c>.wem</c> and whatever the converter derived from it — so every
/// folder already on disk still HITS. What is new is
/// <see cref="SourceMarkerFileName"/> inside each folder, rewritten on every use:
/// its text is the archive that produced the folder (which makes orphan
/// reclamation decidable) and its write time is the last-use stamp the LRU sorts
/// on.
/// </para>
/// <para>
/// <b>The marker carries the last-use stamp because the FILESYSTEM's does not.</b>
/// Windows ships with last-access-time updates disabled by default
/// (<c>NtfsDisableLastAccessUpdate</c>), so <c>Directory.GetLastAccessTimeUtc</c>
/// on a cache hit returns whatever it returned last month. A rewritten file's
/// write time always moves.
/// </para>
/// <para>
/// <b>Everything here is best-effort per item, exactly like
/// <c>StemCacheStore</c>:</b> a file the audio engine still holds open is skipped
/// and left for the next sweep rather than failing the whole operation. A folder
/// that ends up half-deleted is not a corruption — the extractor re-derives any
/// missing piece on the next load (see <c>AudioFormatConverter.IsFreshUsableAudioFile</c>),
/// which is the whole reason this cache may evict at all.
/// </para>
/// </summary>
public sealed class LibraryAudioCacheStore
{
    /// <summary>
    /// Default ceiling: <b>8 GiB</b>.
    /// <para>
    /// Measured from the machine that motivated this: 127.6 GB across 2,543
    /// folders is ~50 MB per song, which is what a decoded 4-minute 48 kHz
    /// stereo WAV plus its intermediates cost. 8 GiB is therefore ~160 songs of
    /// instant re-open — far more than any one session revisits — while a miss
    /// costs a single decode of an archive the user still has. The number is
    /// deliberately NOT a fraction of the volume: "10% of the drive" on the
    /// owner's 930 GB C: would authorise 93 GB, and 127 GB of decoded WAV is
    /// indefensible on a 930 GB drive too. It is an absolute budget because what
    /// it buys is bounded — nobody replays 160 different songs before the cache
    /// pays for itself again.
    /// </para>
    /// </summary>
    public const long DefaultMaxBytes = 8L * 1024 * 1024 * 1024;

    /// <summary>Written into every cache folder on every use. Holds the absolute
    /// path of the archive the folder was derived from; its LAST WRITE TIME is
    /// the last-use stamp. Dot-prefixed so it never collides with an extracted
    /// entry name.</summary>
    internal const string SourceMarkerFileName = ".psarcsource";

    private readonly string _root;
    private readonly IAppLogger? _logger;
    private readonly LibraryAudioCacheEvictionPlanner _planner;
    private int _sweeping;

    /// <summary>
    /// <c>enableAutomaticSweep</c> decides whether <see cref="ScheduleSweep"/> may
    /// DELETE. Default <c>false</c>, and that default is load-bearing rather than
    /// cautious boilerplate.
    /// <para>
    /// <b>This shipped the wrong way round for one test run and cost the owner
    /// 119 GB.</b> the package source falls back to a store over the
    /// REAL <c>%LocalAppData%</c> root when nobody hands it one, and
    /// <c>RocksmithPackageBuilderTests</c> builds a <c>goldentest_p.psarc</c> in a
    /// temp folder and loads it through exactly that constructor — so a unit test
    /// pruned a live cache down to the cap. Writing to the real root was always
    /// harmless (extraction has always done it); DELETING from it is not, and a
    /// destructive default that only the composition root ever wanted is a
    /// destructive default in every other caller too.
    /// </para>
    /// <para>
    /// So automatic eviction is something the APP opts into
    /// (<c>App.EditingServices.cs</c>), not a side effect of constructing a
    /// parser. <see cref="Sweep"/> itself stays callable — an explicit call is an
    /// explicit decision, and the tests use it.
    /// </para>
    /// </summary>
    public LibraryAudioCacheStore(
        IAppLogger? logger = null,
        string? rootOverride = null,
        long maxBytes = DefaultMaxBytes,
        LibraryAudioCacheEvictionPlanner? planner = null,
        bool enableAutomaticSweep = false)
    {
        _logger = logger;
        _root = rootOverride ?? DefaultRoot();
        MaxBytes = maxBytes;
        _planner = planner ?? new LibraryAudioCacheEvictionPlanner();
        AutomaticSweepEnabled = enableAutomaticSweep;
    }

    /// <summary>Root directory of the cache (created lazily on first use).</summary>
    public string Root => _root;

    /// <summary>The size ceiling <see cref="Sweep"/> works down to.</summary>
    public long MaxBytes { get; }

    /// <summary>Whether <see cref="ScheduleSweep"/> does anything. See the
    /// constructor's <c>enableAutomaticSweep</c> remarks — only the composition
    /// root turns this on.</summary>
    public bool AutomaticSweepEnabled { get; }

    internal static string DefaultRoot()
    {
        var localAppData = UserDataFolders.LocalAppData;
        if (string.IsNullOrWhiteSpace(localAppData)) localAppData = Path.GetTempPath();
        return Path.Combine(localAppData, "DotCharter", "PsarcAudioCache");
    }

    /// <summary>
    /// The folder name an archive maps to. Byte-for-byte the scheme
    /// the package source has always used, so
    /// folders written by older builds still hit — and, because the last-write
    /// ticks are IN the name, an archive that is updated or re-downloaded starts
    /// a new folder and permanently orphans the old one. That orphaning is the
    /// defect this store exists to clean up after, not a bug to fix here: the
    /// timestamp is also what makes a stale decode impossible.
    /// </summary>
    public static string DirectoryNameFor(string psarcPath)
    {
        var psarcName = SafeFileName.Make(Path.GetFileNameWithoutExtension(psarcPath));
        var timestamp = File.Exists(psarcPath)
            ? File.GetLastWriteTimeUtc(psarcPath).Ticks.ToString(CultureInfo.InvariantCulture)
            : "unknown";
        var pathHash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(Path.GetFullPath(psarcPath).ToUpperInvariant())))[..16];
        return $"{psarcName}_{pathHash}_{timestamp}";
    }

    /// <summary>Absolute path of the folder that holds <paramref name="psarcPath"/>'s audio.</summary>
    public string DirectoryFor(string psarcPath) => Path.Combine(_root, DirectoryNameFor(psarcPath));

    /// <summary>True when <paramref name="path"/> is a file inside one of this cache's
    /// folders. A song is only ever converted to the device rate there
    /// (<see cref="LibraryDeviceRateAudio"/>), never a user's own audio.</summary>
    public bool Contains(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_root)) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(path);
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                   && full.IndexOf(Path.DirectorySeparatorChar, root.Length) > root.Length;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// Creates (or re-stamps) the folder for an archive and returns it. Called on
    /// every extraction, HIT OR MISS — a cache hit is a use, and an LRU that only
    /// counted misses would evict exactly the songs the user plays most.
    /// </summary>
    public string PrepareForUse(string psarcPath)
    {
        var directory = DirectoryFor(psarcPath);
        Directory.CreateDirectory(directory);
        TouchMarker(directory, psarcPath);
        return directory;
    }

    private void TouchMarker(string directory, string psarcPath)
    {
        try
        {
            File.WriteAllText(Path.Combine(directory, SourceMarkerFileName), Path.GetFullPath(psarcPath));
        }
        catch (Exception ex)
        {
            // A missing marker only downgrades the folder to Unknown: it is still
            // measured and still LRU-evictable, it just cannot be proven orphaned.
            _logger?.Trace(AppLogCategory.Import,
                $"PSARC audio cache: could not stamp '{directory}': {ex.Message}");
        }
    }

    /// <summary>Measures the cache. A directory walk — cheap enough for a
    /// settings page, and safe to call from anywhere.</summary>
    public LibraryAudioCacheUsage MeasureUsage()
    {
        try
        {
            if (!Directory.Exists(_root)) return LibraryAudioCacheUsage.Empty;
            var count = 0;
            long bytes = 0;
            foreach (var directory in Directory.EnumerateDirectories(_root))
            {
                bytes += DirectorySize(directory);
                count++;
            }

            return new LibraryAudioCacheUsage(count, bytes);
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Import, $"PSARC audio cache measure failed: {ex.Message}");
            return LibraryAudioCacheUsage.Empty;
        }
    }

    /// <summary>
    /// Deletes every cached folder and returns how many went. The user-facing
    /// reclaim button; the app's own housekeeping goes through <see cref="Sweep"/>.
    /// <para>
    /// Best-effort per folder: one whose audio the engine currently holds open is
    /// skipped and left for the next attempt rather than failing the whole clear.
    /// </para>
    /// </summary>
    public int Clear()
    {
        var deleted = 0;
        try
        {
            if (!Directory.Exists(_root)) return 0;
            foreach (var directory in Directory.EnumerateDirectories(_root).ToList())
            {
                if (TryDeleteDirectory(directory))
                    deleted++;
            }
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Import, $"PSARC audio cache clear failed: {ex.Message}");
        }

        if (deleted > 0)
        {
            _logger?.Info(AppLogCategory.Import,
                $"PSARC audio cache cleared: {deleted} folder(s) deleted from '{_root}'.");
        }

        return deleted;
    }

    /// <summary>
    /// Reclaims orphans, then evicts least-recently-used folders until the cache
    /// is back under <see cref="MaxBytes"/>. Returns the plan that was APPLIED
    /// (best-effort deletes mean the bytes actually freed can be lower).
    /// </summary>
    /// <param name="protectedDirectoryName">Folder for the song being loaded
    /// right now — never a candidate. See
    /// <see cref="LibraryAudioCacheEvictionPlanner.Plan"/>.</param>
    public LibraryAudioCacheEvictionPlan Sweep(string? protectedDirectoryName = null)
    {
        try
        {
            if (!Directory.Exists(_root)) return LibraryAudioCacheEvictionPlan.Empty;

            var entries = ScanEntries();
            var plan = _planner.Plan(entries, MaxBytes, protectedDirectoryName);
            if (plan.IsEmpty) return plan;

            var deleted = 0;
            foreach (var entry in plan.All)
            {
                if (TryDeleteDirectory(Path.Combine(_root, entry.Name)))
                    deleted++;
            }

            _logger?.Info(AppLogCategory.Import,
                $"PSARC audio cache swept: {plan.Orphans.Count} orphan(s) + {plan.Evicted.Count} " +
                $"least-recently-used folder(s) targeted, {deleted} removed, " +
                $"{plan.BytesBefore:N0} -> {plan.BytesAfter:N0} bytes (cap {MaxBytes:N0}).");
            return plan;
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Import, $"PSARC audio cache sweep failed: {ex.Message}");
            return LibraryAudioCacheEvictionPlan.Empty;
        }
    }

    /// <summary>
    /// Runs <see cref="Sweep"/> off the calling thread, at most one at a time.
    /// Song loading calls this after every extraction: the scan is a directory
    /// walk and the deletes can be tens of GB on a cache nobody has ever pruned,
    /// neither of which may land on the thread opening a song.
    /// </summary>
    public void ScheduleSweep(string? protectedDirectoryName = null)
    {
        // A store nobody opted in is a MEASURE-and-CLEAR store, not a pruning one.
        if (!AutomaticSweepEnabled) return;
        // Interlocked rather than a lock: a second load arriving mid-sweep should
        // SKIP, not queue — the sweep it would have run has already seen its
        // folder (the caller stamped the marker before extracting).
        if (Interlocked.CompareExchange(ref _sweeping, 1, 0) != 0) return;
        _ = Task.Run(() =>
        {
            try { Sweep(protectedDirectoryName); }
            finally { Volatile.Write(ref _sweeping, 0); }
        });
    }

    /// <summary>
    /// Reads every folder under the root into the pure planner's vocabulary.
    /// Internal so the reachability rules can be tested against a real temp tree
    /// without going through a song load.
    /// </summary>
    internal IReadOnlyList<LibraryAudioCacheEntry> ScanEntries()
    {
        if (!Directory.Exists(_root)) return [];

        var scanned = new List<(string Name, long Bytes, DateTime LastUsed, string? Source)>();
        foreach (var directory in Directory.EnumerateDirectories(_root))
        {
            var name = Path.GetFileName(directory);
            if (string.IsNullOrEmpty(name)) continue;
            var marker = Path.Combine(directory, SourceMarkerFileName);
            var source = ReadMarker(marker);
            var lastUsed = SafeTimestamp(
                () => source != null ? File.GetLastWriteTimeUtc(marker) : Directory.GetLastWriteTimeUtc(directory));
            scanned.Add((name, DirectorySize(directory), lastUsed, source));
        }

        // Pass 1: what the folder itself claims. A marker naming an archive that
        // is gone, or that now maps to a different folder name (its timestamp
        // moved), is proof this folder is unreachable.
        var reach = new Dictionary<string, LibraryAudioCacheReachability>(StringComparer.Ordinal);
        foreach (var (name, _, _, source) in scanned)
            reach[name] = ClassifyBySource(name, source);

        // Pass 2: supersession, which needs no marker at all and so is the only
        // signal that reaches folders written before this type existed. Two
        // folders sharing a path hash are two revisions of ONE archive, and an
        // archive has exactly one current mtime — so at most one of them can ever
        // be named again. Keep the highest ticks (a restored-from-backup archive
        // could in principle carry an older mtime, which costs one decode) and
        // orphan the rest. A folder already proven Reachable by its marker wins
        // over this heuristic.
        foreach (var group in scanned
                     .Select(s => (s.Name, Parsed: TryParseName(s.Name, out var hash, out var ticks), Hash: hash, Ticks: ticks))
                     .Where(s => s.Parsed)
                     .GroupBy(s => s.Hash, StringComparer.Ordinal)
                     .Where(g => g.Count() > 1))
        {
            var newest = group.Max(g => g.Ticks);
            foreach (var member in group)
            {
                if (member.Ticks == newest) continue;
                if (reach[member.Name] == LibraryAudioCacheReachability.Reachable) continue;
                reach[member.Name] = LibraryAudioCacheReachability.Orphaned;
            }
        }

        return [.. scanned.Select(s => new LibraryAudioCacheEntry(s.Name, s.Bytes, s.LastUsed, reach[s.Name]))];
    }

    private static LibraryAudioCacheReachability ClassifyBySource(string directoryName, string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return LibraryAudioCacheReachability.Unknown;
        try
        {
            if (!File.Exists(source)) return LibraryAudioCacheReachability.Orphaned;
            return string.Equals(DirectoryNameFor(source), directoryName, StringComparison.OrdinalIgnoreCase)
                ? LibraryAudioCacheReachability.Reachable
                : LibraryAudioCacheReachability.Orphaned;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // An unreadable or malformed path is not evidence of death.
            return LibraryAudioCacheReachability.Unknown;
        }
    }

    /// <summary>
    /// Splits <c>{psarcName}_{pathHash16}_{ticks}</c> from the RIGHT — archive
    /// names contain underscores far more often than not, so parsing forwards
    /// finds the wrong fields.
    /// </summary>
    internal static bool TryParseName(string directoryName, out string pathHash, out long ticks)
    {
        pathHash = "";
        ticks = 0;

        var lastUnderscore = directoryName.LastIndexOf('_');
        if (lastUnderscore <= 0) return false;
        // "unknown" is what the writer stamps for an archive it could not stat;
        // such a folder is not a revision of anything and must not group.
        if (!long.TryParse(directoryName.AsSpan(lastUnderscore + 1), NumberStyles.None,
                CultureInfo.InvariantCulture, out ticks))
        {
            return false;
        }

        var hashUnderscore = directoryName.LastIndexOf('_', lastUnderscore - 1);
        if (hashUnderscore < 0) return false;

        var candidate = directoryName[(hashUnderscore + 1)..lastUnderscore];
        if (candidate.Length != 16) return false;
        foreach (var c in candidate)
        {
            if (!Uri.IsHexDigit(c))
                return false;
        }

        pathHash = candidate;
        return true;
    }

    private static string? ReadMarker(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path).Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static DateTime SafeTimestamp(Func<DateTime> read)
    {
        try { return read(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable stamp: treat as ancient so it sorts to the front of the
            // LRU rather than becoming un-evictable.
            return DateTime.MinValue;
        }
    }

    private static long DirectorySize(string directory)
    {
        long bytes = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                try { bytes += new FileInfo(file).Length; }
                catch (IOException) { /* vanished between listing and stat */ }
                catch (UnauthorizedAccessException) { /* ditto */ }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Partial size is better than none: it still ranks the folder.
        }

        return bytes;
    }

    private bool TryDeleteDirectory(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return false;
            Directory.Delete(directory, recursive: true);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Import,
                $"PSARC audio cache skipped '{Path.GetFileName(directory)}': {ex.Message}");
            return false;
        }
    }
}
