using DotCharter.Services.Gameplay.Leaderboards;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Services.Gameplay.Setlists;
using DotCharter.Services.Gameplay.Stats;
using DotCharter.Services.Audio.LivePlay;
using DotCharter.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI.ViewModels.PlayMode;

/// <summary>
///     Play Mode setlists (#646) driven through the REAL <see cref="PlayModeViewModel" />
///     — its song loader, its run exits and its screen — over own-chart project
///     paths, so the flow is the one the app runs: start, Next from Results, Skip
///     and End from Pause, the last song's Results, a missing song skipped and
///     named, and walking away ending the setlist.
/// </summary>
public sealed class PlaySetlistFlowTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DotCharterTests", "setlist-flow-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Start_OpensTheFirstSongsGetReadyOnItsPathAndAtItsSpeed()
    {
        var (vm, _) = Create();
        vm.PracticeSpeedPercent = 90;
        UseList(vm, Entry("first", "Bass", 80), Entry("second", "Lead"));

        await vm.Setlists.StartSetlistCommand.ExecuteAsync(null);

        Assert.True(vm.Setlists.IsRunning);
        Assert.Equal(PlayModeScreen.TuningCheck, vm.CurrentScreen);
        Assert.Equal("first", vm.FlowSongTitle);
        Assert.Equal("Bass", vm.SelectedFlowArrangement!.Name);
        Assert.Equal(80, vm.PracticeSpeedPercent);
        Assert.Equal("Gig · song 1 of 2", vm.Setlists.ProgressLabel);
        Assert.Empty(vm.Setlists.SkippedNote);
    }

    [Fact]
    public async Task NextFromResults_OpensTheNextSong_AndTheLastSongsResultsSayTheSetlistIsDone()
    {
        var (vm, _) = Create();
        vm.PracticeSpeedPercent = 90;
        UseList(vm, Entry("first", "Bass", 80), Entry("second", "Lead"));
        await vm.Setlists.StartSetlistCommand.ExecuteAsync(null);

        vm.CurrentScreen = PlayModeScreen.Results;
        Assert.True(vm.Setlists.HasNext);
        Assert.False(vm.Setlists.IsFinished);
        Assert.Equal("Next: second", vm.Setlists.NextLabel);

        await vm.Setlists.NextSetlistSongCommand.ExecuteAsync(null);

        Assert.True(vm.Setlists.IsRunning, "Next passes through Song Select without ending the setlist");
        Assert.Equal(PlayModeScreen.TuningCheck, vm.CurrentScreen);
        Assert.Equal("second", vm.FlowSongTitle);
        Assert.Equal("Lead", vm.SelectedFlowArrangement!.Name);
        Assert.Equal(100, vm.PracticeSpeedPercent);
        Assert.Equal("Gig · song 2 of 2", vm.Setlists.ProgressLabel);

        vm.CurrentScreen = PlayModeScreen.Results;
        Assert.False(vm.Setlists.HasNext);
        Assert.True(vm.Setlists.IsFinished);
        Assert.Equal("Setlist complete · 2 / 2 played", vm.Setlists.FinishedLabel);

        vm.BackToSongSelectCommand.Execute(null);

        Assert.False(vm.Setlists.IsRunning);
        Assert.Equal(90, vm.PracticeSpeedPercent);
    }

    [Fact]
    public async Task SkipFromPause_AbandonsTheRunAndOpensTheNextSong()
    {
        var (vm, _) = Create();
        UseList(vm, Entry("first", "Lead"), Entry("second", "Bass"));
        await vm.Setlists.StartSetlistCommand.ExecuteAsync(null);
        vm.CurrentScreen = PlayModeScreen.Paused;

        await vm.Setlists.SkipSetlistSongCommand.ExecuteAsync(null);

        Assert.True(vm.Setlists.IsRunning);
        Assert.Equal(PlayModeScreen.TuningCheck, vm.CurrentScreen);
        Assert.Equal("second", vm.FlowSongTitle);
        Assert.Equal("Bass", vm.SelectedFlowArrangement!.Name);
    }

    [Fact]
    public async Task SkippingTheLastSong_EndsTheSetlistOnSongSelect()
    {
        var (vm, _) = Create();
        UseList(vm, Entry("only", "Lead"));
        await vm.Setlists.StartSetlistCommand.ExecuteAsync(null);
        vm.CurrentScreen = PlayModeScreen.Paused;

        await vm.Setlists.SkipSetlistSongCommand.ExecuteAsync(null);

        Assert.False(vm.Setlists.IsRunning);
        Assert.Equal(PlayModeScreen.SongSelect, vm.CurrentScreen);
    }

    [Fact]
    public async Task EndFromPause_LeavesForSongSelectAndRestoresTheSpeed()
    {
        var (vm, _) = Create();
        vm.PracticeSpeedPercent = 70;
        UseList(vm, Entry("first", "Lead", 50), Entry("second", "Lead"));
        await vm.Setlists.StartSetlistCommand.ExecuteAsync(null);
        Assert.Equal(50, vm.PracticeSpeedPercent);
        vm.CurrentScreen = PlayModeScreen.Paused;

        vm.Setlists.EndSetlistCommand.Execute(null);

        Assert.False(vm.Setlists.IsRunning);
        Assert.Equal(PlayModeScreen.SongSelect, vm.CurrentScreen);
        Assert.Equal(70, vm.PracticeSpeedPercent);
        Assert.Empty(vm.Setlists.ProgressLabel);
    }

    [Fact]
    public async Task AMissingSong_IsSkippedAndNamedOnTheNextGetReady()
    {
        var (vm, session) = Create();
        UseList(vm, Entry("first", "Lead"), Entry("gone", "Lead", exists: false), Entry("third", "Bass"));
        await vm.Setlists.StartSetlistCommand.ExecuteAsync(null);
        vm.CurrentScreen = PlayModeScreen.Results;

        await vm.Setlists.NextSetlistSongCommand.ExecuteAsync(null);

        Assert.True(vm.Setlists.IsRunning);
        Assert.Equal(PlayModeScreen.TuningCheck, vm.CurrentScreen);
        Assert.Equal("third", vm.FlowSongTitle);
        Assert.Equal("Skipped, not in your library: gone", vm.Setlists.SkippedNote);
        Assert.Equal("Gig · song 3 of 3", vm.Setlists.ProgressLabel);
        Assert.DoesNotContain(session.Loaded, p => p.Contains("gone", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhenNothingAfterAMissingSongOpens_TheSetlistEndsAndSongSelectNamesIt()
    {
        var (vm, _) = Create();
        UseList(vm, Entry("first", "Lead"), Entry("gone", "Lead", exists: false));
        await vm.Setlists.StartSetlistCommand.ExecuteAsync(null);
        vm.CurrentScreen = PlayModeScreen.Results;

        await vm.Setlists.NextSetlistSongCommand.ExecuteAsync(null);

        Assert.False(vm.Setlists.IsRunning);
        Assert.Equal(PlayModeScreen.SongSelect, vm.CurrentScreen);
        Assert.Equal("Setlist ended. Not in your library: gone", vm.Setlists.Notice);
    }

    [Fact]
    public async Task LeavingForSongSelectAnyOtherWay_EndsTheSetlist()
    {
        var (vm, _) = Create();
        UseList(vm, Entry("first", "Lead"), Entry("second", "Lead"));
        await vm.Setlists.StartSetlistCommand.ExecuteAsync(null);

        vm.BackCommand.Execute(null);

        Assert.Equal(PlayModeScreen.SongSelect, vm.CurrentScreen);
        Assert.False(vm.Setlists.IsRunning);
        Assert.False(vm.Setlists.HasNext);
    }

    [Fact]
    public async Task AddToSetlist_TakesTheSelectedSongAndSpeed_AndIsSavedForTheProfile()
    {
        Directory.CreateDirectory(_dir);
        var profiles = new PlayerProfileService(FakeLocalization.English(), null, Path.Combine(_dir, "profiles.json"), "Tester");
        var store = new PlaySetlistStore(id => Path.Combine(_dir, "data", id.ToString("N")));
        var setlists = new PlaySetlistViewModel(FakeLocalization.English(), store, profiles);
        var (vm, _) = Create(profiles, setlists);
        await setlists.LoadAsync();
        var path = Chart("picked");
        vm.SelectedSong = PlaySongItem.FromProject(path, true, FakeLocalization.English());
        vm.PracticeSpeedPercent = 60;

        Assert.True(setlists.CanAddSong);
        setlists.AddSongToSetlistCommand.Execute(null);
        await setlists.WhenSaved;

        var list = Assert.Single(setlists.Lists);
        Assert.Equal("Setlist 1", list.Name);
        var row = Assert.Single(setlists.Entries);
        Assert.Equal("picked", row.Title);
        var read = new PlaySetlistStore(id => Path.Combine(_dir, "data", id.ToString("N"))).Load(profiles.ActiveProfile.Id);
        var saved = Assert.Single(Assert.Single(read.Setlists).Entries);
        Assert.Equal(path, saved.SongKey);
        Assert.Equal(60, saved.SpeedPercent);
        Assert.Equal("picked", saved.Title);
    }

    [Fact]
    public void ARename_IsTrimmedAndReannouncedToThePicker_AndABlankNameIsPutBack()
    {
        var (vm, _) = Create();
        UseList(vm, Entry("first", "Lead"));
        var list = vm.Setlists.SelectedList!;
        var announced = 0;
        list.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlaySetlistOption.Name)) announced++;
        };

        vm.Setlists.SelectedListName = "  Saturday gig ";

        Assert.Equal("Saturday gig", list.Name);
        Assert.Equal("Saturday gig", list.Setlist.Name);
        Assert.Equal("Saturday gig", vm.Setlists.SelectedListName);
        Assert.Same(list, vm.Setlists.SelectedList);
        Assert.Equal(1, announced);

        vm.Setlists.SelectedListName = "   ";

        Assert.Equal("Saturday gig", list.Name);
        Assert.Equal("Saturday gig", vm.Setlists.SelectedListName);
        Assert.Equal(1, announced);
    }

    [Fact]
    public async Task AnUnreadableStore_IsReportedOnSongSelect()
    {
        Directory.CreateDirectory(_dir);
        var profiles = new PlayerProfileService(FakeLocalization.English(), null, Path.Combine(_dir, "profiles.json"), "Tester");
        var folder = Path.Combine(_dir, "data", profiles.ActiveProfile.Id.ToString("N"));
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, PlaySetlistStore.StoreFileName);
        File.WriteAllText(file, "{\"Setlists\":[]}");
        var setlists = new PlaySetlistViewModel(FakeLocalization.English(),
            new PlaySetlistStore(id => Path.Combine(_dir, "data", id.ToString("N"))), profiles);

        using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await setlists.LoadAsync();

        Assert.Equal(FakeLocalization.English()["PlayMode_Setlist_Unreadable"], setlists.Notice);
    }

    // ── harness ────────────────────────────────────────────────────────────

    private string Chart(string name, bool exists = true)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, name + ".rscp");
        if (exists) File.WriteAllText(path, "");
        return path;
    }

    private PlaySetlistEntry Entry(string name, string arrangement, int speed = 100, bool exists = true) =>
        new() { SongKey = Chart(name, exists), Arrangement = arrangement, SpeedPercent = speed, Title = name };

    private static void UseList(PlayModeViewModel vm, params PlaySetlistEntry[] entries)
    {
        var list = new PlaySetlistOption(new PlaySetlist { Name = "Gig", Entries = [.. entries] });
        vm.Setlists.Lists.Add(list);
        vm.Setlists.SelectedList = list;
    }

    private (PlayModeViewModel Vm, TwoPathChartSession Session) Create(
        PlayerProfileService? profiles = null, PlaySetlistViewModel? setlists = null)
    {
        Directory.CreateDirectory(_dir);
        profiles ??= new PlayerProfileService(FakeLocalization.English(), null, Path.Combine(_dir, "profiles.json"), "Tester");
        var practice = new PracticeProfileService(profiles, null, Path.Combine(_dir, "legacy.json"));
        var state = new ProjectState();
        var session = new TwoPathChartSession(state);
        var vm = TestRigBuilders.PlayMode(profiles, new GameplayStatsService(practice), new LeaderboardService(profiles),
            state, FakeLocalization.English(), projectSession: session, setlists: setlists);
        return (vm, session);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }
}
