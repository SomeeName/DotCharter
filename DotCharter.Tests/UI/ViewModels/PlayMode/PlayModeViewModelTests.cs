using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Gameplay.Leaderboards;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Services.Gameplay.Scoring;
using DotCharter.Services.Gameplay.Stats;
using DotCharter.Services.Library;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.Panels;
using DotCharter.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI.ViewModels.PlayMode;

public sealed class PlayModeViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));

    private PlayModeViewModel NewVm(out PlayerProfileService profiles)
    {
        profiles = new PlayerProfileService(FakeLocalization.English(), logger: null,
            filePath: Path.Combine(_dir, "player-profiles.json"), defaultProfileName: "Tester");
        var store = new PracticeProfileService(profiles, logger: null,
            legacyFilePath: Path.Combine(_dir, "legacy.json"));
        return TestRigBuilders.PlayMode(profiles, new GameplayStatsService(store),
            new LeaderboardService(profiles), new ProjectState(), localization: FakeLocalization.English());
    }

    [Fact]
    public void StartsOnStartScreen()
    {
        var vm = NewVm(out _);
        Assert.Equal(PlayModeScreen.Start, vm.CurrentScreen);
        Assert.True(vm.IsStartScreen);
    }

    [Fact]
    public void UseInGameTones_DefaultsOff_AndTogglePersists()
    {
        var profiles = new PlayerProfileService(FakeLocalization.English(), logger: null,
            filePath: Path.Combine(_dir, "player-profiles.json"), defaultProfileName: "Tester");
        var store = new PracticeProfileService(profiles, logger: null,
            legacyFilePath: Path.Combine(_dir, "legacy.json"));
        var config = new FakeConfigurationService();
        var vm = TestRigBuilders.PlayMode(profiles, new GameplayStatsService(store),
            new LeaderboardService(profiles), new ProjectState(), FakeLocalization.English(), config);

        Assert.False(vm.UseInGameTones);            // off by default
        Assert.False(vm.HasInGameTones);            // no coordinator ⇒ card hidden, no crash

        vm.UseInGameTones = true;
        Assert.True(config.AppConfig.UseInGameTones); // persisted app-wide
    }

    /// <summary>An unplayed library song with no stats still shows the difficulty
    /// the index cached on first load, via the ApplyStats fallback.</summary>
    private static PlaySongItem RatedSong(string title, double difficulty)
    {
        var entry = new LibrarySongEntry
        {
            Title = title,
            SongKey = title,
            Difficulty = difficulty,
            Arrangements = [new LibraryArrangementEntry { RouteMask = 1, SngPath = "a.sng" }]
        };
        var item = PlaySongItem.FromPsarc(new PsarcSongListItem(entry, FakeLocalization.English()));
        item.ApplyStats(null); // unplayed → Difficulty falls back to the index rating
        return item;
    }

    [Fact]
    public void UnplayedSong_ShowsIndexRating_ViaApplyStatsFallback()
    {
        var rated = RatedSong("Hard Song", 8.2);
        Assert.True(rated.HasDifficulty);
        Assert.Equal(8.2, rated.Difficulty, 3);
        Assert.Equal(8.2.ToString("0.0"), rated.DifficultyLabel); // one decimal, current culture

        var unrated = RatedSong("Unknown", 0);
        Assert.False(unrated.HasDifficulty);
        Assert.Equal("", unrated.DifficultyLabel);
    }

    [Fact]
    public void FullComboBadge_ProjectsFromStats_AndOnlyWhenEarned()
    {
        var entry = new LibrarySongEntry
        {
            Title = "Song",
            SongKey = "sk",
            Arrangements = [new LibraryArrangementEntry { RouteMask = 1, SngPath = "a.sng" }]
        };
        var item = PlaySongItem.FromPsarc(new PsarcSongListItem(entry, FakeLocalization.English()));

        item.ApplyStats(new SongPlayStats("sk", "Lead", 3, 1000, 0.99, 0.9,
            LetterGrade.S, DateTime.UtcNow, "Song", FullCombo: true));
        Assert.True(item.IsFullCombo);

        item.ApplyStats(new SongPlayStats("sk", "Lead", 3, 1000, 0.95, 0.9,
            LetterGrade.A, DateTime.UtcNow, "Song", FullCombo: false));
        Assert.False(item.IsFullCombo); // played but not a full combo

        item.ApplyStats(null);
        Assert.False(item.IsFullCombo); // unplayed
    }

    [Fact]
    public void SongSelect_SortsAndFiltersByDifficulty()
    {
        var vm = NewVm(out _);
        vm.SongSelect.SetLibraryForTest(
        [
            RatedSong("Easy", 2.0),
            RatedSong("Hard", 8.0),
            RatedSong("Unrated", 0),
            RatedSong("Medium", 5.0)
        ]);

        // Sort by difficulty (SortMode 4): hardest first, unrated (0) last.
        vm.SongSelect.SortMode = 4;
        Assert.Equal(["Hard", "Medium", "Easy", "Unrated"], vm.SongSelect.SongList.Select(s => s.Title));

        // Hard band keeps exactly the hard song — the unrated one is the ""
        // bucket the user can ADD, not a silent pass (the unified facet policy).
        var hard = vm.SongSelect.DifficultyFacet.Options.Single(o => o.Value == "hard");
        var unrated = vm.SongSelect.DifficultyFacet.Options.Single(o => o.Value == "");
        hard.IsChecked = true;
        Assert.Equal(["Hard"], vm.SongSelect.SongList.Select(s => s.Title));

        unrated.IsChecked = true;
        Assert.Equal(["Hard", "Unrated"], vm.SongSelect.SongList.Select(s => s.Title).Order());

        // …and switching bands re-narrows through the same facet.
        hard.IsChecked = false;
        unrated.IsChecked = false;
        vm.SongSelect.DifficultyFacet.Options.Single(o => o.Value == "easy").IsChecked = true;
        Assert.Equal(["Easy"], vm.SongSelect.SongList.Select(s => s.Title));

        // Clearing the facet restores the full list.
        vm.SongSelect.DifficultyFacet.Clear();
        Assert.Equal(4, vm.SongSelect.SongList.Count);
    }

    [Fact]
    public void FailedScreen_EscIsTheQuitPath_ToSongSelect()
    {
        // The rock-meter fail-out lands on the Failed screen with the run
        // already ended; Esc routes through NavigateBack, which must take the
        // quit path (song select), never resume or results.
        var vm = NewVm(out _);
        vm.CurrentScreen = PlayModeScreen.Failed;
        Assert.True(vm.IsFailedScreen);

        Assert.True(vm.NavigateBack());
        Assert.True(vm.IsSongSelectScreen);
    }

    [Fact]
    public void Backdrop_HiddenBehindFreezeFrameScreens()
    {
        // Paused AND Failed float over a frozen-highway capture — the animated
        // backdrop must park behind both (its timer stops when hidden).
        var vm = NewVm(out _);

        vm.CurrentScreen = PlayModeScreen.SongSelect;
        Assert.True(vm.IsBackdropVisible);

        vm.CurrentScreen = PlayModeScreen.Paused;
        Assert.False(vm.IsBackdropVisible);

        vm.CurrentScreen = PlayModeScreen.Failed;
        Assert.False(vm.IsBackdropVisible);
    }

    /// <summary>Start hands the player to the shell's Home (the Play Mode menu was
    /// removed on 2026-09-22); the screen enum no longer names it.</summary>
    [Fact]
    public void Start_HandsThePlayerToHome_AndThereIsNoMenuScreen()
    {
        var vm = NewVm(out _);
        var home = 0;
        vm.HomeRequested += () => home++;

        vm.EnterHomeCommand.Execute(null);

        Assert.Equal(1, home);
        Assert.True(vm.IsStartScreen);
        Assert.DoesNotContain("Menu", Enum.GetNames<PlayModeScreen>());
    }

    [Fact]
    public void NavigateBack_FromSongSelect_GoesHome_AndReportsUnhandledOnStart()
    {
        var vm = NewVm(out _);
        var home = 0;
        vm.HomeRequested += () => home++;
        vm.OpenSongSelectCommand.Execute(null);

        Assert.True(vm.NavigateBack());
        Assert.Equal(1, home);

        vm.CurrentScreen = PlayModeScreen.Start;
        Assert.False(vm.NavigateBack());
        Assert.True(vm.IsStartScreen);
    }

    [Fact]
    public void ThePauseMenusHome_AbandonsTheRun_ThenGoesHome()
    {
        var vm = NewVm(out _);
        var home = 0;
        vm.HomeRequested += () => home++;
        vm.CurrentScreen = PlayModeScreen.Paused;

        vm.AbortToHomeCommand.Execute(null);

        Assert.Equal(1, home);
        Assert.True(vm.IsSongSelectScreen);
    }

    /// <summary>The History and Trends screens (the Progress hub) were removed
    /// on 2026-09-19: the screen enum no longer names them, so nothing can
    /// navigate there.</summary>
    [Fact]
    public void TheProgressHubScreens_AreGone()
    {
        var names = Enum.GetNames<PlayModeScreen>();
        Assert.DoesNotContain("History", names);
        Assert.DoesNotContain("Trends", names);
    }

    [Fact]
    public void ResultsScreen_NavigatesBackToSongSelect()
    {
        var vm = NewVm(out _);
        vm.CurrentScreen = PlayModeScreen.Results;
        Assert.True(vm.NavigateBack());
        Assert.True(vm.IsSongSelectScreen);
    }

    [Fact]
    public void ActiveProfileName_FollowsProfileSwitch()
    {
        var vm = NewVm(out var profiles);
        // The default profile has no custom name and reads the localized "Player 1".
        Assert.Equal(FakeLocalization.English()["Profile_DefaultName"], vm.ActiveProfileName);

        var second = profiles.Create("Zwei");
        profiles.SwitchTo(second.Id);
        Assert.Equal("Zwei", vm.ActiveProfileName);
    }

    public void Dispose() => BestEffortDelete.Directory(_dir);
}
