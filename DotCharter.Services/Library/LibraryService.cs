using System.Diagnostics;
using DotCharter.Services.Infrastructure.LibraryDoctor;
using DotCharter.Services.Library.Index;
using DotCharter.Services.Library.Prefetch;

namespace DotCharter.Services.Library;

/// <summary>
/// The song library: scans a folder for every registered
///     <see cref="ILibraryPackageSource" />'s files, serves unchanged files from the
///     SQLite index, parses the rest in parallel and hands each song to the source
///     that owns its extension to open, prepare, cover, block and probe. The scan
///     never knows a package format; a source never knows the index, the walk or
///     the song lists. Callers (Song Select, the Library screen, Play Mode,
///     onboarding, the convert-on-select prefetch, Library Doctor) see only
///     <see cref="ILibraryService" />, <see cref="ILibraryAudioPreparer" />,
///     <see cref="ILibrarySongPlayability" /> and <see cref="ILibraryPackageProbe" />.
///     Duplicate rows are folded by the song lists through <see cref="LibraryDedupe" />.
/// </summary>
public sealed class LibraryService : ILibraryService, ILibraryAudioPreparer, ILibrarySongPlayability, ILibraryPackageProbe
{
    private readonly IAppLogger _logger;
    private readonly ILibraryIndex? _libraryIndex;
    private readonly IReadOnlyList<ILibraryPackageSource> _sources;

    /// <param name="logger">The log sink.</param>
    /// <param name="sources">Every package source, in scan order. The app registers
    /// <c>.psarc</c> before <c>.dcsong</c>, the order the scan always listed them in.</param>
    /// <param name="libraryIndex">Optional: without it every scan parses every file.</param>
    public LibraryService(IAppLogger logger, IEnumerable<ILibraryPackageSource> sources, ILibraryIndex? libraryIndex = null)
    {
        _logger = logger;
        _libraryIndex = libraryIndex;
        _sources = [.. sources];
    }

    /// <summary>The registered sources, in scan order.</summary>
    public IReadOnlyList<ILibraryPackageSource> Sources => _sources;

    /// <summary>Test seam: the cold scan's parser count; unset, half the
    /// logical processors capped at <see cref="LibraryScanParallelParse.MaxDegree" />.</summary>
    internal int? ScanParallelismForTests { get; init; }

    /// <summary>Test seam: wraps each cold-scan parse ON its parser thread —
    /// the path and the real parse — so a test can reorder completions, count
    /// concurrent parses or fail one archive.</summary>
    internal Func<string, Func<IReadOnlyList<LibrarySongEntry>>, IReadOnlyList<LibrarySongEntry>>? ScanParseInterceptorForTests { get; init; }

    private int ScanDegreeOfParallelism =>
        ScanParallelismForTests ?? LibraryScanParallelParse.DegreeFor(Environment.ProcessorCount);

    public Task<IReadOnlyList<LibrarySongEntry>> ScanAsync(string rootPath)
        => ScanAsync(rootPath, progress: null, songFound: null, cancellationToken: CancellationToken.None);

    public Task<IReadOnlyList<LibrarySongEntry>> ScanAsync(
        string rootPath,
        IProgress<LibraryScanProgress>? progress,
        CancellationToken cancellationToken)
        => ScanAsync(rootPath, progress, songFound: null, cancellationToken);

    public Task<IReadOnlyList<LibrarySongEntry>> ScanAsync(
        string rootPath,
        IProgress<LibraryScanProgress>? progress,
        IProgress<IReadOnlyList<LibrarySongEntry>>? songFound,
        CancellationToken cancellationToken)
    {
        return Task.Run<IReadOnlyList<LibrarySongEntry>>(() =>
        {
            if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
                return [];

            // Materialize the file list up front so we can report a real
            // total. EnumerateFiles is lazy but we need the count for a
            // meaningful progress fraction; the scan itself dominates the
            // wall-clock anyway.
            var files = LibraryArchiveFiles.Enumerate(rootPath, _sources.SelectMany(static s => s.FileExtensions));
            foreach (var source in _sources) source.BeginScan();

            // One slot per FILE, filled by whichever stage resolves it; the result
            // is read back in file order, so its tie order never depends on which
            // parser finished first.
            var perFile = new IReadOnlyList<LibrarySongEntry>?[files.Length];
            var songsFound = 0;
            progress?.Report(new LibraryScanProgress(0, files.Length, "", 0));

            // Best-effort library index: files whose (size, mtime) still match
            // their indexed values are served straight from SQLite instead of
            // being re-parsed, which makes every scan after the first near
            // instant. BeginScan returning null (or any per-file cache miss /
            // failure) falls back to the pre-0.12 full-parse path.
            using var index = TryBeginIndexScan();
            index?.PurgeMissing(rootPath, files);

            // Cache hits are coalesced into larger songFound batches: a warm
            // scan produces hits far faster than the UI wants individual
            // per-file notifications (thousands of Progress posts would
            // flood the dispatcher that batching exists to protect).
            var cachedBatch = new List<LibrarySongEntry>();
            void FlushCachedBatch()
            {
                if (cachedBatch.Count == 0) return;
                songFound?.Report(cachedBatch.ToArray());
                cachedBatch.Clear();
            }

            // Progress is COALESCED, for the same reason songFound is batched
            // one block above: `progress` is a Progress<T> built on the UI
            // thread, so every Report is a dispatcher work item that runs a
            // LocalizationService.Format plus two PropertyChanged raises. One
            // per file meant 1,825 of them — pushed at the UI thread during a
            // scan that on the boot path runs while the window is still
            // settling. A warm scan finishes in ~330 ms, so per-file reports
            // were also invisible: nobody can read 1,825 filenames in a third
            // of a second. ~30 Hz keeps a COLD scan feeling live and collapses a
            // warm one to ~10 posts.
            var lastProgress = Stopwatch.StartNew();
            var processed = 0;
            void FileDone(string path)
            {
                // The LAST file always reports, so the bar always reaches 100%.
                processed++;
                if (progress == null || (processed < files.Length && lastProgress.ElapsedMilliseconds < 33)) return;
                lastProgress.Restart();
                progress.Report(new LibraryScanProgress(processed, files.Length, path, songsFound));
            }

            // Stage 1, sequential: stat + index lookup (fast — one bulk read).
            // The misses are collected for stage 2.
            var misses = new List<(int FileIndex, bool StatKnown, long Size, long MtimeTicks)>();
            var servedFromIndex = 0;
            for (var i = 0; i < files.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var path = files[i];

                long fileSize = 0, mtimeUtcTicks = 0;
                var statKnown = false;
                try
                {
                    var info = new FileInfo(path);
                    if (info.Exists)
                    {
                        fileSize = info.Length;
                        mtimeUtcTicks = info.LastWriteTimeUtc.Ticks;
                        statKnown = true;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Trace(AppLogCategory.Import,
                        $"Library file stat failed; skipping index for '{path}'. Message='{ex.Message}'.");
                }

                if (index != null && statKnown &&
                    index.TryGetCachedSongs(path, fileSize, mtimeUtcTicks, out var cached))
                {
                    servedFromIndex++;
                    perFile[i] = cached;
                    songsFound += cached.Count;
                    if (cached.Count > 0)
                    {
                        cachedBatch.AddRange(cached);
                        if (cachedBatch.Count >= 256) FlushCachedBatch();
                    }

                    FileDone(path);
                }
                else
                {
                    misses.Add((i, statKnown, fileSize, mtimeUtcTicks));
                }
            }

            FlushCachedBatch();

            if (servedFromIndex > 0)
            {
                _logger.Info(AppLogCategory.Import,
                    $"Library index served {servedFromIndex}/{files.Length} file(s) from cache. Root='{rootPath}'.");
            }

            // Stage 2: the misses are parsed in parallel and committed by
            // THIS thread — the one index writer — in file order.
            if (misses.Count > 0)
            {
                var degree = ScanDegreeOfParallelism;
                var parseClock = Stopwatch.StartNew();
                LibraryScanParallelParse.Run(
                    misses.Select(m => files[m.FileIndex]).ToArray(), degree, ParseForScan,
                    (missIndex, outcome) =>
                    {
                        var miss = misses[missIndex];
                        var path = files[miss.FileIndex];
                        if (outcome.Error is { } ex)
                        {
                            _logger.Warning(AppLogCategory.Import,
                                $"Library scan skipped '{path}'. Type={ex.GetType().Name}, message='{ex.Message}'.");
                        }
                        else
                        {
                            var entries = outcome.Songs;
                            perFile[miss.FileIndex] = entries;
                            songsFound += entries.Count;
                            // Cache even empty archives so they aren't re-parsed
                            // every scan. Transactional per file, and BEFORE
                            // songFound: a cancel from that callback leaves this
                            // file whole in the index.
                            if (miss.StatKnown) UpsertBestEffort(index, path, miss.Size, miss.MtimeTicks, entries);
                            // Songs appear progressively, without waiting for the
                            // whole library scan.
                            if (entries.Count > 0) songFound?.Report(entries);
                        }

                        FileDone(path);
                    },
                    cancellationToken);

                _logger.Info(AppLogCategory.Import,
                    $"Library scan parsed {misses.Count}/{files.Length} file(s) on up to {degree} thread(s) in {parseClock.ElapsedMilliseconds} ms. Root='{rootPath}'.");
            }

            return perFile
                .SelectMany(songs => songs ?? [])
                .OrderBy(song => song.Artist, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(song => song.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }, cancellationToken);
    }

    /// <summary>Opens a library song through the source that owns its file.</summary>
    public Task<LibraryLoadedSong> LoadSongAsync(LibrarySongEntry song) =>
        SourceFor(song.PackagePath) is { } source
            ? source.LoadSongAsync(song)
            : Task.FromException<LibraryLoadedSong>(new NotSupportedException(
                $"No library package source reads '{song.PackagePath}'."));

    /// <summary>The cover PNG, "" or the row's own path when it names no cover entry.</summary>
    public Task<string> LoadCoverImageAsync(LibrarySongEntry song) =>
        Task.Run(() =>
            string.IsNullOrWhiteSpace(song.PackagePath) || string.IsNullOrWhiteSpace(song.CoverImageEntryPath) ||
            SourceFor(song.PackagePath) is not { } source
                ? song.CoverImagePath
                : source.LoadCover(song));

    /// <inheritdoc />
    public LibraryAudioPrepareOutcome PrepareAudio(LibrarySongEntry song, CancellationToken cancellationToken) =>
        SourceFor(song.PackagePath)?.PrepareAudio(song, cancellationToken) ?? LibraryAudioPrepareOutcome.NoAudio;

    /// <inheritdoc />
    public LibrarySongBlock BlockFor(LibrarySongEntry song) =>
        SourceFor(song.PackagePath)?.BlockFor(song) ?? LibrarySongBlock.None;

    /// <inheritdoc />
    public PackageProbeResult Probe(string path) =>
        SourceFor(path)?.Probe(path)
        ?? new PackageProbeResult(false, AudioAvailability.Unknown, "No library package source reads this file.");

    /// <summary>The source whose extension <paramref name="path" /> ends in, or null.</summary>
    internal ILibraryPackageSource? SourceFor(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        foreach (var source in _sources)
        {
            foreach (var extension in source.FileExtensions)
            {
                if (path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) return source;
            }
        }

        return null;
    }

    private IReadOnlyList<LibrarySongEntry> ParseForScan(string path) =>
        ScanParseInterceptorForTests is { } intercept
            ? intercept(path, () => ReadSongs(path))
            : ReadSongs(path);

    private IReadOnlyList<LibrarySongEntry> ReadSongs(string path) =>
        SourceFor(path) is { } source
            ? source.ReadForIndex(path)
            : throw new NotSupportedException($"No library package source reads '{path}'.");

    /// <summary>The index is a cache: a failed write costs a re-parse next scan, never the songs.</summary>
    private void UpsertBestEffort(ILibraryIndexScan? index, string path, long size, long mtimeTicks,
        IReadOnlyList<LibrarySongEntry> songs)
    {
        try
        {
            index?.UpsertFile(path, size, mtimeTicks, songs);
        }
        catch (Exception ex)
        {
            _logger.Warning(AppLogCategory.Import,
                $"Library index write failed for '{path}'. Type={ex.GetType().Name}, message='{ex.Message}'.");
        }
    }

    /// <summary>
    /// The index is a cache and must never fail a scan: even an exception
    /// out of <see cref="ILibraryIndex.BeginScan"/> just means "scan
    /// without a cache this time".
    /// </summary>
    private ILibraryIndexScan? TryBeginIndexScan()
    {
        try
        {
            return _libraryIndex?.BeginScan();
        }
        catch (Exception ex)
        {
            _logger.Warning(AppLogCategory.Import,
                $"Library index unavailable; scanning without cache. Type={ex.GetType().Name}, message='{ex.Message}'.");
            return null;
        }
    }
}
