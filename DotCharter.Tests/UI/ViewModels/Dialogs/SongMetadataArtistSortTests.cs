using DotCharter.Presentation.Screens.Dialogs;

namespace DotCharter.Tests.UI.ViewModels.Dialogs;

/// <summary>
///     Song Metadata has no <c>ArtistSort</c> FIELD, and it was authoring one
///     anyway: <c>after</c> carried <c>ArtistSort = Artist</c>, so simply opening
///     the dialog and pressing Save overwrote the sort name an RS-XML or psarc
///     import had read (<c>RsXmlSongReader</c>). "Beatles, The" became "The
///     Beatles" and the song sorted under T in game.
///     <para>
///         Nothing needed the copy. <c>RsXmlSongWriter.FirstNonBlank</c> and
///         <c>RscpWriter</c> both fall back to the artist when the sort name is
///         blank, and the package library source imports it EMPTY on purpose —
///         <c>docs/agents/services/parsing-psarc.md</c> says so in as many words.
///         The action's <c>HasEffect</c> is <c>before != after</c>, so the write
///         was not even a no-op: it fired for a Save with no edits at all.
///     </para>
/// </summary>
public sealed class SongMetadataArtistSortTests
{
    private static (SongMetadataViewModel Vm, ChartData Chart, HistoryService History) Rig(
        string artist, string artistSort)
    {
        var logger = new FakeAppLogger();
        var history = new HistoryService(logger);
        var state = new ProjectState();
        var project = new ChartProject
        {
            Chart = new ChartData { Artist = artist, ArtistSort = artistSort, SongName = "Come Together" }
        };
        state.ActiveProject = project;
        return (new SongMetadataViewModel(state, history, FakeLocalization.English(), logger),
            project.Chart, history);
    }

    /// <summary>THE regression: an authored sort name survives a Save that did not
    ///     touch it.</summary>
    [Fact]
    public void Save_WithNoEdits_KeepsAnImportedSortName()
    {
        var (vm, chart, _) = Rig("The Beatles", "Beatles, The");

        vm.SaveCommand.Execute(null);

        Assert.Equal("Beatles, The", chart.ArtistSort);
    }

    /// <summary>…and it survives an edit to a NEIGHBOURING field, which is the way
    ///     the loss actually happened — nobody opens this dialog to change nothing.</summary>
    [Fact]
    public void Save_AfterEditingTheAlbum_StillKeepsTheSortName()
    {
        var (vm, chart, _) = Rig("The Beatles", "Beatles, The");

        vm.AlbumName = "Abbey Road";
        vm.SaveCommand.Execute(null);

        Assert.Equal("Abbey Road", chart.AlbumName);
        Assert.Equal("Beatles, The", chart.ArtistSort);
    }

    /// <summary>
    ///     The one case that still tracks: a sort name that was only ever a MIRROR
    ///     of the artist follows a rename, or a chart whose two fields agreed would
    ///     be left with a stale sort key the moment the artist is corrected.
    /// </summary>
    [Fact]
    public void Save_AfterRenamingTheArtist_FollowsAMirroredSortName()
    {
        var (vm, chart, _) = Rig("The Beetles", "The Beetles");

        vm.Artist = "The Beatles";
        vm.SaveCommand.Execute(null);

        Assert.Equal("The Beatles", chart.Artist);
        Assert.Equal("The Beatles", chart.ArtistSort);
    }

    /// <summary>An EMPTY sort name stays empty — that is the documented psarc
    ///     import state, and both writers already fall back to the artist for it.</summary>
    [Fact]
    public void Save_LeavesAnEmptySortNameEmpty()
    {
        var (vm, chart, _) = Rig("The Beatles", "");

        vm.SongName = "Something";
        vm.SaveCommand.Execute(null);

        Assert.Equal("Something", chart.SongName);
        Assert.Equal("", chart.ArtistSort);
    }

    /// <summary>The control: undo still restores every field the dialog owns,
    ///     including the one it now leaves alone.</summary>
    [Fact]
    public void Undo_RestoresTheSortNameWithTheRest()
    {
        var (vm, chart, history) = Rig("The Beetles", "The Beetles");

        vm.Artist = "The Beatles";
        vm.SaveCommand.Execute(null);
        history.Undo();

        Assert.Equal("The Beetles", chart.Artist);
        Assert.Equal("The Beetles", chart.ArtistSort);
    }
}
