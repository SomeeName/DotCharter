using System.Text.RegularExpressions;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Editing.Fixing;
using DotCharter.Services.Editing.Maintenance;
using DotCharter.Tests.Services.Editing.HandShapes;

namespace DotCharter.Tests.Services.Editing.Maintenance;

/// <summary>
///     #497: "Fix chart" is the two existing operations (Auto-fix arrangements = Errors, Improve chart = Polish)
///     behind one door. The preview runs them on a copy and changes nothing; Apply runs the ticked groups as ONE
///     undo step; every label either operation can report has its words in all three languages.
/// </summary>
public sealed class FixChartTests
{
    [Fact]
    public async Task SpacingOnlyPolishIsPreviewedCommittedAndUndoable()
    {
        var h = new ChartPowerToolsTests.Harness(improvement: new ChartImprovementService());
        var arrangement = h.State.ActiveArrangement!;
        var note = arrangement.Levels[0].Notes[0];
        note.Fret = 24;
        arrangement.EventPoints.Add(new EventPoint { Position = new FractionalPosition(31), Phrase = "END" });

        var before = ChartProjectCloner.Clone(h.State.ActiveProject!, retainIdentities: true);
        var preview = await h.Maintenance.PreviewFixChartAsync(FixChartGroups.Polish);

        Assert.True(preview.PolishCount > 0);
        Assert.False(note.Ignore);
        Assert.False(h.History.CanUndo);
        h.Maintenance.FixChart(FixChartGroups.Polish);
        var after = ChartProjectCloner.Clone(h.State.ActiveProject!, retainIdentities: true);
        Assert.True(note.Ignore);
        Assert.True(note.IgnoreDerived);
        Assert.Single(h.History.GetUndoSteps());
        h.History.Undo();
        Assert.False(note.Ignore);
        Assert.False(note.IgnoreDerived);
        Assert.Empty(DeepCompare.Diff("undo", before, h.State.ActiveProject!, new HashSet<string>()));
        h.History.Redo();
        Assert.True(note.Ignore);
        Assert.True(note.IgnoreDerived);
        Assert.Empty(DeepCompare.Diff("redo", after, h.State.ActiveProject!, new HashSet<string>()));
    }

    private static readonly (int, int)[] G5 = [(0, 3), (1, 5), (2, 5)];

    private static ChartPowerToolsTests.Harness Seeded()
    {
        var h = new ChartPowerToolsTests.Harness(autoFix: new ArrangementAutoFixService());
        var arrangement = h.State.ActiveArrangement!;
        arrangement.StringCount = 6;
        var level = arrangement.Levels[0];
        level.Notes.Clear();
        level.Chords.Add(ChordHandShapePlannerTests.Chord(0, 1, G5));
        level.Chords.Add(ChordHandShapePlannerTests.Chord(4, 1, G5));
        return h;
    }

    [Fact]
    public async Task ThePreview_CountsWhatWouldChange_AndChangesNothing()
    {
        var h = Seeded();
        var arrangement = h.State.ActiveArrangement!;
        var shapesBefore = arrangement.Levels[0].HandShapes.Count;

        var preview = await h.Maintenance.PreviewFixChartAsync(FixChartGroups.Both);

        Assert.True(preview.ErrorCount > 0, "the chart has chords outside any hand shape: Errors must count them");
        Assert.Equal(shapesBefore, arrangement.Levels[0].HandShapes.Count);
        Assert.False(h.History.CanUndo);
        Assert.DoesNotContain(preview.Errors, c => c.Count <= 0);
    }

    [Fact]
    public async Task APreviewOfOneGroup_LeavesTheOtherEmpty()
    {
        var h = Seeded();

        var errors = await h.Maintenance.PreviewFixChartAsync(FixChartGroups.Errors);
        var polish = await h.Maintenance.PreviewFixChartAsync(FixChartGroups.Polish);

        Assert.Empty(errors.Polish);
        Assert.Empty(polish.Errors);
        Assert.True(errors.ErrorCount > 0);
    }

    [Fact]
    public async Task Apply_RunsBothGroupsAsOneUndoStep_AndWhatThePreviewCountedIsWhatChanged()
    {
        var h = Seeded();
        var preview = await h.Maintenance.PreviewFixChartAsync(FixChartGroups.Both);
        var arrangement = h.State.ActiveArrangement!;
        Assert.Empty(arrangement.Levels[0].HandShapes);

        h.Maintenance.FixChart(FixChartGroups.Both);

        Assert.NotEmpty(h.State.ActiveProject!.Arrangements[0].Levels[0].HandShapes);
        Assert.Contains(preview.Total.ToString(), h.State.MainDisplayMessage);
        h.History.Undo();
        Assert.False(h.History.CanUndo); // ONE node for both groups
        Assert.Empty(h.State.ActiveProject!.Arrangements[0].Levels[0].HandShapes);
    }

    [Fact]
    public void ApplyingNothing_PushesNoUndoNode()
    {
        var h = Seeded();

        h.Maintenance.FixChart(FixChartGroups.None);

        Assert.False(h.History.CanUndo);
    }

    /// <summary>Every label an operation reports is a resource key in all three files, or the dialog shows a key.</summary>
    [Fact]
    public void EveryLabelEitherOperationReports_HasItsWordsInAllThreeLanguages()
    {
        var root = TestPaths.RepoRoot();
        var labels = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in new[]
                 {
                     Path.Combine("Fixing", "ArrangementAutoFixService.cs"), Path.Combine("Improvement", "ChartImprovementService.cs")
                 })
        {
            var text = File.ReadAllText(Path.Combine(root, "DotCharter.Services", "Editing", file));
            foreach (Match m in Regex.Matches(text, @"result\.Add\(""(?<label>[^""]+)"""))
                labels.Add(m.Groups["label"].Value);
        }

        Assert.True(labels.Count >= 20, $"only {labels.Count} labels found: the pattern has gone blind");
        foreach (var resx in new[] { "Resources.resx", "Resources.de-DE.resx", "Resources.ja-JP.resx" })
        {
            var text = File.ReadAllText(Path.Combine(root, "DotCharter.Localization", resx));
            foreach (var label in labels)
            {
                var key = FixChartChangeKey.For(label);
                Assert.True(text.Contains($"name=\"{key}\"", StringComparison.Ordinal), $"{resx} has no '{key}' for the label '{label}'");
            }
        }
    }

    [Theory]
    [InlineData("sortedCollections", "FixChart_Change_SortedCollections")]
    [InlineData("song-end cleanup", "FixChart_Change_SongEndCleanup")]
    [InlineData("dangling link-next targets", "FixChart_Change_DanglingLinkNextTargets")]
    public void ALabelBecomesItsResourceKey(string label, string key) => Assert.Equal(key, FixChartChangeKey.For(label));
}
