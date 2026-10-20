using System.Globalization;
using DotCharter.Services.Library;

namespace DotCharter.Services.Package.DcSong;

/// <summary>
/// The Library's row for a <c>.dcsong</c>: the same <see cref="LibrarySongEntry" />
///     a Rocksmith archive gives, so the scan, the SQLite index, both song lists and their
///     filters take it unchanged. Pure: built from the package's manifest and its
///     (checksum-verified) chart, which the scan has already read.
///     <para>
///         The manifest names what the package says about itself (key, title, artist,
///         album, year, charter, length); the chart gives what only the chart knows —
///         each arrangement's path, tuning, capo and note count, and the difficulty
///         rating a <c>.psarc</c> only earns on its first load. <see cref="LibrarySongEntry.SongBank" />
///         carries the audio member, which says the codec.
///     </para>
/// </summary>
public static class DcSongLibraryEntry
{
    /// <summary>What each arrangement's <see cref="LibraryArrangementEntry.SngPath" /> names:
    /// the member it is read from, so the entry counts as loadable.</summary>
    public const string ChartMember = DcSongFormat.ChartPath;

    private const int RouteLead = 1;
    private const int RouteRhythm = 2;
    private const int RouteBass = 4;

    public static LibrarySongEntry Build(string packagePath, DcSongManifest manifest, ChartProject project, string coverImagePath)
    {
        var info = manifest.Song;
        var chart = project.Chart;
        var arrangements = Arrangements(project);
        var tuningSource = arrangements.FirstOrDefault(a => (a.RouteMask & RouteLead) != 0)
                           ?? arrangements.FirstOrDefault(a => (a.RouteMask & RouteRhythm) != 0)
                           ?? arrangements.FirstOrDefault(a => (a.RouteMask & RouteBass) != 0);
        return new LibrarySongEntry
        {
            PackagePath = packagePath,
            SongKey = info.Key,
            Charter = FirstNonBlank(info.Charter, chart.Charter),
            SongBank = manifest.Find(DcSongFileRole.Audio)?.Path ?? "",
            Title = FirstNonBlank(info.Title, chart.SongName, Path.GetFileNameWithoutExtension(packagePath)),
            Artist = FirstNonBlank(info.Artist, chart.Artist),
            Album = FirstNonBlank(info.Album, chart.AlbumName),
            Year = Year(info.Year) is > 0 and var year ? year : Year(chart.AlbumYear),
            AverageTempo = project.Bpm,
            SongLength = Math.Max(0, info.LengthMs) / 1000.0,
            TuningSummary = tuningSource?.Tuning ?? "",
            CapoSummary = tuningSource?.Capo ?? 0,
            CoverImagePath = coverImagePath,
            CoverImageEntryPath = manifest.Find(DcSongFileRole.Cover)?.Path ?? "",
            Difficulty = Difficulty(project),
            Arrangements = arrangements,
        };
    }

    /// <summary>One entry per instrument arrangement in project order, and one "Vocals"
    /// entry when the chart carries lyrics.</summary>
    internal static List<LibraryArrangementEntry> Arrangements(ChartProject project)
    {
        var entries = project.Arrangements
            .Where(a => a.ArrangementType != ArrangementType.Vocal)
            .Select(a => new LibraryArrangementEntry
            {
                ArrangementName = a.Name,
                SngPath = ChartMember,
                RouteMask = RouteMaskOf(a.ArrangementType),
                Represent = a.Subtype == ArrangementSubtype.Main,
                BassPick = a.PickedBass,
                ScrollSpeed = a.ScrollSpeed,
                NoteCount = HardestNoteCount(a),
                Tuning = TuningOf(a),
                Capo = a.Capo,
                ToneBase = a.ToneBase,
            })
            .ToList();
        if (project.VocalTracks.Any(v => v.Phrases.Any(p => p.Lyrics.Count > 0)))
            entries.Add(new LibraryArrangementEntry { ArrangementName = "Vocals", SngPath = ChartMember });
        return entries;
    }

    internal static int RouteMaskOf(ArrangementType type) => type switch
    {
        ArrangementType.Lead => RouteLead,
        ArrangementType.Rhythm => RouteRhythm,
        ArrangementType.Combo => RouteLead | RouteRhythm,
        ArrangementType.Bass => RouteBass,
        _ => 0
    };

    /// <summary>The tuning as the library stores it: the strings' notes, low to high
    /// ("D A D G B E"), from the offsets every tuning writer resolves.</summary>
    internal static string TuningOf(Arrangement arrangement)
    {
        var count = Math.Max(1, arrangement.StringCount);
        var offsets = new int[count];
        var resolved = TuningOffsetResolution.Resolve(arrangement);
        for (var i = 0; i < count && i < resolved.Count; i++) offsets[i] = resolved[i];
        return string.Join(" ", TuningUtilities.GetTuningNames(count, offsets));
    }

    /// <summary>Notes and chords at the hardest level, as Rocksmith's <c>NotesHard</c> counts them.</summary>
    private static int HardestNoteCount(Arrangement arrangement)
    {
        var hardest = arrangement.Levels.MaxBy(l => l.DifficultyIndex);
        return hardest == null ? 0 : hardest.Notes.Count + hardest.Chords.Count;
    }

    /// <summary>The song's 1–10 rating: its hardest arrangement, as a loaded
    /// <c>.psarc</c> is rated (0 when the chart has no beats to rate against).</summary>
    private static double Difficulty(ChartProject project)
    {
        if (project.Beats.Count == 0) return 0;
        var rating = 0.0;
        foreach (var arrangement in project.Arrangements.Where(a => a.ArrangementType != ArrangementType.Vocal))
            rating = Math.Max(rating, ChartDifficultyEstimator.Estimate(arrangement, project.Beats));
        return rating;
    }

    private static int Year(string? text) =>
        int.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var year) && year is > 0 and < 10000 ? year : 0;

    private static string FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? "";
}
