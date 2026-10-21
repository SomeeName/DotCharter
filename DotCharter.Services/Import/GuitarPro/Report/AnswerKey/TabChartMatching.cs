using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DotCharter.Services.Import.GuitarPro.Report.AnswerKey;

/// <summary>One row of a CustomsForge "my CDLC" CSV export: only the columns the comparison reads.</summary>
public sealed record CdlcRow(string Id, string Title, string ArtistId, DateTime? CreatedAt, DateTime? UpdatedAt)
{
    /// <summary>Known-good: UPDATED on or after <paramref name="cutoff" /> (the owner's rule; the creation date does not count).</summary>
    public bool IsKnownGood(DateTime cutoff) => UpdatedAt >= cutoff;
}

/// <summary>
///     A local, hand-written match: the row's tab (relative to the tab root) and/or chart folder
///     (relative to the chart root), or <see cref="Skip" /> to drop the row.
/// </summary>
public sealed record MatchOverride(string Id, string? Tab, string? ChartFolder, bool Skip);

/// <summary>A candidate path with its match score (0..1) and the artist read from its name, if any.</summary>
public sealed record MatchCandidate(string Path, double Score, string? Artist);

/// <summary>
///     Matches a CDLC row (title) to a finished-chart folder (<c>Artist - Title</c>) and to a Guitar
///     Pro tab, by fuzzy title: NFKD-folded, accent- and punctuation-free, compared as character
///     bigrams (Dice), with and without bracketed parts such as <c>(feat. …)</c>. The chart
///     folder's artist then breaks ties between tabs. The engine of <c>tools/tab-vs-chart</c>.
/// </summary>
public static partial class TabChartMatching
{
    /// <summary>Below this a candidate is "unmatched" (the override file takes it from there).</summary>
    public const double MinimumScore = 0.72;

    private static readonly string[] TabExtensions = [".gp", ".gpx", ".gp5", ".gp4", ".gp3"];

    /// <summary>Reads the CSV (RFC 4180: quoted cells may hold commas, quotes and line breaks).</summary>
    public static List<CdlcRow> ReadCdlcCsv(string text)
    {
        var rows = ParseCsv(text);
        if (rows.Count == 0) return [];

        var header = rows[0];
        int Col(string name) => header.FindIndex(h => h.Trim().Trim('\uFEFF').Equals(name, StringComparison.OrdinalIgnoreCase));
        var (id, title, artist, created, updated) = (Col("ID"), Col("Title"), Col("Artist ID"), Col("Created At"), Col("Updated At"));
        if (id < 0 || title < 0) throw new FormatException("The CSV has no ID and Title columns.");

        static string Cell(List<string> row, int i) => i >= 0 && i < row.Count ? row[i] : "";
        return [.. rows.Skip(1)
            .Where(r => Cell(r, id).Trim().Length > 0)
            .Select(r => new CdlcRow(Cell(r, id).Trim(), Cell(r, title).Trim(), Cell(r, artist).Trim(),
                Date(Cell(r, created)), Date(Cell(r, updated))))];
    }

    public static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c != '"')
                {
                    cell.Append(c);
                }
                else if (i + 1 < text.Length && text[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else
                {
                    quoted = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    quoted = true;
                    break;
                case ',':
                    row.Add(cell.ToString());
                    cell.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    row.Add(cell.ToString());
                    cell.Clear();
                    rows.Add(row);
                    row = [];
                    break;
                default:
                    cell.Append(c);
                    break;
            }
        }

        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString());
            rows.Add(row);
        }

        return rows;
    }

    /// <summary>
    ///     Override lines: <c>id &lt;TAB&gt; tab-or-dash &lt;TAB&gt; chart-folder-or-dash</c>, or
    ///     <c>id &lt;TAB&gt; skip</c>. <c>#</c> starts a comment.
    /// </summary>
    public static Dictionary<string, MatchOverride> ReadOverrides(string text)
    {
        var result = new Dictionary<string, MatchOverride>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#')) continue;

            var cells = line.Split('\t').Select(c => c.Trim()).ToArray();
            if (cells.Length < 2 || cells[0].Length == 0) continue;

            static string? Value(string[] c, int i) => i < c.Length && c[i].Length > 0 && c[i] != "-" ? c[i] : null;
            var skip = cells[1].Equals("skip", StringComparison.OrdinalIgnoreCase);
            result[cells[0]] = new MatchOverride(cells[0], skip ? null : Value(cells, 1), skip ? null : Value(cells, 2), skip);
        }

        return result;
    }

    /// <summary>Lower case, NFKD without combining marks, letters and digits only, single spaces.</summary>
    public static string Normalize(string text)
    {
        var folded = text.Normalize(NormalizationForm.FormKD);
        var sb = new StringBuilder(folded.Length);
        foreach (var c in folded)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }

        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>0..1: the best Dice bigram score over the titles with and without bracketed parts.</summary>
    public static double TitleSimilarity(string a, string b)
    {
        var best = 0.0;
        foreach (var x in Variants(a))
        {
            foreach (var y in Variants(b))
                best = Math.Max(best, Dice(x, y));
        }

        return best;
    }

    /// <summary>Splits <c>Artist - Title</c>; a name with no separator is all title.</summary>
    public static (string? Artist, string Title) SplitName(string name)
    {
        var i = name.IndexOf(" - ", StringComparison.Ordinal);
        return i > 0 ? (name[..i].Trim(), name[(i + 3)..].Trim()) : (null, name.Trim());
    }

    /// <summary>The best chart folder (folder NAMES, <c>Artist - Title</c>) for a title, or null.</summary>
    public static MatchCandidate? BestChartFolder(string title, IEnumerable<string> folderNames)
    {
        MatchCandidate? best = null;
        foreach (var folder in folderNames)
        {
            var (artist, folderTitle) = SplitName(StripFolderNoise(folder));
            var score = TitleSimilarity(title, folderTitle) - (IsCopyFolder(folder) ? 0.05 : 0);
            if (best == null || score > best.Score) best = new MatchCandidate(folder, score, artist);
        }

        return best != null && best.Score >= MinimumScore ? best : null;
    }

    /// <summary>
    ///     The best tab (paths relative to the tab root) for a title, preferring files whose path
    ///     names <paramref name="artist" />, then the richer format (<c>.gp</c> first), then the
    ///     shorter path.
    /// </summary>
    public static MatchCandidate? BestTab(string title, string? artist, IEnumerable<string> relativePaths)
    {
        var normalizedArtist = artist == null ? "" : Normalize(artist);
        var scored = new List<(MatchCandidate Candidate, double Raw, int Rank)>();
        foreach (var path in relativePaths)
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();
            var rank = Array.IndexOf(TabExtensions, extension);
            if (rank < 0) continue;

            var (fileArtist, fileTitle) = SplitName(Path.GetFileNameWithoutExtension(path));
            var score = TitleSimilarity(title, fileTitle);
            if (normalizedArtist.Length > 0)
            {
                var folded = Normalize(path).Replace(" ", "", StringComparison.Ordinal);
                if (folded.Contains(normalizedArtist.Replace(" ", "", StringComparison.Ordinal), StringComparison.Ordinal)) score += 0.1;
                else if (fileArtist != null && TitleSimilarity(fileArtist, artist!) < 0.6) score -= 0.15;
            }

            scored.Add((new MatchCandidate(path, Math.Min(1.0, score), fileArtist), score, rank));
        }

        var best = scored
            .OrderByDescending(s => Math.Round(s.Raw, 6))
            .ThenBy(s => s.Rank)
            .ThenBy(s => s.Candidate.Path.Length)
            .ThenBy(s => s.Candidate.Path, StringComparer.Ordinal)
            .Select(s => s.Candidate)
            .FirstOrDefault();
        return best != null && best.Score >= MinimumScore ? best : null;
    }

    private static DateTime? Date(string text) =>
        DateTime.TryParse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d)
            ? d
            : null;

    private static IEnumerable<string> Variants(string title)
    {
        var whole = Normalize(title).Replace(" ", "", StringComparison.Ordinal);
        if (whole.Length > 0) yield return whole;

        var bare = Normalize(Brackets().Replace(title, " ")).Replace(" ", "", StringComparison.Ordinal);
        if (bare.Length > 0 && bare != whole) yield return bare;
    }

    private static double Dice(string a, string b)
    {
        if (a == b) return 1.0;
        if (a.Length < 2 || b.Length < 2) return 0.0;

        // Containment of a long enough title ("Song" inside "Song TV size") is near-certain; a
        // short word inside a long title is not.
        var (shorter, longer) = a.Length <= b.Length ? (a, b) : (b, a);
        var contained = shorter.Length >= 5 && shorter.Length * 10 >= longer.Length * 3 && longer.Contains(shorter, StringComparison.Ordinal)
            ? 0.85
            : 0.0;

        var bigrams = new Dictionary<(char, char), int>();
        for (var i = 0; i + 1 < a.Length; i++)
            bigrams[(a[i], a[i + 1])] = bigrams.GetValueOrDefault((a[i], a[i + 1])) + 1;

        var shared = 0;
        for (var i = 0; i + 1 < b.Length; i++)
        {
            var key = (b[i], b[i + 1]);
            if (bigrams.TryGetValue(key, out var n) && n > 0)
            {
                bigrams[key] = n - 1;
                shared++;
            }
        }

        return Math.Max(contained, 2.0 * shared / (a.Length - 1 + b.Length - 1));
    }

    private static string StripFolderNoise(string folder) => Brackets().Replace(folder, " ").Trim();

    private static bool IsCopyFolder(string folder) => CopyFolder().IsMatch(folder);

    [GeneratedRegex(@"\([^)]*\)|\[[^\]]*\]|（[^）]*）|【[^】]*】|\bfeat\..*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Brackets();

    [GeneratedRegex(@"\(Toolkit|\(From-|Copy|Kopie|Backup", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CopyFolder();
}
