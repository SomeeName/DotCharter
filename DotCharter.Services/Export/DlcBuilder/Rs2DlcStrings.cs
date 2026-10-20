using System.Text.RegularExpressions;

namespace DotCharter.Services.Export.DlcBuilder;

/// <summary>
///     The string rules a <c>.rs2dlc</c> project file has to obey, ported from
///     Rocksmith2014.NET (DLC Builder) so a project we write reads back the way one
///     it wrote does.
/// </summary>
/// <remarks>
///     Sources, verbatim, from the reference checkout at the repo root
///     (<c>Rocksmith2014.NET/</c>, git-ignored — it is NOT a build dependency):
///     <list type="bullet">
///         <item><c>src/Rocksmith2014.DLCProject/StringValidator.fs</c> — <c>dlcKey</c>,
///         <c>sortField</c>, <c>removeArticles</c>, <c>fileName</c>,
///         <c>tryGetOfficialArtistNameSortValue</c>.</item>
///         <item><c>src/Rocksmith2014.DLCProject/DLCKey.fs</c> — <c>create</c>.</item>
///     </list>
///     <para>
///         These are DLC Builder's rules, not ours, so they are transcribed rather
///         than improved. If the upstream file changes, this one has drifted.
///     </para>
/// </remarks>
public static partial class Rs2DlcStrings
{
    /// <summary>DLC Builder refuses a key shorter than this.</summary>
    public const int MinimumDlcKeyLength = 5;

    /// <summary>
    ///     Sort values Rocksmith's own official files use for artists whose name has
    ///     two spellings in the wild. Transcribed from
    ///     <c>StringValidator.tryGetOfficialArtistNameSortValue</c>; the key is the
    ///     lower-cased artist name.
    /// </summary>
    private static readonly Dictionary<string, string> OfficialArtistSortValues =
        new(StringComparer.Ordinal)
        {
            ["a day to remember"] = "A Day To Remember",
            ["a flock of seagulls"] = "A Flock of Seagulls",
            ["b.b. king"] = "BB King",
            ["blink-182"] = "Blink 182",
            ["blue öyster cult"] = "Blue Oyster Cult",
            ["booker t. & the m.g.'s"] = "Booker T & The MGs",
            ["bobby \"blue\" bland"] = "Bobby Blue Bland",
            ["brooks & dunn"] = "Brooks Dunn",
            ["foster the people"] = "Foster People",
            ["grace potter & the nocturnals"] = "Grace Potter and The Nocturnals",
            ["hail the sun"] = "Hail Sun",
            ["the mamas & the papas"] = "Mamas and The Papas",
            ["motörhead"] = "Motorhead",
            ["mötley crüe"] = "Motley Crue",
            ["t. rex"] = "T Rex"
        };

    /// <summary>Only alphanumeric characters survive in a DLC key.</summary>
    public static string DlcKey(string? input) =>
        input is null ? string.Empty : NonAlphanumeric().Replace(input, "");

    /// <summary>A sort field must START on an alphanumeric character.</summary>
    public static string SortField(string? input) =>
        input is null ? string.Empty : LeadingNonAlphanumeric().Replace(input, "");

    /// <summary>Drops a leading English article. Case-insensitive, like
    /// <c>String.startsWith</c> in the F# source.</summary>
    public static string RemoveArticles(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        foreach (var article in (string[])["the ", "a ", "an "])
        {
            if (input.StartsWith(article, StringComparison.OrdinalIgnoreCase))
                return input[article.Length..];
        }

        return input;
    }

    /// <summary>The sort value for an ARTIST name: the official spelling when
    /// Rocksmith has one, otherwise the name with its article dropped.</summary>
    public static string ArtistSortValue(string? artist)
    {
        if (string.IsNullOrEmpty(artist)) return string.Empty;
        return OfficialArtistSortValues.TryGetValue(artist.ToLowerInvariant(), out var official)
            ? SortField(official)
            : SortField(RemoveArticles(artist));
    }

    /// <summary>The sort value for a TITLE: article dropped, then sort-fielded.</summary>
    public static string TitleSortValue(string? title) =>
        string.IsNullOrEmpty(title) ? string.Empty : SortField(RemoveArticles(title));

    /// <summary>An ALBUM name is its own sort value upstream
    /// (<c>convertToSortField</c> passes it through untouched).</summary>
    public static string AlbumSortValue(string? album) => album ?? string.Empty;

    /// <summary>
    ///     DLC Builder's key: two characters of the charter name (or two random
    ///     letters), then five of the artist, then five of the title, padded with
    ///     random letters to <see cref="MinimumDlcKeyLength" />.
    /// </summary>
    /// <param name="charterName">Supplies the two-character prefix.</param>
    /// <param name="artist">Supplies up to five alphanumeric characters.</param>
    /// <param name="title">Supplies up to five alphanumeric characters.</param>
    /// <param name="randomLetter">
    ///     Injected so a test is deterministic. Upstream this is
    ///     <c>RandomGenerator.nextAlphabet</c>.
    /// </param>
    public static string CreateDlcKey(
        string? charterName, string? artist, string? title, Func<char>? randomLetter = null)
    {
        randomLetter ??= () => (char)('a' + Random.Shared.Next(26));

        var prefixSource = DlcKey(charterName);
        var prefix = prefixSource.Length < 2
            ? new string([randomLetter(), randomLetter()])
            : prefixSource[..2];

        var key = prefix + Part(artist) + Part(title);

        while (key.Length < MinimumDlcKeyLength) key += randomLetter();
        return key;

        static string Part(string? value)
        {
            var alnum = DlcKey(value);
            return alnum.Length <= 5 ? alnum : alnum[..5];
        }
    }

    /// <summary>
    ///     A file name without its extension, DLC Builder style: diacritics removed,
    ///     everything but <c>[ 0-9a-zA-Z_-]</c> dropped, runs of whitespace folded to
    ///     one dash. This is what produces <c>Polkadot-Stingray_Sakasama.rs2dlc</c>.
    /// </summary>
    public static string FileNameStem(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        var noDiacritics = RemoveDiacritics(input);
        var filtered = NotFileNameSafe().Replace(noDiacritics, "");
        return Whitespace().Replace(filtered, "-");
    }

    /// <summary>The <c>&lt;artist&gt;_&lt;title&gt;.rs2dlc</c> name DLC Builder
    /// defaults to. Never empty — an untitled project still needs a file.</summary>
    public static string ProjectFileName(string? artist, string? title)
    {
        var stem = $"{FileNameStem(artist)}_{FileNameStem(title)}".Trim('_');
        if (string.IsNullOrWhiteSpace(stem)) stem = "project";
        return stem + ".rs2dlc";
    }

    private static string RemoveDiacritics(string input)
    {
        var decomposed = input.Normalize(System.Text.NormalizationForm.FormD);
        var builder = new System.Text.StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) !=
                System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                builder.Append(c);
            }
        }

        return builder.ToString().Normalize(System.Text.NormalizationForm.FormC);
    }

    [GeneratedRegex("[^0-9a-zA-Z]")]
    private static partial Regex NonAlphanumeric();

    [GeneratedRegex("^[^0-9a-zA-Z]*")]
    private static partial Regex LeadingNonAlphanumeric();

    [GeneratedRegex("[^ 0-9a-zA-Z_-]")]
    private static partial Regex NotFileNameSafe();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
