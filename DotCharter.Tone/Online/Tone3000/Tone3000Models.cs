namespace DotCharter.Tone.Online.Tone3000;

/// <summary>Gear-type filter for a TONE3000 capture search. <see cref="Any"/>
/// omits the filter.</summary>
public enum Tone3000GearType
{
    Any = 0,
    Amp,
    AmpCab,
    Pedal,
    Outboard,
    Cab,
    Space,
    Experimental
}

/// <summary>
///     Neural-capture architecture filter (<c>architecture</c>).
///
///     <para><b>There is no "everything" value, and the absent value is not
///     it.</b> Omitting the parameter returns A1 + Custom and EXCLUDES
///     A2-only tones — the API documents this as a deliberate legacy default
///     "so API users that pre-date A2 don't receive models they can't load".
///     That is why A2 captures were unfindable here: not a search bug of ours
///     on top of the parameter-name bug, but a filter we never sent.
///     <see cref="A2"/> is therefore the only way to see them, and it excludes
///     A1 in turn.</para>
/// </summary>
public enum Tone3000Architecture
{
    /// <summary>Omit the parameter: the server's legacy A1 + Custom set.</summary>
    Default = 0,
    A1,
    A2,
    Custom
}

/// <summary>
///     Result ordering for a TONE3000 search (<c>TonesSort</c>).
///     <see cref="ServerDefault"/> omits the parameter, which is what the API
///     documents as the sensible choice: <c>best-match</c> when a query term is
///     present, <c>trending</c> when it is not.
/// </summary>
public enum Tone3000Sort
{
    ServerDefault = 0,
    BestMatch,
    Newest,
    Oldest,
    Trending,
    DownloadsAllTime
}

/// <summary>
///     One community capture ("tone") as listed by the TONE3000 search API. A
///     tone groups one or more downloadable model files (<see cref="Tone3000Model"/>)
///     — different size tiers / formats of the same capture. The license and the
///     attribution page URL are load-bearing: per the app's attribution policy
///     they are displayed in the UI and stored with every download.
/// </summary>
public sealed record Tone3000Tone(
    long Id,
    string Title,
    string Author,
    string GearType,
    string License,
    string LicenseUrl,
    string PageUrl,
    string Description,
    IReadOnlyList<string> Formats,
    IReadOnlyList<string> Sizes,
    long Downloads,
    int A1ModelCount = 0,
    int A2ModelCount = 0,
    int CustomModelCount = 0)
{
    /// <summary>True when this tone ships at least one A2-architecture model.
    /// A tone with ONLY A2 models is invisible to a search that does not send
    /// <c>architecture=2</c> — the server's legacy default is A1 + Custom.</summary>
    public bool HasA2 => A2ModelCount > 0;

    /// <summary>True when this tone ships a model the legacy (architecture-less)
    /// search would have returned: A1 or Custom.</summary>
    public bool HasLegacyArchitecture => A1ModelCount > 0 || CustomModelCount > 0;
}

/// <summary>One downloadable model file belonging to a tone. <see cref="ModelUrl"/>
/// is fetched with the signed-in user's Bearer access token.
/// <see cref="Architecture"/> is the API's <c>architecture_version</c>
/// (<c>"1"</c>/<c>"2"</c>/<c>"custom"</c>, empty for a non-neural file such as
/// an IR). <see cref="Format"/> is EMPTY on the current API — the models
/// endpoint carries no format field, it lives on the parent tone — so treat an
/// empty format as UNKNOWN, never as "not a NAM".</summary>
public sealed record Tone3000Model(
    long Id,
    long ToneId,
    string Name,
    string Format,
    string SizeTier,
    string Architecture,
    string ModelUrl,
    long FileBytes);

/// <summary>Free-text search request. <see cref="Query"/> is the primary lever —
/// community tones are commonly named after artists/songs, so "tone for song X"
/// is just a text search. <see cref="PerPage"/> is clamped to the server's
/// documented maximum (<see cref="Tone3000Wire.MaxPageSize"/>).</summary>
public sealed record Tone3000SearchQuery(
    string Query,
    Tone3000GearType GearType = Tone3000GearType.Any,
    string? Format = null,
    string? Size = null,
    Tone3000Architecture Architecture = Tone3000Architecture.Default,
    Tone3000Sort Sort = Tone3000Sort.ServerDefault,
    int Page = 1,
    int PerPage = 24);

/// <summary>One page of search results. <see cref="TotalCount"/> is 0 when the
/// server did not report a total.</summary>
public sealed record Tone3000SearchPage(
    IReadOnlyList<Tone3000Tone> Tones,
    int Page,
    int TotalCount);

/// <summary>The signed-in TONE3000 identity (from <c>GET /api/v1/user</c>).</summary>
public sealed record Tone3000User(string Id, string UserName);

/// <summary>
///     Enum ↔ wire-string maps for the TONE3000 query parameters, and the
///     query-string builder for <c>GET /api/v1/tones/search</c>. Pure.
///
///     <para><b>The parameter names are the contract and they are NOT the
///     obvious ones.</b> Source: the API reference at
///     <c>https://www.tone3000.com/api</c> and the official SDK
///     (<c>github.com/tone-3000/api</c>, <c>src/tone3000-client.ts</c>
///     <c>searchTones</c>), which agree exactly:
///     <c>query</c> (NOT <c>q</c>), <c>page</c>, <c>page_size</c> (NOT
///     <c>per_page</c>; default 10, <b>max 25</b>), <c>sort</c>, <c>gears</c>
///     (NOT <c>gear_type</c> — a LIST, underscore-joined, e.g.
///     <c>amp_amp-cab_pedal</c>, which is why the separator is not a hyphen:
///     the values themselves contain hyphens), <c>sizes</c> (NOT <c>size</c>;
///     also underscore-joined by the SDK), <c>format</c> (singular) and
///     <c>architecture</c>. Unknown parameters are ignored by the server, so
///     the old spellings did not fail loudly — they silently searched for
///     nothing with no filters, which is exactly what "search doesn't work"
///     looked like.</para>
/// </summary>
public static class Tone3000Wire
{
    /// <summary>Server-documented maximum for <c>page_size</c>.</summary>
    public const int MaxPageSize = 25;

    /// <summary>Separator for the multi-valued <c>gears</c>/<c>sizes</c>
    /// parameters. Underscore, because the values themselves contain hyphens
    /// (<c>amp-cab</c>, <c>aida-x</c>).</summary>
    public const char ListSeparator = '_';

    /// <summary>Wire value for a gear-type filter, or null for <see cref="Tone3000GearType.Any"/>.</summary>
    public static string? GearTypeValue(Tone3000GearType gearType) => gearType switch
    {
        Tone3000GearType.Amp => "amp",
        Tone3000GearType.AmpCab => "amp-cab",
        Tone3000GearType.Pedal => "pedal",
        Tone3000GearType.Outboard => "outboard",
        Tone3000GearType.Cab => "cab",
        Tone3000GearType.Space => "space",
        Tone3000GearType.Experimental => "experimental",
        _ => null
    };

    /// <summary>Wire value for an architecture filter, or null for
    /// <see cref="Tone3000Architecture.Default"/> (omit the parameter — the
    /// server's legacy A1 + Custom set, which EXCLUDES A2-only tones).</summary>
    public static string? ArchitectureValue(Tone3000Architecture architecture) => architecture switch
    {
        Tone3000Architecture.A1 => "1",
        Tone3000Architecture.A2 => "2",
        Tone3000Architecture.Custom => "custom",
        _ => null
    };

    /// <summary>Wire value for a sort order, or null for
    /// <see cref="Tone3000Sort.ServerDefault"/> (omit the parameter and let the
    /// server pick best-match/trending from whether a query was supplied).</summary>
    public static string? SortValue(Tone3000Sort sort) => sort switch
    {
        Tone3000Sort.BestMatch => "best-match",
        Tone3000Sort.Newest => "newest",
        Tone3000Sort.Oldest => "oldest",
        Tone3000Sort.Trending => "trending",
        Tone3000Sort.DownloadsAllTime => "downloads-all-time",
        _ => null
    };

    /// <summary>
    ///     Builds the query string (without a leading <c>?</c>) for
    ///     <c>/tones/search</c> from <paramref name="query"/>. Every optional
    ///     filter is OMITTED rather than sent empty — an empty
    ///     <c>gears=</c>/<c>format=</c> is a filter value, not "no filter".
    ///     Pure, so the whole URL shape unit-tests without a socket.
    /// </summary>
    public static string BuildSearchQueryString(Tone3000SearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var parts = new List<string>(8);
        Add("query", query.Query);
        parts.Add($"page={Math.Max(1, query.Page)}");
        parts.Add($"page_size={Math.Clamp(query.PerPage, 1, MaxPageSize)}");
        Add("sort", SortValue(query.Sort));
        Add("gears", GearTypeValue(query.GearType));
        Add("sizes", query.Size);
        Add("format", query.Format);
        Add("architecture", ArchitectureValue(query.Architecture));
        return string.Join('&', parts);

        void Add(string name, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            parts.Add($"{name}={Uri.EscapeDataString(value.Trim())}");
        }
    }
}
