using System.Diagnostics;

namespace DotCharter.Services.Library.Prefetch;

/// <summary>
/// Convert-on-select. A first open of a library song spends ~0.9 s
///     extracting and converting its WEM; a repeat open finds the cached WAV in
///     ~50 ms. This moves the first one into the pause between selecting a song
///     and opening it: <see cref="SettleDelay" /> after the selection stops
///     moving, the song is prepared on the thread pool, one at a time, and a new
///     selection cancels it. Open joins a prefetch already converting its song
///     (the per-folder gate in the package source's audio extract).
/// </summary>
public sealed class LibraryAudioPrefetcher : ILibraryAudioPrefetcher, IDisposable
{
    /// <summary>Long enough that keyboard-scrolling through the list prepares
    /// nothing; short next to the time a user takes to click Open.</summary>
    public static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(400);

    private readonly ILibraryAudioPreparer _preparer;
    private readonly IAppLogger _logger;
    private readonly SettledSelectionRunner<LibrarySongEntry> _runner;

    public LibraryAudioPrefetcher(ILibraryAudioPreparer preparer, IAppLogger logger)
        : this(preparer, logger, static (delay, ct) => Task.Delay(delay, ct))
    {
    }

    internal LibraryAudioPrefetcher(ILibraryAudioPreparer preparer, IAppLogger logger,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        _preparer = preparer;
        _logger = logger;
        _runner = new SettledSelectionRunner<LibrarySongEntry>(
            SettleDelay,
            delay,
            (song, ct) => Task.Run(() => Prefetch(song, ct), ct),
            (song, ex) => _logger.Warning(AppLogCategory.Import,
                $"PSARC audio prefetch failed. Song='{song.SongKey}', psarc='{song.PackagePath}'.", ex));
    }

    public void SelectionChanged(LibrarySongEntry? song) => _ = _runner.Select(song);

    /// <summary>The run behind the most recent <see cref="SelectionChanged" />; tests await it.</summary>
    internal Task SelectionChangedForTests(LibrarySongEntry? song) => _runner.Select(song);

    private void Prefetch(LibrarySongEntry song, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var outcome = _preparer.PrepareAudio(song, cancellationToken);
            var line = $"PSARC audio prefetch finished. Song='{song.SongKey}', outcome={outcome}, ms={stopwatch.ElapsedMilliseconds}.";
            if (outcome == LibraryAudioPrepareOutcome.AlreadyPrepared)
                _logger.Debug(AppLogCategory.Import, line);
            else
                _logger.Info(AppLogCategory.Import, line);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.Debug(AppLogCategory.Import,
                $"PSARC audio prefetch cancelled. Song='{song.SongKey}', ms={stopwatch.ElapsedMilliseconds}.");
            throw;
        }
    }

    public void Dispose() => _runner.Dispose();
}
