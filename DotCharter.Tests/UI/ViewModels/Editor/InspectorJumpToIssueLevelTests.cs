using System.Text.RegularExpressions;
using DotCharter.UI.ViewModels.Editor.Inspector;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     "Jump to issue" landed on the right TIME and the wrong LEVEL.
///     <para>
///         Every validation rule sweeps every level of the arrangement — all
///         sixteen open <c>foreach (var level in arrangement.Levels)</c> — so on
///         a chart with dynamic difficulty most findings belong to a level that
///         is not the one on screen. The command seeked, then selected the
///         issue's element regardless: the highway drew nothing (it renders the
///         ACTIVE level), the Sound card described a sound the charter could not
///         see, and an edit made from that card landed on another level's
///         element. <c>ArrangementIssue</c> carries no level, so the owning one
///         is resolved from the back-reference.
///     </para>
/// </summary>
public sealed class InspectorJumpToIssueLevelTests
{
    private static (Arrangement Arrangement, GuitarNote OnLevel1, Chord ChordOnLevel1) TwoLevels()
    {
        var arrangement = new Arrangement { Name = "Lead" };
        var level0 = new ChartLevel { DifficultyIndex = 0 };
        level0.Notes.Add(new GuitarNote { Position = new FractionalPosition(0), StringIndex = 0, Fret = 3 });
        var level1 = new ChartLevel { DifficultyIndex = 1 };
        var note = new GuitarNote { Position = new FractionalPosition(0), StringIndex = 0, Fret = 3 };
        level1.Notes.Add(note);
        var chord = new Chord { Position = new FractionalPosition(2) };
        level1.Chords.Add(chord);
        arrangement.Levels.Add(level0);
        arrangement.Levels.Add(level1);
        return (arrangement, note, chord);
    }

    [Fact]
    public void LevelOwning_FindsTheLevelHoldingTheIssuesNote()
    {
        var (arrangement, note, _) = TwoLevels();

        var owner = InspectorIssuesViewModel.LevelOwning(arrangement,
            new ArrangementIssue(ArrangementIssueType.NoteWithoutAnchor, "", 0, Note: note));

        Assert.Same(arrangement.Levels[1], owner);
    }

    [Fact]
    public void LevelOwning_FindsTheLevelHoldingAChordHandShapeOrAnchor()
    {
        var (arrangement, _, chord) = TwoLevels();
        var shape = new HandShape { Position = new FractionalPosition(4) };
        var anchor = new FretAnchor(new FractionalPosition(6), 5);
        arrangement.Levels[1].HandShapes.Add(shape);
        arrangement.Levels[1].Anchors.Add(anchor);

        Assert.Same(arrangement.Levels[1], InspectorIssuesViewModel.LevelOwning(arrangement,
            new ArrangementIssue(ArrangementIssueType.MutedStringInNonMutedChord, "", 0, Chord: chord)));
        Assert.Same(arrangement.Levels[1], InspectorIssuesViewModel.LevelOwning(arrangement,
            new ArrangementIssue(ArrangementIssueType.OverlappingHandShapes, "", 0, HandShape: shape)));
        Assert.Same(arrangement.Levels[1], InspectorIssuesViewModel.LevelOwning(arrangement,
            new ArrangementIssue(ArrangementIssueType.AnchorOnOrBelowCapo, "", 0, Anchor: anchor)));
    }

    [Fact]
    public void LevelOwning_IsNull_ForAWholeArrangementFindingOrADeletedElement()
    {
        var (arrangement, _, _) = TwoLevels();

        Assert.Null(InspectorIssuesViewModel.LevelOwning(arrangement,
            new ArrangementIssue(ArrangementIssueType.NoEndPhrase, "")));
        Assert.Null(InspectorIssuesViewModel.LevelOwning(arrangement,
            new ArrangementIssue(ArrangementIssueType.NoteWithoutAnchor, "", 0,
                Note: new GuitarNote { Position = new FractionalPosition(0) })));
        Assert.Null(InspectorIssuesViewModel.LevelOwning(null,
            new ArrangementIssue(ArrangementIssueType.NoEndPhrase, "")));
    }

    /// <summary>The command itself takes eleven services, so — like the rest of
    /// this VM's coverage — the wiring is a source lint over the one call site.</summary>
    [Fact]
    public void JumpToIssue_SwitchesTheActiveLevel_BeforeItSelects()
    {
        var src = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "ViewModels", "Editor",
            "Inspector", "InspectorIssuesViewModel.cs")));
        var body = src[src.IndexOf("public void JumpToIssue(", StringComparison.Ordinal)..];
        body = body[..body.IndexOf("internal void RecomputeIssues()", StringComparison.Ordinal)];

        var switchAt = body.IndexOf("_projectState.ActiveLevel = owner;", StringComparison.Ordinal);
        var selectAt = body.IndexOf("Selection.SelectedNotes.Add(issue.Note)", StringComparison.Ordinal);
        Assert.True(switchAt > 0, "JumpToIssue no longer switches to the level that owns the issue.");
        Assert.True(selectAt > switchAt, "The level switch must happen before the selection.");
        Assert.Contains("LevelOwning(_projectState.ActiveArrangement, issue)", body);
    }

    [Fact]
    public void EveryValidationRule_StillSweepsEveryLevel_WhichIsWhyTheSwitchIsNeeded()
    {
        // Vacuity keeper: if the rules ever became single-level, the switch
        // above would be dead code and this test would say so.
        var rules = UiSourceTree.GetFiles(
            Path.Combine(TestPaths.RepoRoot(), "DotCharter.Services", "Editing", "Validation", "Rules"), "*.cs");
        Assert.NotEmpty(rules);
        Assert.Contains(rules, f => Regex.IsMatch(UiSourceTree.ReadAllText(f), @"foreach \(var level in .*\.Levels\)"));
    }
}
