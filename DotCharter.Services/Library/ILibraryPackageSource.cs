using DotCharter.Services.Infrastructure.LibraryDoctor;
using DotCharter.Services.Library.Prefetch;

namespace DotCharter.Services.Library;

/// <summary>
/// One kind of package file the song library reads: <c>.dcsong</c>
///     (<c>DcSongLibrarySource</c>) and Rocksmith <c>.psarc</c> (the package source).
///     <see cref="LibraryService" /> owns everything neutral — the walk, the SQLite index,
///     the parallel parse, progress and the song lists — and asks the source that owns
///     a file's extension for the rest. Nothing above the Library talks to a source.
///     <para>
///         A source answers only for paths ending in one of its <see cref="FileExtensions" />;
///         <see cref="LibraryService" /> never hands it anything else.
///     </para>
/// </summary>
public interface ILibraryPackageSource
{
    /// <summary>The extensions this source reads, with the dot (<c>.psarc</c>). The scan
    /// lists the files of each source in registration order, then each extension in this
    /// order; <see cref="LibrarySongEntry.SourceKind" /> stays the extension.</summary>
    IReadOnlyList<string> FileExtensions { get; }

    /// <summary>Called once at the start of every scan, off the UI thread, before any
    /// <see cref="ReadForIndex" /> (a source probes what its rows' block reasons need).</summary>
    void BeginScan();

    /// <summary>The library rows of the package at <paramref name="path" />, for the index.
    /// Runs on a scan parser thread, several at once; throws for a package it cannot read
    /// (the scan logs it as skipped and goes on).</summary>
    IReadOnlyList<LibrarySongEntry> ReadForIndex(string path);

    /// <summary>Opens a library song to play or chart: the chart and its playable audio.</summary>
    Task<LibraryLoadedSong> LoadSongAsync(LibrarySongEntry song);

    /// <summary>Convert-on-select: the song's playable audio only, under the same
    /// gate <see cref="LoadSongAsync" /> waits on. Blocking; off the UI thread.</summary>
    LibraryAudioPrepareOutcome PrepareAudio(LibrarySongEntry song, CancellationToken cancellationToken);

    /// <summary>The song's cover as a cached PNG path (<see cref="LibraryCoverCache" />),
    /// or "" when there is none. Blocking; <see cref="LibraryService" /> calls it off the
    /// UI thread, and only for a song that names a cover entry.</summary>
    string LoadCover(LibrarySongEntry song);

    /// <summary>Why the song cannot be played on this computer; answers from state already
    /// probed, so a row can ask while it is built.</summary>
    LibrarySongBlock BlockFor(LibrarySongEntry song);

    /// <summary>Library Doctor: does the package at <paramref name="path" /> still read, and
    /// does it carry audio. Never throws: a failure is an unreadable result.</summary>
    PackageProbeResult Probe(string path);
}
