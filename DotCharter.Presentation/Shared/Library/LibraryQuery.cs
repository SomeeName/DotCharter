using System.Globalization;
using System.Text;

namespace DotCharter.Presentation.Shared.Library;

/// <summary>
///     The one filter engine both song lists run: the free-text search, the facet legs, the record
///     ladder, and the per-option counts a facet popover shows. Pure — no UI, no I/O, no clock.
/// </summary>
public static class LibraryQuery
{
    /// <summary>Splits a query on whitespace; a lone "-" (from "artist - title") is dropped.
    /// Hoisted out of <see cref="Matches" /> so a pass splits once, not once per row.</summary>
    public static string[] Tokenize(string search) =>
        search.Length == 0
            ? []
            : search.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Where(t => t.Length > 1 || char.IsLetterOrDigit(t[0]))
                .ToArray();

    /// <summary>
    ///     Lower case (invariantly) and stripped of combining marks, so "Motorhead" finds
    ///     "Motörhead". Both sides of the search are folded, so the per-row test is an ordinal
    ///     Contains. ASCII skips the normalization; a string that will not normalize is lower-cased.
    /// </summary>
    public static string Fold(string text)
    {
        if (text.Length == 0) return text;
        if (Ascii.IsValid(text)) return text.ToLowerInvariant();

        string decomposed;
        try
        {
            decomposed = text.Normalize(NormalizationForm.FormD);
        }
        catch (ArgumentException)
        {
            return text.ToLowerInvariant();
        }

        var builder = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                builder.Append(ch);
        }

        return builder.ToString().ToLowerInvariant();
    }

    /// <summary><see cref="Tokenize" /> with each token <see cref="Fold" />ed — what <see cref="Matches" /> expects.</summary>
    public static string[] TokenizeFolded(string search)
    {
        var tokens = Tokenize(search);
        for (var i = 0; i < tokens.Length; i++) tokens[i] = Fold(tokens[i]);
        return tokens;
    }

    /// <summary>Whether a row survives the filter. Pass <see cref="TokenizeFolded" /> of the search, once per pass.</summary>
    public static bool Matches(ILibrarySong song, in LibraryFilter c, string[]? searchTokens = null)
    {
        if (c.HideLessons && song.IsLesson) return false;

        searchTokens ??= TokenizeFolded(c.Search);
        if (searchTokens.Length > 0)
        {
            // Every token somewhere in the folded haystack, in any order.
            var haystack = song.SearchHaystack;
            foreach (var token in searchTokens)
                if (!haystack.Contains(token, StringComparison.Ordinal)) return false;
        }

        // PATHS is the one multi-valued facet: any selected path keeps the row.
        if (c.Paths is { Count: > 0 })
        {
            var badges = song.ArrangementBadges;
            if (badges.Count == 0)
            {
                if (!c.Paths.Contains("")) return false;
            }
            else if (!AnySelected(badges, c.Paths))
            {
                return false;
            }
        }

        // Keys are computed only for an active facet: a pass over an unfiltered library allocates nothing.
        if (c.Tunings is { Count: > 0 } && !c.Tunings.Contains(LibraryFacets.TuningKeyOf(song))) return false;
        if (c.Capos is { Count: > 0 } && !c.Capos.Contains(LibraryFacets.CapoKeyOf(song))) return false;
        if (c.Lengths is { Count: > 0 } && !c.Lengths.Contains(LibraryFacets.LengthKeyOf(song))) return false;
        if (c.Difficulties is { Count: > 0 } && !c.Difficulties.Contains(LibraryFacets.DifficultyKeyOf(song))) return false;
        if (c.PlayedStates is { Count: > 0 } && !c.PlayedStates.Contains(LibraryFacets.PlayedKeyOf(song))) return false;
        if (c.Origins is { Count: > 0 } && !c.Origins.Contains(LibraryFacets.OriginKeyOf(song))) return false;
        if (c.Genres is { Count: > 0 } && !c.Genres.Contains(LibraryFacets.GenreKeyOf(song))) return false;
        if (c.Years is { Count: > 0 } && !c.Years.Contains(LibraryFacets.DecadeKey(song.Year))) return false;
        if (c.Statuses is { Count: > 0 } && !c.Statuses.Contains(LibraryFacets.StatusKeyOf(song))) return false;

        return c.Record <= 0 || MeetsRecord(song, c.Record);
    }

    /// <summary>
    ///     How many rows fall in each of <paramref name="facet" />'s buckets with every OTHER filter
    ///     applied — the number beside each option of an open popover.
    /// </summary>
    public static Dictionary<string, int> CountFacet(IEnumerable<ILibrarySong> songs, in LibraryFilter filter, string facet)
    {
        var others = filter.Without(facet);
        var tokens = TokenizeFolded(others.Search);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var song in songs)
        {
            if (!Matches(song, others, tokens)) continue;
            foreach (var value in LibraryFacets.ValuesOf(song, facet))
                counts[value] = counts.GetValueOrDefault(value) + 1;
        }

        return counts;
    }

    // The RECORD ladder is an achievement filter: an unplayed row has no record, and fails it.
    private static bool MeetsRecord(ILibrarySong song, int record)
    {
        if (song is not IPlayedLibrarySong { HasBest: true } played) return false;
        if (PlayRecordFilter.IsFullComboOnly(record)) return played.IsFullCombo;
        return PlayRecordFilter.MinGradeFor(record) is not { } floor || played.Grade >= floor;
    }

    private static bool AnySelected(IReadOnlyList<string> values, IReadOnlyCollection<string> selected)
    {
        foreach (var value in values)
            if (selected.Contains(value)) return true;
        return false;
    }
}
