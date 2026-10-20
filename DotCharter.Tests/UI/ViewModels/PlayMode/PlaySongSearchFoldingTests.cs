using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Gameplay.Leaderboards;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Services.Gameplay.Stats;
using DotCharter.Services.Library;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.Panels;
using DotCharter.UI.ViewModels.PlayMode;
using DotCharter.Presentation.Shared.Library;

namespace DotCharter.Tests.UI.ViewModels.PlayMode;

/// <summary>
///     Song Select's free-text search folds ACCENTS as well as case.
///     <para>
///         It used to compare with <c>StringComparison.CurrentCultureIgnoreCase</c>,
///         which folds case and nothing else: to that comparer "o" and "ö" are
///         different letters, so every accented artist in the library —
///         Motörhead, Björk, Mötley Crüe, Sigur Rós — was reachable ONLY by
///         typing the accent. The row PRINTS the accent, so nothing on screen
///         said why "motorhead" found nothing.
///     </para>
///     <para>
///         Both sides go through <see cref="LibraryQuery.Fold" /> (the row's
///         haystack once per row, the query once per pass) and the per-row test
///         is then an ordinal <c>Contains</c>.
///     </para>
/// </summary>
public sealed class PlaySongSearchFoldingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));

    public PlaySongSearchFoldingTests() => Directory.CreateDirectory(_dir);

    [Theory]
    // The query is unaccented, the library row is not.
    [InlineData("Mot\u00f6rhead", "Ace of Spades", "motorhead")]
    [InlineData("Mot\u00f6rhead", "Ace of Spades", "MOTORHEAD")]
    [InlineData("Bj\u00f6rk", "J\u00f3ga", "bjork joga")]
    [InlineData("Sigur R\u00f3s", "Hoppipolla", "sigur ros")]
    [InlineData("Beyonc\u00e9", "Halo", "beyonce")]
    // …and the accented spelling must keep working, in both directions.
    [InlineData("Mot\u00f6rhead", "Ace of Spades", "mot\u00f6rhead")]
    [InlineData("Motorhead", "Ace of Spades", "mot\u00f6rhead")]
    public void AnAccentedRow_IsFoundByTheUnaccentedSpelling(string artist, string title, string query)
        => Assert.True(Matches(Row(artist, title), query),
            $"'{query}' did not find '{artist} - {title}'");

    /// <summary>The guard against a fold so aggressive it matches everything:
    /// folding must not make two DIFFERENT words equal.</summary>
    [Theory]
    [InlineData("Mot\u00f6rhead", "Ace of Spades", "metallica")]
    [InlineData("Bj\u00f6rk", "J\u00f3ga", "bjork zzz")]
    public void FoldingDoesNotWidenTheMatch(string artist, string title, string query)
        => Assert.False(Matches(Row(artist, title), query));

    /// <summary>The fold is an index key, not display text — so it is INVARIANT
    /// case folding, and it leaves scripts that carry no combining marks
    /// alone.</summary>
    [Fact]
    public void TheFoldIsCultureInvariantAndLeavesUnaccentedScriptsAlone()
    {
        Assert.Equal("iii", LibraryQuery.Fold("IiI"));
        Assert.Equal("motorhead", LibraryQuery.Fold("Mot\u00d6rhead"));
        Assert.Equal("\u30e1\u30bf\u30ea\u30ab", LibraryQuery.Fold("\u30e1\u30bf\u30ea\u30ab"));
        Assert.Equal("", LibraryQuery.Fold(""));
    }

    /// <summary>The tokens the pass hoists must be folded the same way the
    /// haystack is, or the hoisted path and the predicate's own fallback
    /// disagree — which is how a "fast path" quietly changes results.</summary>
    [Fact]
    public void TheHoistedTokensAreFolded_AndTheSharedSplitterIsUntouched()
    {
        Assert.Equal(["motorhead", "ace"], LibraryQuery.TokenizeFolded("Mot\u00f6rhead Ace"));
        // The raw splitter is UNCHANGED: the editor's song browser shares it and
        // keeps a raw haystack with a culture-aware compare.
        Assert.Equal(["Mot\u00f6rhead", "Ace"], LibraryQuery.Tokenize("Mot\u00f6rhead Ace"));
    }

    /// <summary>End to end through the real list: the row is in the library, the
    /// unaccented query keeps it, and the "N of M" readout agrees.</summary>
    [Fact]
    public void TheSongList_KeepsAnAccentedRowForAnUnaccentedQuery()
    {
        var vm = NewVm();
        vm.SongSelect.SetLibraryForTest([Row("Mot\u00f6rhead", "Ace of Spades"), Row("Metallica", "One")]);

        vm.SongSelect.SearchText = "motorhead";
        vm.SongSelect.FilterPass.RunImmediately();

        Assert.Single(vm.SongSelect.SongList);
        Assert.Equal("Mot\u00f6rhead", vm.SongSelect.SongList[0].Artist);
        Assert.Equal("1", vm.SongSelect.SongShownCountText);
        Assert.Equal("2", vm.SongSelect.SongTotalCountText);
    }

    private static bool Matches(PlaySongItem song, string query)
        => LibraryQuery.Matches(song, new LibraryFilter(query));

    private static PlaySongItem Row(string artist, string title)
    {
        var key = title.Replace(" ", "", StringComparison.Ordinal);
        return PlaySongItem.FromPsarc(new PsarcSongListItem(new LibrarySongEntry
        {
            SongKey = key,
            Title = title,
            Artist = artist,
            Album = "Album",
            Charter = "Charter",
            SongLength = 200,
            TuningSummary = "E A D G B E",
            PackagePath = $@"C:\lib\{key}_p.psarc",
            Arrangements = [new LibraryArrangementEntry { ArrangementName = "Lead", SngPath = "s/1.sng" }]
        }, FakeLocalization.English()));
    }

    private PlayModeViewModel NewVm()
    {
        var profiles = new PlayerProfileService(FakeLocalization.English(), logger: null,
            filePath: Path.Combine(_dir, "player-profiles.json"), defaultProfileName: "Tester");
        var store = new PracticeProfileService(profiles, logger: null,
            legacyFilePath: Path.Combine(_dir, "legacy.json"));
        return TestRigBuilders.PlayMode(profiles, new GameplayStatsService(store),
            new LeaderboardService(profiles), new ProjectState(), localization: FakeLocalization.English());
    }

    public void Dispose() => BestEffortDelete.Directory(_dir);
}
