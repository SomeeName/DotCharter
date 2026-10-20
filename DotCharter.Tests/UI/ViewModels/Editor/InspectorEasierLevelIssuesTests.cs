using DotCharter.UI.ViewModels.Editor.Inspector;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     #397: the Inspector's Issues list leaves out the issues on easier difficulty levels unless
///     asked. The hardest level and the level on screen always list theirs, and an issue with no
///     element (a whole-arrangement one) is never left out.
/// </summary>
public sealed class InspectorEasierLevelIssuesTests
{
    [Fact]
    public void Issues_on_easier_levels_other_than_the_one_on_screen_are_set_aside()
    {
        var arrangement = new Arrangement { Name = "Lead" };
        var notes = new GuitarNote[3];
        for (var d = 0; d < 3; d++)
        {
            var level = new ChartLevel { DifficultyIndex = d };
            notes[d] = new GuitarNote { Position = new FractionalPosition(d), Fret = 3 };
            level.Notes.Add(notes[d]);
            arrangement.Levels.Add(level);
        }

        var onEasy = new ArrangementIssue(ArrangementIssueType.OpenNoteWithBend, "easy", 0, Note: notes[0]);
        var onMid = new ArrangementIssue(ArrangementIssueType.OpenNoteWithBend, "mid", 0, Note: notes[1]);
        var onHard = new ArrangementIssue(ArrangementIssueType.OpenNoteWithBend, "hard", 0, Note: notes[2]);
        var whole = new ArrangementIssue(ArrangementIssueType.OpenNoteWithBend, "whole arrangement");

        var easier = InspectorIssuesViewModel.OnEasierLevels(arrangement, [onEasy, onMid, onHard, whole],
            shown: arrangement.Levels[1]);

        Assert.Equal([onEasy], easier);
        Assert.Equal(2, InspectorIssuesViewModel.OnEasierLevels(arrangement, [onEasy, onMid, onHard, whole],
            shown: arrangement.Levels[2]).Count);
    }

    [Fact]
    public void A_one_level_arrangement_sets_nothing_aside()
    {
        var arrangement = new Arrangement { Name = "Bass" };
        var note = new GuitarNote { Fret = 3 };
        arrangement.Levels.Add(new ChartLevel { Notes = { note } });

        Assert.Empty(InspectorIssuesViewModel.OnEasierLevels(arrangement,
            [new ArrangementIssue(ArrangementIssueType.OpenNoteWithBend, "x", 0, Note: note)], arrangement.Levels[0]));
    }
}
