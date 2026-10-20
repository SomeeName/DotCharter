namespace DotCharter.Tests.Services.Editing.Maintenance;

/// <summary>
///     #370: a power tool that changes ONE arrangement records that arrangement twice instead of
///     the whole project twice. The risk of the smaller snapshot is a tool that ALSO changes
///     something outside the arrangement: undo would then leave that change behind. So every tool
///     that records an arrangement snapshot is held here to a whole-project deep compare across
///     do, undo and redo, on a project with a second arrangement it must not touch.
/// </summary>
public sealed class ArrangementSnapshotUndoTests
{
    public static TheoryData<string> Tools => ["Refret", "GenerateLevels", "SquashLevels"];

    private static ChartPowerToolsTests.Harness Prepared(string tool)
    {
        var h = new ChartPowerToolsTests.Harness(new LevelSquashService());
        var project = h.State.ActiveProject!;
        var level = h.State.ActiveArrangement!.Levels[0];
        level.Notes.Clear();
        int[] frets = [3, 5, 7, 8, 10, 12, 14, 15];
        for (var i = 0; i < frets.Length; i++)
            level.Notes.Add(new GuitarNote { Position = new FractionalPosition(i), StringIndex = 1, Fret = frets[i] });

        var rhythm = new Arrangement { Name = "Rhythm", ArrangementType = ArrangementType.Rhythm };
        var rhythmLevel = new ChartLevel();
        for (var i = 0; i < 4; i++)
            rhythmLevel.Notes.Add(new GuitarNote { Position = new FractionalPosition(i), StringIndex = 0, Fret = 5 });
        rhythm.Levels.Add(rhythmLevel);
        project.Arrangements.Add(rhythm);

        if (tool == "SquashLevels") Assert.True(h.Maintenance.GenerateLevels(3));
        h.History.Clear();
        return h;
    }

    private static bool Run(ChartPowerToolsTests.Harness h, string tool)
    {
        switch (tool)
        {
            case "Refret":
                return h.Maintenance.Refret();
            case "GenerateLevels":
                return h.Maintenance.GenerateLevels(4);
            default:
                h.Maintenance.SquashLevels();
                return h.History.CanUndo;
        }
    }

    private static void AssertSame(ChartProject expected, ChartProject actual, string when)
    {
        var diffs = DeepCompare.Diff("project", expected, actual, new HashSet<string>());
        Assert.True(diffs.Count == 0, $"{when}: the project differs:\n  " + string.Join("\n  ", diffs.Take(10)));
    }

    [Theory]
    [MemberData(nameof(Tools))]
    public void AScopedTool_UndoesAndRedoesTheWholeProjectExactly(string tool)
    {
        var h = Prepared(tool);
        var project = h.State.ActiveProject!;
        var rhythm = project.Arrangements[1];
        var before = ChartProjectCloner.Clone(project);

        Assert.True(Run(h, tool), $"{tool} changed nothing; the fixture no longer exercises it");
        var after = ChartProjectCloner.Clone(project);
        Assert.True(DeepCompare.Diff("project", before, after, new HashSet<string>()).Count > 0,
            $"{tool} reported a change that never reached the live project");

        h.History.Undo();
        AssertSame(before, project, $"{tool} undone");
        h.History.Redo();
        AssertSame(after, project, $"{tool} redone");
        Assert.Same(rhythm, project.Arrangements[1]);
    }

    [Theory]
    [MemberData(nameof(Tools))]
    public void AScopedTool_RecordsOneArrangementTwice_NotTheProject(string tool)
    {
        var h = Prepared(tool);
        var project = h.State.ActiveProject!;
        var before = ChartProjectFootprint.EstimateBytes(project.Arrangements[0]);

        Assert.True(Run(h, tool));

        Assert.Equal(before + ChartProjectFootprint.EstimateBytes(project.Arrangements[0]), h.History.RetainedBytes);
    }
}
