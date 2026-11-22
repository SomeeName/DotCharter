using DotCharter.Tests.UI.ViewModels.Panels;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Editor.Inspector;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     #751 P5 review M3/M4: the Library page follows an arrangement switch (its list never shows the previous
///     arrangement's templates), "Next use" steps the playhead from use to use without selecting (a selection would leave
///     the page, which is offered only with nothing selected), and the use count names the set the verbs walk.
/// </summary>
public sealed class ChordLibraryUsesTests
{
    [Fact]
    public void AnArrangementSwitch_RebuildsTheList_FromTheNewArrangement()
    {
        var (f, library, sections, posted) = Build();
        sections.ChooseCommand.Execute(SoundSection.Library);
        library.Refresh();
        Assert.Equal(f.Arrangement.ChordTemplates.Count, library.Entries.Count);

        var other = new Arrangement { Name = "Rhythm", ArrangementType = ArrangementType.Rhythm };
        other.Levels.Add(new ChartLevel());
        var g = new ChordTemplate { Id = 0, Name = "G" };
        g.Frets[0] = 3;
        other.ChordTemplates.Add(g);
        f.Project.Arrangements.Add(other);
        f.Editor.ActiveArrangement = other;
        foreach (var pass in posted.ToArray()) pass();

        Assert.Equal("G", Assert.Single(library.Entries).Template.Name);
    }

    [Fact]
    public void NextUse_StepsThePlayheadFromUseToUse_AndLeavesTheSelectionAlone()
    {
        var (f, library, _, _) = Build();
        f.Selection.ClearSelections();
        library.Refresh();
        library.Selected = library.Entries.First(e => e.Template.Name == "A5");

        var seeks = new List<double>();
        for (var i = 0; i < 4; i++)
        {
            library.NextUseCommand.Execute(null);
            seeks.Add(f.State.CurrentTimeMs);
        }

        // A5 is used on beats 0, 1 and 2 of the active level (500 ms a beat); the playhead starts at 0 and wraps.
        Assert.Equal([500.0, 1000.0, 0.0, 500.0], seeks);
        Assert.Empty(f.Selection.SelectedChords);
    }

    [Fact]
    public void TheUseCount_IsTheActiveLevelsSet_AndNamesTheTotalWhenItDiffers()
    {
        var (_, library, _, _) = Build();
        library.Refresh();
        library.Selected = library.Entries.First(e => e.Template.Name == "A5");

        Assert.Equal(3, library.UseCount);
        Assert.Equal(4, library.TotalUseCount);
        Assert.Equal("3 uses · 4 in all", library.UsesText);

        library.Selected = library.Entries.First(e => e.Template.Name == "B5");
        Assert.Equal("1 use", library.UsesText);
    }

    private static (ChordLadderFixture, ChordLibraryViewModel, SoundSectionsViewModel, List<Action>) Build()
    {
        var f = new ChordLadderFixture();
        var sections = new SoundSectionsViewModel();
        var queue = new SoundQueueViewModel(() => f.Arrangement, () => f.Arrangement.Levels[0], () => [], () => [],
            f.Selection, _ => { }, FakeLocalization.English(), _ => (ChordNamingOptions.Default, DoubleStopNameOptions.Default));
        var posted = new List<Action>();
        var library = new ChordLibraryViewModel(f.Editor, f.Panel, sections, queue, f.History, FakeLocalization.English(),
            () => f.State.CurrentTimeMs)
        {
            Post = posted.Add
        };
        return (f, library, sections, posted);
    }
}
