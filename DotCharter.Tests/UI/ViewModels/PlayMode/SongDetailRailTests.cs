using System.Collections.Specialized;
using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Gameplay.Leaderboards;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Services.Gameplay.Stats;
using DotCharter.Services.Library;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.Panels;
using DotCharter.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI.ViewModels.PlayMode;

/// <summary>
///     The Song Library detail rail: the arrangement tiles (which path the run
///     opens on) and the tuning-check pill.
/// </summary>
public sealed class SongDetailRailTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));

    public SongDetailRailTests() => Directory.CreateDirectory(_dir);

    /// <summary>
    ///     Re-picking the ALREADY selected arrangement must still republish that
    ///     tile. The tile is a <c>ToggleButton</c> whose <c>IsChecked</c> is bound
    ///     ONE-WAY, so its own click flips the visual to unchecked BEFORE the
    ///     command runs; only a fresh notification puts it back. The command used
    ///     to skip the replace when nothing changed, so the tile drew as
    ///     unselected while <c>SelectedArrangement</c> still pointed at it — and
    ///     since the rail pre-selects the first path, that is the very first click
    ///     a player makes.
    /// </summary>
    [Fact]
    public void RePickingTheSelectedArrangement_StillRepublishesItsTile()
    {
        var vm = NewVm();
        vm.SongSelect.SetLibraryForTest([Song("leadbass", "E A D G B E", "Lead,Bass")]);
        vm.SelectedSong = vm.SongSelect.SongList[0];

        Assert.Equal(2, vm.SelectedSongArrangements.Count);
        Assert.True(vm.SelectedSongArrangements[0].IsSelected, "the rail must pre-select the first path");
        var alreadySelected = vm.SelectedSongArrangements[0];

        var replaced = new List<int>();
        ((INotifyCollectionChanged)vm.SelectedSongArrangements).CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Replace) replaced.Add(e.NewStartingIndex);
        };

        vm.SelectArrangementCommand.Execute(alreadySelected);

        Assert.Contains(0, replaced);
        Assert.True(vm.SelectedSongArrangements[0].IsSelected);
        Assert.Equal("Lead", vm.SelectedArrangement?.Label);
    }

    /// <summary>Picking a different path still moves the tick, and moves it off
    /// the old one — the guard against "fix the re-pick by ticking everything".</summary>
    [Fact]
    public void PickingAnotherArrangement_MovesTheTick()
    {
        var vm = NewVm();
        vm.SongSelect.SetLibraryForTest([Song("leadbass", "E A D G B E", "Lead,Bass")]);
        vm.SelectedSong = vm.SongSelect.SongList[0];

        vm.SelectArrangementCommand.Execute(vm.SelectedSongArrangements[1]);

        Assert.False(vm.SelectedSongArrangements[0].IsSelected);
        Assert.True(vm.SelectedSongArrangements[1].IsSelected);
        Assert.Equal("Bass", vm.SelectedArrangement?.Label);
        Assert.Single(vm.SelectedSongArrangements, a => a.IsSelected);
    }

    /// <summary>
    ///     The tuning check has THREE states and the green one is a claim. A song
    ///     the app cannot judge — no tuning on the row, or no tuning verified yet
    ///     — must land in the neutral state, never the in-tune one. The view keyed
    ///     the green pill on <c>!SelectedTuningNeedsChange</c>, so both
    ///     claim-nothing branches were painted IN TUNE beside the Play button.
    /// </summary>
    [Fact]
    public void ASongWithNoTuning_IsNeitherInTuneNorOutOfTune()
    {
        var vm = NewVm();
        // An own chart carries no tuning at all.
        vm.SongSelect.SetLibraryForTest([Song("notuning", tuning: "", paths: "Lead")]);
        vm.SelectedSong = vm.SongSelect.SongList[0];

        Assert.False(vm.SelectedTuningIsVerified, "the screen claimed the guitar is in tune for a tuning it does not know");
        Assert.False(vm.SelectedTuningNeedsChange);
        Assert.True(vm.SelectedTuningIsUnverified);
        Assert.NotEqual("", vm.SelectedTuningCheckLabel);
    }

    /// <summary>A known tuning with nothing verified yet is the same neutral
    /// state: it states the requirement and claims nothing about the guitar.</summary>
    [Fact]
    public void AKnownTuningWithNothingVerified_IsAlsoNeutral()
    {
        var vm = NewVm();
        vm.SongSelect.SetLibraryForTest([Song("standard", tuning: "E A D G B E", paths: "Lead")]);
        vm.SelectedSong = vm.SongSelect.SongList[0];

        Assert.False(vm.SelectedTuningIsVerified);
        Assert.False(vm.SelectedTuningNeedsChange);
        Assert.True(vm.SelectedTuningIsUnverified);
    }

    /// <summary>Exactly one of the three is ever set, and clearing the selection
    /// leaves none of them claiming anything.</summary>
    [Fact]
    public void TheThreeTuningStatesAreMutuallyExclusive()
    {
        var vm = NewVm();
        vm.SongSelect.SetLibraryForTest([Song("standard", tuning: "E A D G B E", paths: "Lead")]);

        foreach (var selection in new PlaySongItem?[] { vm.SongSelect.SongList[0], null })
        {
            vm.SelectedSong = selection;
            var lit = new[]
            {
                vm.SelectedTuningIsVerified, vm.SelectedTuningNeedsChange, vm.SelectedTuningIsUnverified
            }.Count(b => b);
            Assert.True(lit <= 1, "two tuning pills would show at once");
        }

        Assert.False(vm.SelectedTuningIsVerified);
        Assert.False(vm.SelectedTuningNeedsChange);
    }

    private static PlaySongItem Song(string key, string tuning, string paths)
        => PlaySongItem.FromPsarc(new PsarcSongListItem(new LibrarySongEntry
        {
            SongKey = key,
            Title = $"Song {key}",
            Artist = $"Artist {key}",
            Album = "Album",
            Charter = "Charter",
            SongLength = 200,
            TuningSummary = tuning,
            PackagePath = $@"C:\lib\{key}_p.psarc",
            Arrangements = [.. paths.Split(',').Select(p =>
                new LibraryArrangementEntry { ArrangementName = p, SngPath = $"s/{key}-{p}.sng" })]
        }, FakeLocalization.English()));

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
