namespace DotCharter.Services.Library;

public sealed class LibrarySongEntry
{
    public string Album { get; init; } = "";
    public string Artist { get; init; } = "";
    public double AverageTempo { get; init; }
    public int Year { get; init; }
    public string PackagePath { get; init; } = "";
    public string SongKey { get; init; } = "";

    /// <summary>CDLC charter — the "Package Author" from the archive's
    /// toolkit.version stamp. Empty for the base game / un-stamped archives.
    /// Part of the shared-leaderboard identity so two charters' cuts of the
    /// same SongKey get distinct boards.</summary>
    public string Charter { get; init; } = "";

    /// <summary>CDLC package version — the "Package Version" from
    /// toolkit.version. Empty when un-stamped. Part of the leaderboard
    /// identity so re-charted/updated builds don't share a board with the
    /// old cut.</summary>
    public string Version { get; init; } = "";

    /// <summary>Where THIS song's audio sits inside its file: the Wwise bank
    /// filename from the manifest ("song_key.bnk"), which resolves the .wem inside
    /// a multi-song archive; for a <c>.dcsong</c> the audio member
    /// (<c>audio/song.opus</c> or <c>audio/song.flac</c>), which says the codec.</summary>
    public string SongBank { get; init; } = "";

    /// <summary>What this entry was read from — its file's extension
    /// (<see cref="LibraryArchiveFiles.KindOf" />), so it needs no column in the
    /// library index.</summary>
    public LibrarySourceKind SourceKind => LibraryArchiveFiles.KindOf(PackagePath);

    /// <summary>A <c>.dcsong</c> whose song is Opus: the audio engine does not
    /// decode Opus, so playing it needs ffmpeg.</summary>
    public bool NeedsAudioDecoder =>
        SourceKind == LibrarySourceKind.DcSong && SongBank.EndsWith(".opus", StringComparison.OrdinalIgnoreCase);
    public string Title { get; init; } = "";
    public string CoverImagePath { get; init; } = "";
    public string CoverImageEntryPath { get; init; } = "";
    public double SongLength { get; init; }
    public string TuningSummary { get; init; } = "";
    public int CapoSummary { get; init; }
    public IReadOnlyList<LibraryArrangementEntry> Arrangements { get; init; } = [];

    /// <summary>Deterministic 1–10 chart difficulty
    /// (<c>ChartDifficultyEstimator</c>, hardest arrangement) cached by the
    /// library index; 0 = never rated. A scan cannot compute it (no note data in
    /// a <c>.hsan</c>) — it is written the first time the song is loaded
    /// (the package source) and read back cheaply
    /// on subsequent scans. Surfaces as the Song Select difficulty chip / sort /
    /// range filter for UNPLAYED songs (played songs use the stats rating).</summary>
    public double Difficulty { get; init; }

    /// <summary>
    ///     Stock or custom, as the registered <see cref="ILibraryOriginClassifier" /> (an
    ///     plugin's) judges it from what this entry ALREADY carries, so it costs no extra
    ///     I/O; <see cref="LibraryOrigin.Unknown" /> when none is registered.
    ///     <para>
    ///         MEMOIZED rather than expression-bodied. Every input is init-only,
    ///         so the answer cannot change for the entry's lifetime, and this is
    ///         read once per ROW per filter pass over a 2,000-song library — an
    ///         expression body would re-walk the arrangement list on every
    ///         keystroke, which is the exact shape the Song Select projections
    ///         were flattened to kill.
    ///     </para>
    /// </summary>
    public LibraryOrigin Origin => _origin ??= LibraryOrigins.Of(this);

    private LibraryOrigin? _origin;

    public string DisplayTitle => string.IsNullOrWhiteSpace(Artist) ? Title : $"{Artist} - {Title}";
    public string ArrangementSummary => string.Join(", ", Arrangements.Select(a => a.PathLabel).Distinct());
    public bool CanLoad => Arrangements.Any(a => !string.IsNullOrWhiteSpace(a.SngPath));
}
