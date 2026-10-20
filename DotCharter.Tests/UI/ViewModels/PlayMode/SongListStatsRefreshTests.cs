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
///     Play Mode re-reads the song list's stats when a run records and when the
///     player switches profile (<c>RefreshSongStats</c>). It then has to re-run
///     the FILTER, because stats decide MEMBERSHIP and not only order: the
///     played facet is <c>HasBest</c>, the record ladder is
///     <c>HasBest</c>/<c>Grade</c>/<c>IsFullCombo</c>, and the difficulty band is
///     the rating <c>ApplyStats</c> just re-stamped.
///     <para>
///         It used to re-run the pass only for the three stats-driven SORTS
///         (recent / best / difficulty). So with the artist sort — the default —
///         and "Played" ticked, switching profile left the OUTGOING player's
///         played songs listed under the incoming player's name, and the
///         "N of M" readout counted them.
///     </para>
/// </summary>
public sealed class SongListStatsRefreshTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));

    public SongListStatsRefreshTests() => Directory.CreateDirectory(_dir);

    /// <summary>The profile switch. Ann has played one of the two songs; Bo has
    /// played neither, so with "Played" ticked Bo's list must be empty.</summary>
    [Fact]
    public void SwitchingProfile_DropsTheOutgoingPlayersRowsFromThePlayedFacet()
    {
        var profiles = new PlayerProfileService(FakeLocalization.English(), logger: null,
            filePath: Path.Combine(_dir, "player-profiles.json"), defaultProfileName: "Ann");
        var store = new PracticeProfileService(profiles, logger: null,
            legacyFilePath: Path.Combine(_dir, "legacy.json"));
        var stats = new GameplayStatsService(store);
        var vm = TestRigBuilders.PlayMode(profiles, stats,
            new LeaderboardService(profiles), new ProjectState(), localization: FakeLocalization.English());

        var played = Row("ann");
        var never = Row("cold");
        store.Record(played.SongIdentity, "Lead:Lead", Run());
        played.ApplyStats(stats.GetBestForSong(played.SongIdentity));
        Assert.True(played.HasBest, "the fixture never recorded a run — the rest of this test would be vacuous");

        vm.SongSelect.SetLibraryForTest([played, never]);
        vm.SongSelect.PlayedFacet.Options.Single(o => o.Value == "played").IsChecked = true;

        // Sort mode 0 (artist) — the default, and the one that used to skip the pass.
        Assert.Equal(0, vm.SongSelect.SortMode);
        Assert.Single(vm.SongSelect.SongList);
        Assert.Equal("1", vm.SongSelect.SongShownCountText);

        var bo = profiles.Create("Bo");
        profiles.SwitchTo(bo.Id);

        Assert.False(played.HasBest, "the rows were not re-stamped from Bo's store");
        Assert.Empty(vm.SongSelect.SongList);
        Assert.Equal("0", vm.SongSelect.SongShownCountText);
        Assert.Equal("2", vm.SongSelect.SongTotalCountText);
    }

    /// <summary>The same fault through the RECORD ladder rather than the played
    /// facet — an achievement filter is the other membership test stats
    /// decide.</summary>
    [Fact]
    public void SwitchingProfile_DropsTheOutgoingPlayersRowsFromTheRecordLadder()
    {
        var profiles = new PlayerProfileService(FakeLocalization.English(), logger: null,
            filePath: Path.Combine(_dir, "player-profiles.json"), defaultProfileName: "Ann");
        var store = new PracticeProfileService(profiles, logger: null,
            legacyFilePath: Path.Combine(_dir, "legacy.json"));
        var stats = new GameplayStatsService(store);
        var vm = TestRigBuilders.PlayMode(profiles, stats,
            new LeaderboardService(profiles), new ProjectState(), localization: FakeLocalization.English());

        var played = Row("ann");
        store.Record(played.SongIdentity, "Lead:Lead", Run());
        played.ApplyStats(stats.GetBestForSong(played.SongIdentity));

        vm.SongSelect.SetLibraryForTest([played, Row("cold")]);
        // The lowest grade floor — "F or better" — which every recorded run clears
        // and every unplayed song fails.
        vm.SongSelect.RecordFilter = PlayRecordFilter.GradesBestFirst.Count;
        Assert.Single(vm.SongSelect.SongList);

        var bo = profiles.Create("Bo");
        profiles.SwitchTo(bo.Id);

        Assert.Empty(vm.SongSelect.SongList);
    }

    private static LivePlaySessionResult Run()
        => new(1000, 0.9, 90, 10, 100, 20, 120, 100, 1.0);

    private static PlaySongItem Row(string key)
        => PlaySongItem.FromPsarc(new PsarcSongListItem(new LibrarySongEntry
        {
            SongKey = key,
            Title = $"Song {key}",
            Artist = $"Artist {key}",
            Album = "Album",
            Charter = "Charter",
            SongLength = 200,
            TuningSummary = "E A D G B E",
            PackagePath = $@"C:\lib\{key}_p.psarc",
            Arrangements = [new LibraryArrangementEntry { ArrangementName = "Lead", SngPath = $"s/{key}.sng" }]
        }, FakeLocalization.English()));

    public void Dispose() => BestEffortDelete.Directory(_dir);
}
