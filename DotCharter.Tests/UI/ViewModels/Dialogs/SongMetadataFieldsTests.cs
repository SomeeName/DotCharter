using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Editing;
using DotCharter.Services.Import.Audio;
using DotCharter.Services.Online.MusicBrainz;
using DotCharter.Services.Session;
using DotCharter.Tone.Online;

namespace DotCharter.Tests.UI.ViewModels.Dialogs;

/// <summary>
///     The Song metadata dialog's DLC Builder fields (#292): sort names, DLC key and version come
///     prefilled with what a build derives and follow the names until edited; a value left at its
///     default is saved blank; the non-Latin offer moves the name and fills the main one from the
///     official Latin name; and the whole Save stays one undo step.
/// </summary>
public sealed class SongMetadataDialogFieldsTests
{
    private sealed class Names(string artist = "", string title = "") : INewProjectSources
    {
        public AudioMetadataResult ReadTags(string audioFilePath) => AudioMetadataResult.Empty;

        public Task<OnlineResult<LatinNameSuggestion>> FindLatinNamesAsync(string a, string t, string album,
            Action<LatinNameSuggestion> onArtist) =>
            Task.FromResult(artist.Length == 0 && title.Length == 0
                ? OnlineResult<LatinNameSuggestion>.Fail(OnlineError.NotConfigured, "offline")
                : OnlineResult<LatinNameSuggestion>.Success(new LatinNameSuggestion(artist, title, "")));
    }

    private static (SongMetadataViewModel Vm, ChartData Chart, HistoryService History) Rig(ChartData chart, INewProjectSources? sources = null)
    {
        var logger = new FakeAppLogger();
        var history = new HistoryService(logger);
        var state = new ProjectState { ActiveProject = new ChartProject { Chart = chart } };
        return (new SongMetadataViewModel(state, history, FakeLocalization.English(), logger, sources ?? new InertNewProjectSources(), new DotCharter.Tests.Infrastructure.FakeSongKeyAudioEstimator()),
            chart, history);
    }

    private static ChartData Chart() => new() { Artist = "The Polkadot Stingray", SongName = "The Sakasama", AlbumName = "Ice Wall" };

    [Fact]
    public void TheFieldsOpenPrefilledWithTheDerivedValues_AndSaveWithNothingChangedRecordsNothing()
    {
        var (vm, chart, history) = Rig(Chart());

        Assert.Equal(SongMetadataDefaults.ArtistSort(chart.Artist), vm.ArtistSort);
        Assert.Equal(SongMetadataDefaults.TitleSort(chart.SongName), vm.TitleSort);
        Assert.Equal(SongMetadataDefaults.AlbumSort(chart.AlbumName), vm.AlbumSort);
        Assert.Equal(SongMetadataDefaults.DlcKey(chart.Artist, chart.SongName), vm.DlcKey);
        Assert.Equal("1", vm.Version);
        Assert.False(vm.ShowSortFields);
        Assert.False(vm.ShowJapaneseFields);

        vm.SaveCommand.Execute(null);

        Assert.False(history.CanUndo, "an untouched Save wrote the derived values into the project");
        Assert.Equal("", chart.TitleSort);
        Assert.Equal("", chart.DlcKey);
    }

    [Fact]
    public void AnAuthoredSortNameKeyAndVersion_AreSavedInOneUndoStep()
    {
        var (vm, chart, history) = Rig(Chart());

        vm.TitleSort = "Sakasama, The";
        vm.AlbumSort = "Wall, Ice";
        vm.ArtistSort = "Polkadot Stingray, The";
        vm.DlcKey = "DCMyOwnKey1";
        vm.Version = "7";
        vm.Charter = "Zed";
        vm.SaveCommand.Execute(null);

        Assert.Equal("Sakasama, The", chart.TitleSort);
        Assert.Equal("Wall, Ice", chart.AlbumSort);
        Assert.Equal("Polkadot Stingray, The", chart.ArtistSort);
        Assert.Equal("DCMyOwnKey1", chart.DlcKey);
        Assert.Equal("7", chart.Version);
        Assert.Equal("Zed", chart.Charter);

        history.Undo();
        Assert.Equal("", chart.TitleSort);
        Assert.Equal("", chart.DlcKey);
        Assert.Equal("1", chart.Version);
        Assert.False(history.CanUndo, "one Save must be one undo node");
    }

    [Fact]
    public void ADerivedField_FollowsARenameUntilItIsEdited()
    {
        var (vm, _, _) = Rig(Chart());

        vm.SongName = "An Ice Wall";
        Assert.Equal(SongMetadataDefaults.TitleSort("An Ice Wall"), vm.TitleSort);
        Assert.Equal(SongMetadataDefaults.DlcKey(vm.Artist, "An Ice Wall"), vm.DlcKey);

        vm.TitleSort = "Mine";
        vm.SongName = "Another";
        Assert.Equal("Mine", vm.TitleSort);
    }

    [Fact]
    public void AStoredSortNameOpensShown_WithTheSortFieldsOn()
    {
        var chart = Chart();
        chart.TitleSort = "Sakasama, The";

        var (vm, _, _) = Rig(chart);

        Assert.Equal("Sakasama, The", vm.TitleSort);
        Assert.True(vm.ShowSortFields);
    }

    [Fact]
    public async Task TheOfferMovesANonLatinName_AndFillsTheMainOneFromTheOfficialName()
    {
        var chart = new ChartData { Artist = "ヨルシカ", SongName = "八月、某、月明かり" };
        var (vm, _, _) = Rig(chart, new Names("Yorushika", "August, A Certain, Moonlight"));
        Assert.True(vm.HasOriginalScriptOffer);

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.MoveToOriginalScriptCommand).ExecuteAsync(null);

        Assert.Equal("Yorushika", vm.Artist);
        Assert.Equal("ヨルシカ", vm.JapaneseArtist);
        Assert.Equal("August, A Certain, Moonlight", vm.SongName);
        Assert.Equal("八月、某、月明かり", vm.JapaneseTitle);
        Assert.True(vm.ShowJapaneseFields);
        Assert.False(vm.HasOriginalScriptOffer);
    }

    [Fact]
    public async Task WithoutAnOfficialName_TheOfferLeavesTheMainFieldForTheCharterToType()
    {
        var (vm, _, _) = Rig(new ChartData { Artist = "ヨルシカ", SongName = "Dummy" }, new Names());

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.MoveToOriginalScriptCommand).ExecuteAsync(null);

        Assert.Equal("", vm.Artist);
        Assert.Equal("ヨルシカ", vm.JapaneseArtist);
        Assert.Equal("Dummy", vm.SongName);
    }
}
