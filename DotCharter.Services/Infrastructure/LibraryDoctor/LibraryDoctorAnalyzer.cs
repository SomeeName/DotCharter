using DotCharter.Localization;
namespace DotCharter.Services.Infrastructure.LibraryDoctor;

/// <summary>
/// The pure rule engine of the Library Doctor. Takes an in-memory
/// <see cref="LibraryDoctorSnapshot"/> (built read-only from the PSARC library
/// index) and returns a structured <see cref="LibraryDoctorReport"/>. No I/O,
/// no clock, fully deterministic — every rule is unit-testable with a fake
/// snapshot.
/// </summary>
public static class LibraryDoctorAnalyzer
{
    // Valid note tokens a tuning summary may contain (matches TuningUtilities'
    // note table). A summary is space-joined per-string note names, e.g.
    // "E A D G B E" (standard), "D A D G B E" (drop D).
    private static readonly HashSet<string> NoteNames = new(StringComparer.Ordinal)
    {
        "C", "C#", "D", "Eb", "E", "F", "F#", "G", "G#", "A", "Bb", "B"
    };

    // Named tunings rscharter/RS understand; the known-set is generated from
    // these through the SAME summary generator the parser uses, so any song
    // whose per-string offsets match one of these produces an identical
    // summary string and is recognized. Anything else is "unknown" (valid but
    // non-standard), not "invalid".
    private static readonly string[] NamedTunings =
    [
        "HIGH_F_SHARP_STANDARD", "HIGH_F_STANDARD", "E_STANDARD", "E_DROP_D", "E_DROP_C",
        "E_FLAT_STANDARD", "E_FLAT_DROP_D_FLAT", "D_STANDARD", "D_DROP_C", "D_FLAT_STANDARD",
        "D_FLAT_DROP_B", "C_STANDARD", "C_DROP_B_FLAT", "B_STANDARD", "B_DROP_A",
        "B_FLAT_STANDARD", "B_FLAT_DROP_A_FLAT", "A_STANDARD", "A_DROP_G", "A_FLAT_STANDARD",
        "G_STANDARD", "G_FLAT_STANDARD", "LOW_F_STANDARD", "OPEN_D", "OPEN_E", "OPEN_G",
        "OPEN_A", "DADGAD"
    ];

    private static readonly HashSet<string> KnownTuningSummaries = BuildKnownTuningSummaries();

    private static HashSet<string> BuildKnownTuningSummaries()
    {
        var set = new HashSet<string>(StringComparer.Ordinal)
        {
            // The parser's literal fallback when an arrangement carries no
            // Tuning object at all — treat it as a recognized E standard.
            "E Standard"
        };

        // 4 = bass, 5 = 5-string bass, 6 = guitar, 7 = 7-string. Summaries are
        // built the same way the manifest parser builds them.
        foreach (var count in new[] { 4, 5, 6, 7 })
        {
            foreach (var name in NamedTunings)
            {
                // TryGet, so a typo in NamedTunings above cannot silently register
                // "E Standard" as the known summary for a name that decodes to
                // nothing — which would mark every genuinely odd tuning as known.
                if (TuningUtilities.TryGetOffsetsForTuningType(name, count, out var offsets))
                    set.Add(string.Join(" ", TuningUtilities.GetTuningNames(count, offsets)));
            }
        }

        return set;
    }

    /// <summary>Classifies a tuning summary. Kept internal-public + pure so the
    /// tuning rule can be tested directly.</summary>
    public static TuningClassification ClassifyTuning(string? tuningSummary)
    {
        if (string.IsNullOrWhiteSpace(tuningSummary))
            return TuningClassification.Invalid;

        var summary = tuningSummary.Trim();

        // Known set wins first: it holds the textual fallback ("E Standard")
        // which would otherwise fail the note-token check below.
        if (KnownTuningSummaries.Contains(summary))
            return TuningClassification.Known;

        var tokens = summary.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length is < 4 or > 7)
            return TuningClassification.Invalid;

        foreach (var token in tokens)
        {
            if (!NoteNames.Contains(token))
                return TuningClassification.Invalid;
        }

        return TuningClassification.Unknown;
    }

    public static LibraryDoctorReport Analyze(LibraryDoctorSnapshot snapshot, ILocalizationService localization)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var issues = new List<LibraryDoctorIssue>();

        AddDuplicateIssues(snapshot.Songs, issues, localization);
        AddPerSongIssues(snapshot.Songs, issues, localization);
        AddUnreadableIssues(snapshot.UnreadablePackages, issues, localization);

        // Stable ordering: severity (Error first), then kind, then display.
        var ordered = issues
            .OrderByDescending(i => i.Severity)
            .ThenBy(i => i.Kind)
            .ThenBy(i => i.SongDisplay, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var groups = ordered
            .GroupBy(i => i.Kind)
            .Select(g => new LibraryIssueGroup(
                g.Key,
                g.Max(i => i.Severity),
                g.ToList()))
            .OrderByDescending(g => g.Severity)
            .ThenBy(g => g.Kind)
            .ToList();

        var packageCount = snapshot.Songs
            .Select(s => s.PsarcPath)
            .Concat(snapshot.UnreadablePackages.Select(p => p.PsarcPath))
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        return new LibraryDoctorReport
        {
            Issues = ordered,
            Groups = groups,
            SongsScanned = snapshot.Songs.Count,
            PackagesScanned = packageCount
        };
    }

    private static void AddDuplicateIssues(
        IReadOnlyList<LibraryDoctorSong> songs, List<LibraryDoctorIssue> issues, ILocalizationService localization)
    {
        // Group by logical identity; a group spanning two or more distinct
        // files is a duplicate. Songs with no key can't be de-duplicated.
        var groups = songs
            .Where(s => !string.IsNullOrWhiteSpace(s.SongKey))
            .GroupBy(s => s.Identity);

        foreach (var group in groups)
        {
            var byPath = group
                .GroupBy(s => s.PsarcPath, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
            if (byPath.Count < 2) continue;

            foreach (var song in byPath)
            {
                issues.Add(new LibraryDoctorIssue(
                    LibraryIssueKind.DuplicatePackage,
                    LibraryIssueSeverity.Warning,
                    song.Display,
                    song.PsarcPath,
                    localization.Plural("LibraryDoctor_Detail_DuplicateOne", "LibraryDoctor_Detail_Duplicate",
                        byPath.Count - 1, song.SongKey)));
            }
        }
    }

    private static void AddPerSongIssues(
        IReadOnlyList<LibraryDoctorSong> songs, List<LibraryDoctorIssue> issues, ILocalizationService localization)
    {
        foreach (var song in songs)
        {
            if (song.LoadableArrangementCount <= 0)
            {
                issues.Add(new LibraryDoctorIssue(
                    LibraryIssueKind.NoArrangements,
                    LibraryIssueSeverity.Error,
                    song.Display,
                    song.PsarcPath,
                    localization["LibraryDoctor_Detail_NoArrangements"]));
            }

            if (song.Audio == AudioAvailability.Missing)
            {
                issues.Add(new LibraryDoctorIssue(
                    LibraryIssueKind.MissingAudio,
                    LibraryIssueSeverity.Warning,
                    song.Display,
                    song.PsarcPath,
                    localization["LibraryDoctor_Detail_MissingAudio"]));
            }

            switch (ClassifyTuning(song.TuningSummary))
            {
                case TuningClassification.Invalid:
                    issues.Add(new LibraryDoctorIssue(
                        LibraryIssueKind.InvalidTuning,
                        LibraryIssueSeverity.Error,
                        song.Display,
                        song.PsarcPath,
                        localization.Format("LibraryDoctor_Detail_InvalidTuning", song.TuningSummary)));
                    break;
                case TuningClassification.Unknown:
                    issues.Add(new LibraryDoctorIssue(
                        LibraryIssueKind.UnknownTuning,
                        LibraryIssueSeverity.Info,
                        song.Display,
                        song.PsarcPath,
                        localization.Format("LibraryDoctor_Detail_UnknownTuning", song.TuningSummary)));
                    break;
            }
        }
    }

    private static void AddUnreadableIssues(
        IReadOnlyList<LibraryDoctorUnreadablePackage> packages, List<LibraryDoctorIssue> issues, ILocalizationService localization)
    {
        foreach (var package in packages)
        {
            issues.Add(new LibraryDoctorIssue(
                LibraryIssueKind.UnreadablePackage,
                LibraryIssueSeverity.Error,
                Path.GetFileName(package.PsarcPath) is { Length: > 0 } name ? name : package.PsarcPath,
                package.PsarcPath,
                string.IsNullOrWhiteSpace(package.Reason)
                    ? localization["LibraryDoctor_Detail_Unreadable"]
                    : localization.Format("LibraryDoctor_Detail_UnreadableReason", package.Reason)));
        }
    }

    public enum TuningClassification
    {
        Known,
        Unknown,
        Invalid
    }
}
