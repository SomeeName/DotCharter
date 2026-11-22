using DotCharter.Services.Editing.Collisions;
using DotCharter.Tests.Services.Editing.Maintenance;

namespace DotCharter.Tests.Services.State;

public sealed class NestedUndoIdentityTests
{
    [Fact]
    public void BeatRedo_ReattachesTheTemplateCapturedByTheFingeringAction()
    {
        var h = Prepared();
        var project = h.State.ActiveProject!;
        var arrangement = h.State.ActiveArrangement!;
        ChangeBpm(h);
        var reduced = arrangement.ChordTemplates.Single(t => t.Id == arrangement.Levels[0].Chords[0].TemplateId);
        Assert.NotEqual(0, reduced.Id);
        h.History.Do(new SetTemplateFingersAction(reduced,
            new Dictionary<int, int>(reduced.Fingers), new Dictionary<int, int> { [0] = 2, [1] = 4 }, h.Editor));
        var edited = ChartProjectCloner.Clone(project);

        h.History.Undo();
        h.History.Undo();
        Assert.DoesNotContain(arrangement.ChordTemplates, t => ReferenceEquals(t, reduced));
        h.History.Redo();
        h.History.Redo();

        Assert.Same(reduced, arrangement.ChordTemplates.Single(t => t.Id == reduced.Id));
        AssertChart(edited, project);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MixedActionCycle_RestoresTheWholeChartAtBothEnds(bool includeProjectSnapshots)
    {
        var h = Prepared();
        var project = h.State.ActiveProject!;
        var level = h.State.ActiveLevel!;
        var note = level.Notes[0];
        var chord = level.Chords[0];
        var child = chord.ChordNotes[0];
        var noteBend = note.BendValues[0];
        var chordBend = child.BendValues[0];
        var initial = ChartProjectCloner.Clone(project);
        if (includeProjectSnapshots) h.Maintenance.RunAsSingleUndoStep(() => project.Chart.Charter = "Edited");
        h.History.Do(Assert.IsType<BeatScaleAction>(new BeatScaleService(new FakeAppLogger())
            .BuildScaleAction(project, 0, 10000, 2)));
        var changedChord = chord.Clone();
        changedChord.ChordNotes[0].Fret++;
        h.History.Do(new CompositeAction([
            new MutateNotesAction(level.Notes, [note], n => n.Fret++),
            new MutateChordsAction(level.Chords, [chord], [chord.Clone()], [changedChord])
        ]));
        // Outer snapshots can repair values later; nested owners must stay live now too.
        Assert.Same(noteBend, note.BendValues[0]);
        Assert.Same(child, chord.ChordNotes[0]);
        Assert.Same(chordBend, child.BendValues[0]);
        ChangeBpm(h);
        var reduced = h.State.ActiveArrangement!.ChordTemplates.Single(t => t.Id == chord.TemplateId);
        h.History.Do(new SetTemplateFingersAction(reduced, new Dictionary<int, int>(reduced.Fingers),
            new Dictionary<int, int> { [0] = 2, [1] = 4 }, h.Editor));
        if (includeProjectSnapshots) h.Maintenance.RunAsSingleUndoStep(() => project.Chart.Genre = "Edited");
        Assert.Equal(new FractionalPosition(6), note.BendValues[0].Position);
        var edited = ChartProjectCloner.Clone(project);

        while (h.History.CanUndo) h.History.Undo();
        AssertChart(initial, project);
        Assert.Same(noteBend, note.BendValues[0]);
        Assert.Same(child, chord.ChordNotes[0]);
        Assert.Same(chordBend, child.BendValues[0]);
        while (h.History.CanRedo) h.History.Redo();
        AssertChart(edited, project);
        Assert.Same(reduced, h.State.ActiveArrangement!.ChordTemplates.Single(t => t.Id == reduced.Id));
        while (h.History.CanUndo) h.History.Undo();
        AssertChart(initial, project);
        Assert.Same(noteBend, note.BendValues[0]);
        Assert.Same(chordBend, child.BendValues[0]);
    }

    [Fact]
    public void CollateralRollback_ReturnsTheBendsCapturedByAnEarlierScale()
    {
        var h = Prepared();
        var project = h.State.ActiveProject!;
        var level = h.State.ActiveLevel!;
        var note = level.Notes[0];
        var bend = note.BendValues[0];
        var initial = ChartProjectCloner.Clone(project);
        h.History.Do(Assert.IsType<BeatScaleAction>(new BeatScaleService(new FakeAppLogger())
            .BuildScaleAction(project, 0, 10000, 2)));
        h.History.Do(new AddNotesAction(level.Notes,
            [new GuitarNote { Position = new FractionalPosition(4), StringIndex = 4, Fret = 7 }],
            () => ChartSpacingEnforcer.EnforceLevel(level, project.Beats, Config())));
        Assert.True(note.Sustain < new FractionalPosition(2));

        h.History.Undo();
        Assert.Same(bend, note.BendValues[0]);
        h.History.Undo();
        AssertChart(initial, project);
    }

    [Theory]
    [InlineData("Edit")]
    [InlineData("Switch")]
    [InlineData("Create")]
    public void TemplateActions_KeepNestedTargetsAcrossSnapshotAndReplay(string kind)
    {
        var h = Prepared();
        var project = h.State.ActiveProject!;
        var arrangement = h.State.ActiveArrangement!;
        var chord = h.State.ActiveLevel!.Chords[0];
        var child = chord.ChordNotes[0];
        var bend = child.BendValues[0];
        var before = ChartProjectCloner.Clone(project);
        var changed = chord.Clone();
        changed.ChordNotes = [.. changed.ChordNotes.Skip(1).Select(n => n.Clone())];
        var template = arrangement.ChordTemplates[0];
        IChartAction action = kind switch
        {
            "Edit" => new EditTemplateGlobalAction(template, template.Clone(), template.Clone(), [chord], [changed], h.Editor),
            "Switch" => new SwitchTemplateAction([chord], [changed], [], [], h.Editor),
            _ => new CreateAndSwitchTemplateAction(arrangement.ChordTemplates,
                new ChordTemplate { Id = 10 }, [chord], [changed], [], [], h.Editor)
        };
        h.History.Do(action);
        var added = new ChordNote { StringIndex = 4, Fret = 9, Sustain = new FractionalPosition(1) };
        var growing = chord.Clone();
        growing.ChordNotes.Add(added);
        h.History.Do(new MutateChordsAction(h.State.ActiveLevel!.Chords, [chord], [chord.Clone()], [growing]));
        var laterChild = chord.ChordNotes[^1];
        h.Maintenance.RunAsSingleUndoStep(() => arrangement.Name = "Edited");
        var edited = ChartProjectCloner.Clone(project);

        while (h.History.CanUndo) h.History.Undo();
        AssertChart(before, project);
        Assert.Same(child, chord.ChordNotes[0]);
        Assert.Same(bend, child.BendValues[0]);
        while (h.History.CanRedo) h.History.Redo();
        AssertChart(edited, project);
        Assert.Same(laterChild, chord.ChordNotes[^1]);
        while (h.History.CanUndo) h.History.Undo();
        AssertChart(before, project);
    }

    private static ChartPowerToolsTests.Harness Prepared()
    {
        var h = new ChartPowerToolsTests.Harness();
        var project = h.State.ActiveProject!;
        project.Beats.Clear();
        for (var i = 0; i < 24; i++) project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120 });
        var arrangement = h.State.ActiveArrangement!;
        var level = h.State.ActiveLevel!;
        level.Notes.Clear();
        var note = new GuitarNote { Position = new FractionalPosition(1), Sustain = new FractionalPosition(3), Fret = 5 };
        note.BendValues.Add(new BendValue(new FractionalPosition(3), 1));
        level.Notes.Add(note);
        var template = new ChordTemplate { Id = 0, Name = "Shape" };
        for (var s = 0; s < 3; s++) template.Frets[s] = s == 0 ? 3 : 5;
        arrangement.ChordTemplates.Add(template);
        var chord = new Chord { Position = new FractionalPosition(8), TemplateId = 0 };
        for (var s = 0; s < 3; s++)
        {
            var child = new ChordNote
            {
                StringIndex = s,
                Fret = template.Frets[s],
                Sustain = new FractionalPosition(2),
                Mute = s == 2 ? MuteType.Full : MuteType.None
            };
            child.BendValues.Add(new BendValue(new FractionalPosition(9), 1));
            chord.ChordNotes.Add(child);
        }
        level.Chords.Add(chord);
        return h;
    }

    private static AppConfig Config() => new()
    {
        MinNoteDistanceMode = MinNoteDistanceMode.BeatFraction, MinNoteDistanceDivisor = 64
    };

    private static void ChangeBpm(ChartPowerToolsTests.Harness h)
    {
        var project = h.State.ActiveProject!;
        var before = project.Beats.Select(b => b.Clone()).ToList();
        var after = project.Beats.Select(b => b.Clone()).ToList();
        after[0].Bpm = 150;
        h.History.Do(new MutateBeatsAction(project.Beats, before, after,
            () => ChartSpacingEnforcer.EnforceArrangement(project.Arrangements[0], project.Beats, Config())));
    }

    private static void AssertChart(ChartProject expected, ChartProject actual)
    {
        var differences = DeepCompare.Diff("project", expected, actual, new HashSet<string>());
        Assert.True(differences.Count == 0, string.Join("\n", differences));
    }
}
