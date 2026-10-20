using System.Text.RegularExpressions;

namespace DotCharter.Tests.UI.ViewModels.PlayMode;

/// <summary>
///     Three Song Library passes re-parsed each row's arrangement summary — a
///     <c>Split</c>, a <c>List</c>, a <c>Trim</c> per part and a linear de-dupe,
///     per row — for a list the row's own constructor had already parsed once
///     and kept as <c>ArrangementBadges</c>: the PATHS leg of the predicate
///     (every row of a filter pass while that facet is selected), the facet
///     count (every matching row on every open of the PATHS popover) and the
///     option build (the whole library, TWICE, on every rebuild). All three
///     live in the shared engine now (<c>LibraryQuery</c>, <c>LibraryFacets</c>,
///     <c>LibraryFacetOptions</c>), which both song lists call — so the guard
///     reads the engine AND both view models, through <see cref="SongQuerySources" />.
///     <para>
///     Pinned by SOURCE, because the sites are a static predicate and a private
///     view-model method with no seam to instrument, and because the defect is
///     exactly "this file names that call". Each assertion carries an ANCHOR
///     that must still be present, so moving or renaming the code fails the
///     guard loudly instead of leaving it passing over a file that no longer
///     contains its subject. Behaviour is covered by the existing
///     <see cref="PlaySongFilterTests" /> Paths cases.
///     </para>
/// </summary>
public sealed class SongListFilterCostTests
{
    [Fact]
    public void ThePerRowFilterReadsTheRowsCachedBadgesInsteadOfReparsingItsSummary()
    {
        var filter = Stripped(SongQuerySources.Engine("LibraryQuery.cs"));

        // Anchor: this is still the file with the PATHS leg in it.
        Assert.Contains("c.Paths is { Count: > 0 }", filter, StringComparison.Ordinal);
        Assert.Contains("song.ArrangementBadges", filter, StringComparison.Ordinal);
        Assert.DoesNotContain("ParseArrangementBadges", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFacetCountAndOptionPassesReadTheCachedBadgesToo()
    {
        // The count reads a row's buckets through ValuesOf, the options build its PATHS list.
        var facets = Stripped(SongQuerySources.Engine("LibraryFacets.cs"));
        var options = Stripped(SongQuerySources.Engine("LibraryFacetOptions.cs"));
        var counts = Stripped(SongQuerySources.Engine("LibraryQuery.cs"));

        Assert.Contains("LibraryFacets.ValuesOf(song, facet)", counts, StringComparison.Ordinal);
        Assert.Contains("song.ArrangementBadges", facets, StringComparison.Ordinal);
        Assert.Contains("song => song.ArrangementBadges", options, StringComparison.Ordinal);
        foreach (var source in new[] { facets, options, counts })
            Assert.DoesNotContain("ParseArrangementBadges", source, StringComparison.Ordinal);
    }

    /// <summary>…and neither view model carries a pass of its own to re-parse in:
    /// both build their options and counts through the engine.</summary>
    [Fact]
    public void BothViewModels_BuildOptionsAndCountsThroughTheEngine()
    {
        var library = Stripped(SongQuerySources.LibraryViewModel());
        var songSelect = Stripped(string.Join('\n', SongQuerySources.SongSelectViewModel().Select(f => f.Source)));

        foreach (var (name, source) in new[] { ("SongBrowserViewModel", library), ("PlayModeViewModel", songSelect) })
        {
            Assert.True(source.Contains("LibraryFacetOptions.For(facet.Key", StringComparison.Ordinal), $"{name}: options");
            Assert.True(source.Contains("LibraryQuery.CountFacet(", StringComparison.Ordinal), $"{name}: counts");
            Assert.DoesNotContain("ParseArrangementBadges", source, StringComparison.Ordinal);
        }
    }

    /// <summary>Comments stripped, because a <c>see cref</c> or a note saying what
    /// the code no longer does is exactly as inert as a deleted call — the same
    /// reason the reachability lints strip them.</summary>
    private static string Stripped(string text)
    {
        text = Regex.Replace(text, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        return Regex.Replace(text, @"//[^\r\n]*", " ");
    }
}
