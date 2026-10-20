using System.Reflection;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;

namespace DotCharter.Tests.UI.ViewModels.Dialogs;

/// <summary>
///     The two metadata dialogs ask only for dates something exports (#218). Rocksmith's
///     manifest takes <c>SongYear</c>; no
///     exporter reads <c>ChartData.ReleaseDate</c>, so neither screen shows it. The value a
///     file's tags carried still lives in the project: a save that never showed it must not
///     drop it.
/// </summary>
public sealed class SongMetadataReleaseDateTests
{
    [Theory]
    [InlineData(typeof(ISongMetadataScreen))]
    [InlineData(typeof(INewProjectScreen))]
    public void NeitherScreen_AsksForAReleaseDate(Type contract)
    {
        Assert.Null(contract.GetProperty("ReleaseDate", BindingFlags.Public | BindingFlags.Instance));
        // …and both ask for the year, the date the exports DO read.
        Assert.NotNull(contract.GetProperty("AlbumYear", BindingFlags.Public | BindingFlags.Instance));
    }

    [Fact]
    public void Save_KeepsTheStoredReleaseDate()
    {
        var logger = new FakeAppLogger();
        var state = new ProjectState();
        var project = new ChartProject
        {
            Chart = new ChartData { Artist = "Muse", SongName = "Knights of Cydonia", AlbumYear = "2006", ReleaseDate = "2006-07-03" }
        };
        state.ActiveProject = project;
        var vm = new SongMetadataViewModel(state, new HistoryService(logger), FakeLocalization.English(), logger)
        {
            AlbumName = "Black Holes and Revelations"
        };

        vm.SaveCommand.Execute(null);

        Assert.Equal("Black Holes and Revelations", project.Chart.AlbumName);
        Assert.Equal("2006-07-03", project.Chart.ReleaseDate);
    }
}
