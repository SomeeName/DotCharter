using DotCharter.Tests.Services.State;
using DotCharter.Tests.Services.Tempo;

namespace DotCharter.Tests.Services.Editing.Maintenance;

/// <summary>
///     The shipped verb behind Tempo map ▸ Align beat grid to notes (#403): ONE undo
///     step that puts the grid AND every position back, and a locked beat map left
///     alone — the lock is a promise (editing-invariants, "Lock beat map").
/// </summary>
public sealed class AlignBeatGridTests
{
    [Fact]
    public void OneUndo_RestoresTheGridAndEveryPositionExactly()
    {
        var h = Build();
        var project = h.State.ActiveProject!;
        var before = ChartProjectCloner.Clone(project);
        var times = Notes(h).Select(n => n.Position.GetPositionMs(project.Beats)).ToList();

        Assert.True(h.Maintenance.AlignBeatGridToNotes(121));

        Assert.Equal(before.Beats[0].Time + 121, project.Beats[0].Time, 6);
        Assert.Equal(times, Notes(h).Select(n => n.Position.GetPositionMs(project.Beats)), new WithinOneMs());
        Assert.Contains("121", h.State.MainDisplayMessage, StringComparison.Ordinal);

        h.History.Undo();

        Assert.False(h.History.CanUndo);
        var diffs = ModelGraphWalker.Compare(before, project, "project");
        Assert.True(diffs.Count == 0, "undo left these behind:\n" + string.Join("\n", diffs));
    }

    [Fact]
    public void ALockedBeatMap_IsRefused_AndNothingMoves()
    {
        var h = Build();
        var project = h.State.ActiveProject!;
        project.IsBeatMapLocked = true;
        var before = ChartProjectCloner.Clone(project);

        Assert.False(h.Maintenance.AlignBeatGridToNotes(121));

        Assert.False(h.History.CanUndo);
        Assert.Empty(ModelGraphWalker.Compare(before, project, "project"));
        Assert.Equal(FakeLocalization.English()["Status_BeatMapLocked"], h.State.MainDisplayMessage);
    }

    [Fact]
    public void NoChart_IsRefused()
    {
        var h = new ChartPowerToolsTests.Harness();
        h.State.ActiveProject = null;

        Assert.False(h.Maintenance.AlignBeatGridToNotes(121));
        Assert.False(h.History.CanUndo);
    }

    /// <summary>The power-tools harness with the #403 chart in it: a grid shipped 121 ms early.</summary>
    private static ChartPowerToolsTests.Harness Build()
    {
        var h = new ChartPowerToolsTests.Harness();
        var chart = BeatGridFixtures.Chart(gridErrorMs: -121);
        var project = h.State.ActiveProject!;
        project.Beats.Clear();
        foreach (var beat in chart.Beats) project.Beats.Add(beat);
        var level = h.State.ActiveLevel!;
        level.Notes.Clear();
        foreach (var note in chart.Arrangements[0].Levels[0].Notes) level.Notes.Add(note);
        return h;
    }

    private static IList<GuitarNote> Notes(ChartPowerToolsTests.Harness h) => h.State.ActiveProject!.Arrangements[0].Levels[0].Notes;

    private sealed class WithinOneMs : IEqualityComparer<double>
    {
        public bool Equals(double x, double y) => Math.Abs(x - y) <= 1.0;

        public int GetHashCode(double obj) => 0;
    }
}
