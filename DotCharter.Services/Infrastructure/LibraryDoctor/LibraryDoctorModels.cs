using System.Globalization;
using DotCharter.Services.Library;

namespace DotCharter.Services.Infrastructure.LibraryDoctor;

/// <summary>The kind of health problem a scanned library entry can have.
/// Ordered by the natural grouping we present in the report.</summary>
public enum LibraryIssueKind
{
    /// <summary>Two or more packages share the same song identity
    /// (SongKey + Charter + Version).</summary>
    DuplicatePackage,

    /// <summary>A package carries a chart but no audio at all (RS1-import
    /// charts, stripped repacks). The chart plays silently.</summary>
    MissingAudio,

    /// <summary>The tuning summary is structurally broken (empty, not note
    /// names, wrong string count) — the archive metadata is corrupt.</summary>
    InvalidTuning,

    /// <summary>A structurally valid but unrecognized (non-standard, non-named)
    /// tuning. Informational — many alternate tunings are perfectly legal.</summary>
    UnknownTuning,

    /// <summary>A song with no loadable (SNG-backed) arrangement — nothing to
    /// play.</summary>
    NoArrangements,

    /// <summary>The package could not be opened/parsed at all (corrupt file).</summary>
    UnreadablePackage
}

/// <summary>How serious an issue is, for sorting + colour in the UI.</summary>
public enum LibraryIssueSeverity
{
    Info,
    Warning,
    Error
}

/// <summary>Whether a package was found to contain audio. <see cref="Unknown"/>
/// means the doctor did not probe it (or could not tell); an unknown value
/// never raises a <see cref="LibraryIssueKind.MissingAudio"/> issue.</summary>
public enum AudioAvailability
{
    Unknown,
    Present,
    Missing
}

/// <summary>
/// The pure, projection-only view of a single indexed song the doctor rules
/// operate over. Deliberately decoupled from <c>LibrarySongEntry</c> so the rule
/// logic can be unit-tested with a hand-built fake index and no I/O.
/// </summary>
public sealed record LibraryDoctorSong
{
    public string PsarcPath { get; init; } = "";
    public string SongKey { get; init; } = "";
    public string Charter { get; init; } = "";
    public string Version { get; init; } = "";
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string TuningSummary { get; init; } = "";

    /// <summary>Number of arrangements that resolve to a playable SNG.</summary>
    public int LoadableArrangementCount { get; init; }

    public AudioAvailability Audio { get; init; } = AudioAvailability.Unknown;

    /// <summary>Case-insensitive logical identity used for duplicate detection —
    /// mirrors the shared-leaderboard key (SongKey|Charter|Version), plus the source
    /// kind (the file's extension), as <c>LibraryDedupe</c> collapses: a <c>.dcsong</c>
    /// beside the <c>.psarc</c> it was exported from is not a duplicate.</summary>
    public string Identity =>
        string.Create(CultureInfo.InvariantCulture,
            $"{SongKey.Trim().ToLowerInvariant()}|{Charter.Trim().ToLowerInvariant()}|{Version.Trim().ToLowerInvariant()}|{LibraryArchiveFiles.KindOf(PsarcPath)}");

    /// <summary>Human-facing label: "Artist - Title", falling back to the song
    /// key then the file name so an issue row is never blank.</summary>
    public string Display
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Artist) && !string.IsNullOrWhiteSpace(Title))
                return $"{Artist} - {Title}";
            if (!string.IsNullOrWhiteSpace(Title)) return Title;
            if (!string.IsNullOrWhiteSpace(SongKey)) return SongKey;
            var name = Path.GetFileName(PsarcPath);
            return string.IsNullOrWhiteSpace(name) ? "(unknown)" : name;
        }
    }
}

/// <summary>A package the scan could not open at all.</summary>
public sealed record LibraryDoctorUnreadablePackage(string PsarcPath, string Reason);

/// <summary>The full input to <see cref="LibraryDoctorAnalyzer.Analyze"/>.</summary>
public sealed record LibraryDoctorSnapshot
{
    public IReadOnlyList<LibraryDoctorSong> Songs { get; init; } = [];
    public IReadOnlyList<LibraryDoctorUnreadablePackage> UnreadablePackages { get; init; } = [];
}

/// <summary>One concrete health finding.</summary>
public sealed record LibraryDoctorIssue(
    LibraryIssueKind Kind,
    LibraryIssueSeverity Severity,
    string SongDisplay,
    string PsarcPath,
    string Detail);

/// <summary>Issues of one kind, pre-grouped with a count for the UI.</summary>
public sealed record LibraryIssueGroup(
    LibraryIssueKind Kind,
    LibraryIssueSeverity Severity,
    IReadOnlyList<LibraryDoctorIssue> Issues)
{
    public int Count => Issues.Count;
}

/// <summary>The structured result of a library health check.</summary>
public sealed record LibraryDoctorReport
{
    public IReadOnlyList<LibraryDoctorIssue> Issues { get; init; } = [];
    public IReadOnlyList<LibraryIssueGroup> Groups { get; init; } = [];
    public int SongsScanned { get; init; }
    public int PackagesScanned { get; init; }

    public int TotalIssueCount => Issues.Count;
    public bool IsHealthy => Issues.Count == 0;

    public static readonly LibraryDoctorReport Empty = new();
}

/// <summary>Progress tick while the (I/O-bound) scan runs.</summary>
public readonly record struct LibraryDoctorProgress(int ProcessedPackages, int TotalPackages)
{
    public double Fraction => TotalPackages == 0 ? 0.0 : (double)ProcessedPackages / TotalPackages;
}
