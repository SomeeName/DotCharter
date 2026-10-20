namespace DotCharter.Presentation.Shared.Library;

/// <summary>Every filter control, snapshotted once per pass for <see cref="LibraryQuery.Matches" />. A facet
/// is a selection of <see cref="LibraryFacets" /> keys: null or empty is inert. Search is trimmed;
/// Record indexes the <see cref="PlayRecordFilter" /> ladder (0 = any).</summary>
public readonly record struct LibraryFilter(string Search)
{
    public IReadOnlyCollection<string>? Paths { get; init; }
    public IReadOnlyCollection<string>? Tunings { get; init; }
    public IReadOnlyCollection<string>? Capos { get; init; }
    public IReadOnlyCollection<string>? Lengths { get; init; }
    public IReadOnlyCollection<string>? Difficulties { get; init; }
    public IReadOnlyCollection<string>? PlayedStates { get; init; }
    public IReadOnlyCollection<string>? Origins { get; init; }
    public IReadOnlyCollection<string>? Genres { get; init; }
    public IReadOnlyCollection<string>? Years { get; init; }
    public IReadOnlyCollection<string>? Statuses { get; init; }
    public int Record { get; init; }
    public bool HideLessons { get; init; }

    /// <summary>Something narrows the list: the search, a facet or the record ladder.</summary>
    public bool IsNarrowing =>
        Search.Length > 0 || Active(Paths) || Active(Tunings) || Active(Capos) ||
        Active(Lengths) || Active(Difficulties) || Active(PlayedStates) ||
        Active(Origins) || Active(Genres) || Active(Years) || Active(Statuses) || Record > 0;

    /// <summary>The filter over a set of facets, by key.</summary>
    public static LibraryFilter FromFacets(
        string search, IEnumerable<(string Key, IReadOnlyCollection<string> Selected)> facets,
        int record = 0, bool hideLessons = false)
    {
        var filter = new LibraryFilter(search.Trim()) { Record = record, HideLessons = hideLessons };
        foreach (var (key, selected) in facets) filter = filter.With(key, selected);
        return filter;
    }

    /// <summary>This filter with one facet's selection replaced; null makes it inert.</summary>
    public LibraryFilter With(string facet, IReadOnlyCollection<string>? selected) => facet switch
    {
        LibraryFacets.Path => this with { Paths = selected },
        LibraryFacets.Tuning => this with { Tunings = selected },
        LibraryFacets.Capo => this with { Capos = selected },
        LibraryFacets.Length => this with { Lengths = selected },
        LibraryFacets.Difficulty => this with { Difficulties = selected },
        LibraryFacets.Played => this with { PlayedStates = selected },
        LibraryFacets.Origin => this with { Origins = selected },
        LibraryFacets.Genre => this with { Genres = selected },
        LibraryFacets.Year => this with { Years = selected },
        LibraryFacets.Status => this with { Statuses = selected },
        _ => throw new ArgumentOutOfRangeException(nameof(facet), facet, "not a library facet")
    };

    /// <summary>Every other filter applied, this facet inert — what a facet's counts are taken over.</summary>
    public LibraryFilter Without(string facet) => With(facet, null);

    private static bool Active(IReadOnlyCollection<string>? selection) => selection is { Count: > 0 };
}
