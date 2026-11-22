using System.Text.Json;
using DotCharter.Tone.Infrastructure.Json;

namespace DotCharter.Services.Online.MusicBrainz;

/// <summary>
///     Tolerant parsing of the MusicBrainz responses this
///     app reads. Pure, so it is testable against canned payloads with no network.
/// </summary>
/// <remarks>
///     Deliberately hand-rolled over <see cref="JsonDocument" /> rather than mapped
///     to records: these are foreign APIs whose optional fields come and go, and a
///     missing <c>releases</c> or <c>aliases</c> array must degrade to null rather
///     than throw. Only a top-level non-JSON body raises.
/// </remarks>
public static class MusicBrainzJson
{
    /// <summary>
    ///     The best recording in a <c>/ws/2/recording?query=…</c> response.
    /// </summary>
    /// <remarks>
    ///     MusicBrainz's own <c>score</c> leads, but it is a text-match score and
    ///     regularly gives 100 to several rows — so ties are broken by whether the
    ///     credited artist actually matches, then by having a release (whose title is the
    ///     album suggestion).
    /// </remarks>
    public static MusicBrainzMatch? ParseBestRecording(string json, string artist, string title)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("recordings", out var recordings) ||
            recordings.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        MusicBrainzMatch? best = null;
        var bestRank = (double.MinValue, double.MinValue, double.MinValue);
        var latinCandidates = new List<(double Score, bool ArtistMatches, string Title)>();

        foreach (var recording in recordings.EnumerateArray())
        {
            if (recording.ValueKind != JsonValueKind.Object) continue;

            var id = JsonFields.String(recording, "id");
            if (string.IsNullOrEmpty(id)) continue;

            var recordingTitle = JsonFields.String(recording, "title") ?? "";
            var credited = ArtistCredit(recording);
            var score = JsonFields.Int32OrNumericText(recording, "score") ?? 0;
            var (releaseId, releaseTitle) = FirstRelease(recording);

            foreach (var candidate in TitleCandidates(recording, recordingTitle))
                latinCandidates.Add((score, Similar(credited, artist), candidate));

            var rank = (
                (double)score,
                Similar(credited, artist) ? 1d : 0d,
                releaseId is not null ? 1d : 0d);

            if (best is not null && rank.CompareTo(bestRank) <= 0) continue;

            bestRank = rank;
            best = new MusicBrainzMatch(
                id, recordingTitle, credited, releaseId, releaseTitle, score);
        }

        // A high text score on a completely different artist is a miss, not a match. Except across scripts:
        // when we searched in an original script, a Latin-credited hit with a top score matched through the
        // artist's or title's alias, and its spelling cannot resemble ours at all.
        var acrossScripts = ScriptDetection.NeedsRomanization(artist) || ScriptDetection.NeedsRomanization(title);
        var unrelated = best is not null && !Similar(best.Artist, artist) && !Similar(best.Title, title);
        if (unrelated && !(acrossScripts && best!.Score >= 90)) return null;

        return best is null ? null : best with { LatinTitle = BestLatinTitle(latinCandidates, best.Score) };
    }

    /// <summary>A recording's title and its English aliases: the names it is known by in Latin script.</summary>
    private static IEnumerable<string> TitleCandidates(JsonElement recording, string title)
    {
        yield return title;
        if (!recording.TryGetProperty("aliases", out var aliases) || aliases.ValueKind != JsonValueKind.Array) yield break;
        foreach (var alias in aliases.EnumerateArray())
        {
            if (alias.ValueKind != JsonValueKind.Object) continue;
            var name = JsonFields.String(alias, "name");
            var locale = JsonFields.String(alias, "locale");
            if (!string.IsNullOrWhiteSpace(name) && (string.IsNullOrEmpty(locale) || ScriptDetection.LooksLikeEnglishLocale(locale)))
                yield return name;
        }
    }

    /// <summary>
    ///     The best Latin-script title among the recordings that scored within a few points of the top one
    ///     (or equal to it, when the credit is not the artist's own spelling); empty when none qualifies.
    /// </summary>
    private static string BestLatinTitle(List<(double Score, bool ArtistMatches, string Title)> candidates, int bestScore) =>
        candidates
            .Where(c => (c.ArtistMatches ? c.Score >= bestScore - 5 : c.Score >= bestScore) && !string.IsNullOrWhiteSpace(c.Title) && ScriptDetection.IsLatin(c.Title))
            .OrderByDescending(c => c.Score)
            .Select(c => c.Title.Trim())
            .FirstOrDefault() ?? "";

    private static string ArtistCredit(JsonElement recording)
    {
        if (!recording.TryGetProperty("artist-credit", out var credits) ||
            credits.ValueKind != JsonValueKind.Array)
        {
            return "";
        }

        var parts = new List<string>();
        foreach (var credit in credits.EnumerateArray())
        {
            if (credit.ValueKind != JsonValueKind.Object) continue;
            var name = JsonFields.String(credit, "name");
            if (string.IsNullOrEmpty(name) &&
                credit.TryGetProperty("artist", out var nested) && nested.ValueKind == JsonValueKind.Object)
            {
                name = JsonFields.String(nested, "name");
            }

            if (!string.IsNullOrEmpty(name)) parts.Add(name);
        }

        return string.Join(" & ", parts);
    }

    private static (string? Id, string? Title) FirstRelease(JsonElement recording)
    {
        if (!recording.TryGetProperty("releases", out var releases) ||
            releases.ValueKind != JsonValueKind.Array)
        {
            return (null, null);
        }

        foreach (var release in releases.EnumerateArray())
        {
            if (release.ValueKind != JsonValueKind.Object) continue;
            var id = JsonFields.String(release, "id");
            if (!string.IsNullOrEmpty(id)) return (id, JsonFields.String(release, "title"));
        }

        return (null, null);
    }

    /// <summary>
    ///     The best LATIN-script name for an artist, from a
    ///     <c>/ws/2/artist?query=…</c> response.
    ///     <para>
    ///     MusicBrainz stores an artist under their ORIGINAL script and hangs the
    ///     romanizations off as aliases, so the answer is almost never the
    ///     <c>name</c> field. Preference order, best first:
    ///     an <c>en</c>-locale PRIMARY alias (the official English name the
    ///     artist uses), any <c>en</c>-locale alias, any Latin alias at all, and
    ///     finally <c>sort-name</c> — which MusicBrainz's own style guide
    ///     requires to be Latin, and which is therefore the reliable floor.
    ///     </para>
    ///     <para>Only rows whose name or aliases actually resemble the query are
    ///     considered, so a mis-scored row cannot rename the song.</para>
    /// </summary>
    public static string? ParseLatinArtistName(string json, string artist)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("artists", out var artists) ||
            artists.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? best = null;
        var bestRank = int.MaxValue;

        foreach (var candidate in artists.EnumerateArray())
        {
            var name = JsonFields.String(candidate, "name");
            var sortName = JsonFields.String(candidate, "sort-name");
            var aliases = candidate.TryGetProperty("aliases", out var a) && a.ValueKind == JsonValueKind.Array
                ? a
                : default;

            if (!MatchesQuery(name, aliases, artist)) continue;

            foreach (var (value, rank) in RankedLatinNames(aliases, sortName))
            {
                if (rank < bestRank && ScriptDetection.IsUsefulRomanization(artist, value))
                {
                    best = ScriptDetection.Clean(value);
                    bestRank = rank;
                }
            }
        }

        return best;
    }

    private static bool MatchesQuery(string? name, JsonElement aliases, string artist)
    {
        if (Similar(name, artist)) return true;
        if (aliases.ValueKind != JsonValueKind.Array) return false;
        foreach (var alias in aliases.EnumerateArray())
        {
            if (Similar(JsonFields.String(alias, "name"), artist))
                return true;
        }

        return false;
    }

    private static IEnumerable<(string? Value, int Rank)> RankedLatinNames(JsonElement aliases, string? sortName)
    {
        if (aliases.ValueKind == JsonValueKind.Array)
        {
            foreach (var alias in aliases.EnumerateArray())
            {
                var value = JsonFields.String(alias, "name");
                var locale = JsonFields.String(alias, "locale");
                var primary = alias.TryGetProperty("primary", out var p) && p.ValueKind == JsonValueKind.True;

                if (ScriptDetection.LooksLikeEnglishLocale(locale))
                    yield return (value, primary ? 0 : 1);
                else
                    yield return (value, 2);
            }
        }

        yield return (sortName, 3);
    }

    /// <summary>Case- and punctuation-insensitive containment, which is what
    /// separates "Radiohead" from "Radiohead Tribute Band" without demanding an
    /// exact string the catalogue rarely has.</summary>
    internal static bool Similar(string? a, string? b)
    {
        var left = Normalize(a);
        var right = Normalize(b);
        if (left.Length == 0 || right.Length == 0) return false;
        return left.Contains(right, StringComparison.Ordinal) ||
               right.Contains(left, StringComparison.Ordinal);
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
                builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }
}
