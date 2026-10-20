using DotCharter.Localization;
using DotCharter.UI.ViewModels.Editor.Inspector;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     #117: the Sound tab lists a chord's findings once per PROBLEM. The lint
///     emits one finding per chord note, and the message omits the string, so a
///     three-note chord outside its anchor printed the same sentence three times
///     and its badge said 3.
/// </summary>
public sealed class InspectorIssueFoldTests
{
    private const string OutsideAnchor = "Fret 10 lies outside the active anchor's range (frets 2–5).";
    private static readonly string[] Standard = ["E", "A", "D", "G", "B", "e"];

    private static IReadOnlyList<InspectorFieldIssue> Fold(IEnumerable<ArrangementIssue> issues, ILocalizationService? loc = null) =>
        InspectorFieldIssue.Fold(issues,
            strings => GuitarStringLabel.List(strings, 6, Standard, "{0} ({1})"),
            loc ?? FakeLocalization.English());

    private static ArrangementIssue Issue(ArrangementIssueType type, string message, Chord chord, int stringIndex) =>
        new(type, message, Chord: chord, StringIndex: stringIndex);

    [Fact]
    public void AChordWhoseThreeNotesBreakTheSameRule_IsOneRow_NamingItsStrings()
    {
        var chord = new Chord();
        var rows = Fold([
            Issue(ArrangementIssueType.NoteOutsideAnchorRange, OutsideAnchor, chord, 2),
            Issue(ArrangementIssueType.NoteOutsideAnchorRange, OutsideAnchor, chord, 0),
            Issue(ArrangementIssueType.NoteOutsideAnchorRange, OutsideAnchor, chord, 1)
        ]);

        var row = Assert.Single(rows);
        Assert.Equal(3, row.Count);
        Assert.Equal("L05", row.Code);
        Assert.Equal(OutsideAnchor, row.Message);
        Assert.Null(row.StringIndex);
        Assert.Equal("— strings E (6), A (5), D (4)", row.Detail);
    }

    [Fact]
    public void TheFoldDetail_ComesFromTheResx()
    {
        var chord = new Chord();
        var row = Assert.Single(Fold([
            Issue(ArrangementIssueType.NoteOutsideAnchorRange, OutsideAnchor, chord, 0),
            Issue(ArrangementIssueType.NoteOutsideAnchorRange, OutsideAnchor, chord, 1)
        ], FakeLocalization.KeysOnly()));
        Assert.Equal("Inspector_Issue_FoldedStringsFmt(E (6), A (5))", row.Detail);
    }

    [Fact]
    public void TwoDifferentRules_StayTwoRows_InTheLintsOrder()
    {
        var chord = new Chord();
        var rows = Fold([
            Issue(ArrangementIssueType.NoteFretBelowCapo, "Fret 1 is behind the capo.", chord, 0),
            Issue(ArrangementIssueType.NoteOutsideAnchorRange, OutsideAnchor, chord, 0),
            Issue(ArrangementIssueType.NoteOutsideAnchorRange, OutsideAnchor, chord, 1)
        ]);

        Assert.Equal(2, rows.Count);
        Assert.Equal(ArrangementIssueType.NoteFretBelowCapo.GetCode(), rows[0].Code);
        Assert.Equal(1, rows[0].Count);
        Assert.Equal(0, rows[0].StringIndex);
        Assert.Equal(string.Empty, rows[0].Detail);
        Assert.Equal(2, rows[1].Count);
    }

    /// <summary>The same rule with a DIFFERENT sentence (another fret, another
    /// anchor) is a different problem and keeps its own row.</summary>
    [Fact]
    public void TheSameRuleWithADifferentSentence_IsADifferentRow()
    {
        var chord = new Chord();
        var rows = Fold([
            Issue(ArrangementIssueType.NoteOutsideAnchorRange, OutsideAnchor, chord, 0),
            Issue(ArrangementIssueType.NoteOutsideAnchorRange, "Fret 12 lies outside the active anchor's range (frets 2–5).", chord, 1)
        ]);
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void TheExportConsequence_SurvivesTheFold()
    {
        var chord = new Chord();
        var row = Assert.Single(Fold([
            Issue(ArrangementIssueType.NoteFretBelowCapo, "behind the capo", chord, 0),
            Issue(ArrangementIssueType.NoteFretBelowCapo, "behind the capo", chord, 3)
        ]));
        Assert.True(row.BlocksExport);
        Assert.Equal(InspectorIssueFieldMap.Map(ArrangementIssueType.NoteFretBelowCapo), row.Field);
    }

    /// <summary>The header badge counts what the tab prints: rows, not lint
    /// findings. A source lint, because the inspector view model cannot be
    /// built outside a headless session (see InspectorSelectionIssueMemoTests).</summary>
    [Fact]
    public void TheSelectionBadge_CountsFoldedRows()
    {
        var source = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "ViewModels", "Editor",
            "InspectorViewModel.SelectionIssues.cs"));
        Assert.Contains("public int SelectionIssueCount => SelectionFieldIssues.Count;", source, StringComparison.Ordinal);
        Assert.Contains("InspectorFieldIssue.Fold(", source, StringComparison.Ordinal);
    }
}
