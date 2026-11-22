using DotCharter.Tests.Services.Editing.Maintenance;
using DotCharter.Tests.Services.State.Linking;

namespace DotCharter.Tests.Services.State;

public sealed class SnapshotMaterializationIdentityTests
{
    [Fact]
    public void GeneratedBends_StayLiveAfterCopyAndScaleReplay()
    {
        var h = new ChartPowerToolsTests.Harness();
        var project = h.State.ActiveProject!;
        var arrangement = h.State.ActiveArrangement!;
        var original = h.State.ActiveLevel!;
        original.Notes.Clear();
        var source = new GuitarNote { Position = new FractionalPosition(1), Sustain = new FractionalPosition(4), Fret = 5 };
        source.BendValues.Add(new BendValue(new FractionalPosition(3), 1));
        original.Notes.Add(source);
        Assert.True(h.Maintenance.GenerateLevels(4));
        var generated = arrangement.Levels[3];
        Assert.Same(generated, h.State.ActiveLevel);
        var note = Assert.Single(generated.Notes);
        var bend = Assert.Single(note.BendValues);
        note.CopyFrom(note.Clone());
        Assert.Same(bend, Assert.Single(note.BendValues));
        Assert.True(h.History.Do(Assert.IsType<BeatScaleAction>(new BeatScaleService(new FakeAppLogger())
            .BuildScaleAction(project, project.Beats[0].Time, project.Beats[^1].Time, 2))));
        var scaled = ChartProjectCloner.Clone(project);

        for (var cycle = 0; cycle < 3; cycle++)
        {
            h.History.Undo();
            Assert.Equal(new FractionalPosition(3), bend.Position);
            h.History.Undo();
            Assert.Same(original, Assert.Single(arrangement.Levels));
            Assert.Same(original, h.State.ActiveLevel);
            h.History.Redo();
            Assert.Same(generated, arrangement.Levels[3]);
            Assert.Same(note, Assert.Single(arrangement.Levels[3].Notes));
            Assert.Same(bend, Assert.Single(arrangement.Levels[3].Notes[0].BendValues));
            Assert.Contains(h.State.ActiveLevel!, arrangement.Levels);
            h.History.Redo();
            Assert.Equal(new FractionalPosition(6), bend.Position);
            AssertChart(scaled, project);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NewRoots_AdoptDescendantIdentitiesDuringFirstRestore(bool newArrangement)
    {
        var project = new ChartProject();
        var arrangement = new Arrangement();
        project.Arrangements.Add(arrangement);
        var before = ChartProjectCloner.Clone(project, retainIdentities: true);
        var after = ChartProjectCloner.Clone(project, retainIdentities: true);
        var target = newArrangement ? new Arrangement() : after.Arrangements[0];
        if (newArrangement) after.Arrangements.Add(target);
        var level = new ChartLevel();
        target.Levels.Add(level);
        var note = new GuitarNote();
        note.BendValues.Add(new BendValue(new FractionalPosition(3), 1));
        level.Notes.Add(note);
        var chord = new Chord();
        var child = new ChordNote { StringIndex = 1, Fret = 5 };
        child.BendValues.Add(new BendValue(new FractionalPosition(4), 1));
        chord.ChordNotes.Add(child);
        level.Chords.Add(chord);
        _ = ChartProjectFootprint.EstimateHistoryBytes(after);
        ChartProjectRestorer.Restore(project, after);
        var live = project.Arrangements[^1].Levels[0];
        var liveNote = Assert.Single(live.Notes);
        var liveBend = Assert.Single(liveNote.BendValues);
        var liveChord = Assert.Single(live.Chords);
        var liveChild = Assert.Single(liveChord.ChordNotes);
        var liveChordBend = Assert.Single(liveChild.BendValues);
        liveNote.CopyFrom(liveNote.Clone());
        liveChord.CopyFrom(liveChord.Clone());

        for (var cycle = 0; cycle < 3; cycle++)
        {
            ChartProjectRestorer.Restore(project, before);
            ChartProjectRestorer.Restore(project, after);
            Assert.Same(live, project.Arrangements[^1].Levels[0]);
            Assert.Same(liveNote, Assert.Single(live.Notes));
            Assert.Same(liveBend, Assert.Single(live.Notes[0].BendValues));
            Assert.Same(liveChord, Assert.Single(live.Chords));
            Assert.Same(liveChild, Assert.Single(live.Chords[0].ChordNotes));
            Assert.Same(liveChordBend, Assert.Single(live.Chords[0].ChordNotes[0].BendValues));
        }
    }

    [Fact]
    public void LinkFollowUp_UndoesScaleOnTheOriginalLivePlacements()
    {
        var h = LinkHarness.WithRhythm();
        var group = h.Link((h.Lead, 1), (h.Lead, 4), (h.Lead, 7));
        var placements = group.Placements.ToArray();
        var positions = placements.Select(p => (p.Position, p.EndPosition)).ToArray();
        var initial = ChartProjectCloner.Clone(h.Project);
        var scale = Assert.IsType<BeatScaleAction>(new BeatScaleService(new FakeAppLogger())
            .BuildScaleAction(h.Project, h.Project.Beats[0].Time, h.Project.Beats[^1].Time, 2));
        Assert.True(h.History.Do(scale));
        var scaled = ChartProjectCloner.Clone(h.Project);

        for (var cycle = 0; cycle < 3; cycle++)
        {
            h.History.Undo();
            var live = Assert.Single(h.Project.LinkGroups);
            Assert.Same(group, live);
            Assert.Equal(placements.Length, live.Placements.Count);
            for (var i = 0; i < placements.Length; i++)
            {
                Assert.Same(placements[i], live.Placements[i]);
                Assert.Equal(positions[i], (live.Placements[i].Position, live.Placements[i].EndPosition));
            }

            AssertChart(initial, h.Project);
            h.History.Redo();
            AssertChart(scaled, h.Project);
        }
    }

    [Fact]
    public void LinkFollowUpSnapshots_AreIncludedInHistoryBudget()
    {
        var h = LinkHarness.WithRhythm();
        h.Link((h.Lead, 1), (h.Lead, 4), (h.Lead, 7));
        var before = h.History.RetainedBytes;
        var scale = Assert.IsType<BeatScaleAction>(new BeatScaleService(new FakeAppLogger())
            .BuildScaleAction(h.Project, h.Project.Beats[0].Time, h.Project.Beats[^1].Time, 2));
        Assert.True(h.History.Do(scale));
        var retained = h.History.RetainedBytes;
        Assert.True(retained > before + ((IChartAction)scale).EstimatedBytes, "The node must charge its link follow-up snapshots too.");
        h.History.Undo();
        h.History.Redo();
        Assert.Equal(retained, h.History.RetainedBytes);
        h.History.Clear();
        Assert.Equal(0, h.History.RetainedBytes);
    }

    [Fact]
    public void LinkGroupEditSnapshots_AreIncludedInHistoryBudget()
    {
        var h = LinkHarness.WithRhythm();
        var group = h.Link((h.Lead, 1), (h.Lead, 4), (h.Lead, 7));
        var rename = DotCharter.Services.Editing.Linking.LinkGroupEdits.Rename(h.Project, group.Id, "New name")!;
        Assert.True(rename.EstimatedBytes > 0);
        var before = h.History.RetainedBytes;
        Assert.True(h.History.Do(rename));
        Assert.Equal(before + rename.EstimatedBytes, h.History.RetainedBytes);
    }

    private static void AssertChart(ChartProject expected, ChartProject actual)
    {
        var differences = DeepCompare.Diff("project", expected, actual, new HashSet<string>());
        Assert.True(differences.Count == 0, string.Join("\n", differences));
    }
}
