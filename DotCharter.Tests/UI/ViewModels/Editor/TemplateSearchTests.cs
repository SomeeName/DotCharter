using DotCharter.Tests.UI.ViewModels.Panels;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Editor.Inspector;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     #751.1 / P5: the Library page's template search finds a template by its NAME and by its FRETS ("x07"), and the
///     list it drives shows both on every entry. Typing goes through the page's coalesced filter pass: a burst of
///     keystrokes is one rebuild.
/// </summary>
public sealed class TemplateSearchTests
{
    [Theory]
    [InlineData("Am", "x02210", "x02", true)]
    [InlineData("Am", "x02210", "x07", false)]
    [InlineData("E", "x07 9 9 x", "x07", true)]
    [InlineData("A5", "x0 7 7 x x", "x077", true)]
    [InlineData("Am7", "x02010", "m7", true)]
    [InlineData("Dm", "xx0231", "dm", true)]
    [InlineData("Dm", "xx0231", "Em", false)]
    [InlineData("Dm", "xx0231", "", true)]
    [InlineData("Dm", "xx0231", "0 2", true)]
    public void AQuery_MatchesByNameOrByFrets(string name, string frets, string query, bool expected) =>
        Assert.Equal(expected, TemplateSearch.Matches(name, frets, query));

    [Fact]
    public void TheLibraryList_FiltersByFretsAndByName_InOnePassPerBurst()
    {
        var f = new ChordLadderFixture();
        var sections = new SoundSectionsViewModel();
        var queue = new SoundQueueViewModel(() => f.Arrangement, () => f.Arrangement.Levels[0], () => [], () => [],
            f.Selection, _ => { }, FakeLocalization.English(), _ => (ChordNamingOptions.Default, DoubleStopNameOptions.Default));
        var posted = new List<Action>();
        using var library = new ChordLibraryViewModel(f.Editor, f.Panel, sections, queue, f.History, FakeLocalization.English(), () => 0)
        {
            Post = posted.Add
        };
        library.Refresh();
        Assert.Equal(f.Arrangement.ChordTemplates.Count, library.Entries.Count);
        Assert.All(library.Entries, e => Assert.False(string.IsNullOrEmpty(e.Frets) || string.IsNullOrEmpty(e.Name)));

        // A5 and "A5 (alt)" hold 5-7-7 on the three lowest strings; B5 7-9-9; E5 0-2-2 (ChordLadderFixture).
        var passes = library.Passes;
        library.Query = "5";
        library.Query = "57";
        library.Query = "577";
        Assert.Single(posted);
        posted[0]();
        Assert.Equal(passes + 1, library.Passes);
        Assert.Equal(["A5", "A5 (alt)"], library.Entries.Select(e => e.Template.Name).Order());
        Assert.All(library.Entries, e => Assert.StartsWith("577", e.Frets, StringComparison.Ordinal));

        library.Query = "b5";
        posted[^1]();
        Assert.Equal("B5", Assert.Single(library.Entries).Template.Name);

        library.Query = "022";
        posted[^1]();
        Assert.Equal("E5", Assert.Single(library.Entries).Template.Name);

        library.Query = "x99";
        posted[^1]();
        Assert.True(library.NoEntries);
    }
}
