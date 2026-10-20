using DotCharter.Core.Domain.Vocals;
using DotCharter.Tests.Services.Editing.Maintenance;

namespace DotCharter.Tests.Services.Editing;

/// <summary>
///     #328: every whole-project tool records two full clones of the project, and the history
///     capped steps by COUNT, so a session of power tools on a base-game song imported with all
///     its levels (20-67 MiB a step) could pin gigabytes. The history now also keeps a byte
///     budget, fed by each action's <see cref="IChartAction.EstimatedBytes" />.
/// </summary>
public sealed class HistoryByteBudgetTests
{
    private const long MiB = 1024 * 1024;

    private sealed class SizedAction(string name, long bytes, List<string> log) : IChartAction
    {
        public long EstimatedBytes => bytes;
        public void Execute() => log.Add("do " + name);
        public void Undo() => log.Add("undo " + name);
        public override string ToString() => name;
    }

    private static HistoryService History(long maxBytes) => new(new FakeAppLogger(), maxBytes: maxBytes);

    private static int UndoAll(HistoryService history)
    {
        var steps = 0;
        while (history.CanUndo)
        {
            history.Undo();
            steps++;
        }

        return steps;
    }

    [Fact]
    public void OverTheBudget_TheOldestHeavyStepsCollapse_AndTheNewestStayUndoable()
    {
        var log = new List<string>();
        var history = History(maxBytes: 100 * MiB);
        for (var i = 0; i < 5; i++) history.Do(new SizedAction("tool" + i, 40 * MiB, log));

        Assert.Equal(80 * MiB, history.RetainedBytes);
        log.Clear();
        Assert.Equal(2, UndoAll(history));
        Assert.Equal(["undo tool4", "undo tool3"], log);
    }

    [Fact]
    public void AStepHeavierThanTheWholeBudget_IsStillUndoable()
    {
        var log = new List<string>();
        var history = History(maxBytes: 10 * MiB);
        history.Do(new SizedAction("small", 0, log));
        history.Do(new SizedAction("huge", 60 * MiB, log));

        // The tool the charter just ran is never collapsed for bytes, and collapsing the
        // weightless edit before it would free nothing, so both stay.
        Assert.Equal(2, UndoAll(history));
    }

    [Fact]
    public void OrdinaryEdits_AreNeverCollapsedForBytes_TheyHoldNone()
    {
        var log = new List<string>();
        var history = History(maxBytes: 50 * MiB);
        for (var i = 0; i < 30; i++) history.Do(new SizedAction("nudge" + i, 0, log));
        history.Do(new SizedAction("tool", 60 * MiB, log));

        Assert.Equal(31, UndoAll(history));
    }

    [Fact]
    public void AHeavyAbandonedBranch_GoesBeforeTheCurrentPath()
    {
        var log = new List<string>();
        var history = History(maxBytes: 100 * MiB);
        history.Do(new SizedAction("base", 40 * MiB, log));
        history.Do(new SizedAction("abandoned", 40 * MiB, log));
        history.Undo();
        history.Do(new SizedAction("kept", 40 * MiB, log));

        Assert.Equal(80 * MiB, history.RetainedBytes);
        Assert.False(history.CanRedo);
        Assert.Equal(2, UndoAll(history));
    }

    [Fact]
    public void Clear_ReleasesEveryByte()
    {
        var history = History(maxBytes: 100 * MiB);
        history.Do(new SizedAction("tool", 30 * MiB, []));
        history.Clear();
        Assert.Equal(0, history.RetainedBytes);
    }

    [Fact]
    public void ACompositeWeighsWhatItsChildrenWeigh()
    {
        var composite = new CompositeAction([new SizedAction("a", 3 * MiB, []), new SizedAction("b", 4 * MiB, [])]);
        Assert.Equal(7 * MiB, composite.EstimatedBytes);
    }

    /// <summary>A real whole-project tool's undo step reports BOTH of its project clones (a tempo
    /// tool moves the shared beat grid, so it snapshots the whole project).</summary>
    [Fact]
    public void AWholeProjectToolStep_ReportsItsTwoProjectClones()
    {
        var h = new ChartPowerToolsTests.Harness();
        var before = ChartProjectFootprint.EstimateBytes(h.State.ActiveProject!);

        h.Maintenance.AddMeasuresAtEnd(2);
        Assert.True(h.History.CanUndo);

        var after = ChartProjectFootprint.EstimateBytes(h.State.ActiveProject!);
        Assert.Equal(before + after, h.History.RetainedBytes);
    }

    /// <summary>
    ///     The estimate tracks what a clone really allocates. The per-element costs come from
    ///     measured clones; this pins them on a project with every kind of element, so a cloner
    ///     that starts copying much more (or the domain growing a heavy field) shows up here.
    /// </summary>
    [Fact]
    public void TheFootprintEstimate_IsWithinAQuarterOfAMeasuredClone()
    {
        var project = MixedProject();

        var measured = AllocationProbe.MeasureCheapest(() => () => ChartProjectCloner.Clone(project));
        var estimated = ChartProjectFootprint.EstimateBytes(project);

        var ratio = (double)estimated / measured;
        Assert.True(ratio is > 0.75 and < 1.25,
            $"estimated {estimated:N0} bytes for a clone that allocated {measured:N0} (ratio {ratio:F2})");
    }

    private static ChartProject MixedProject()
    {
        var project = new ChartProject();
        for (var i = 0; i < 400; i++) project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120 });
        var track = new VocalTrack { Name = "Vox" };
        var phrase = new LyricPhrase();
        for (var i = 0; i < 300; i++) phrase.Lyrics.Add(new LyricEvent { Position = new FractionalPosition(i), Text = "la" });
        track.Phrases.Add(phrase);
        project.VocalTracks.Add(track);

        for (var a = 0; a < 2; a++)
        {
            var arrangement = new Arrangement { Name = "Lead" + a };
            for (var l = 0; l < 6; l++)
            {
                var level = new ChartLevel { DifficultyIndex = l };
                for (var i = 0; i < 300 * (l + 1); i++)
                {
                    level.Notes.Add(new GuitarNote { Position = new FractionalPosition(i), StringIndex = i % 6, Fret = i % 12 });
                    if (i % 4 == 0)
                    {
                        var chord = new Chord { Position = new FractionalPosition(i) };
                        for (var s = 0; s < 3; s++) chord.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = 2 });
                        level.Chords.Add(chord);
                    }

                    if (i % 8 == 0) level.Anchors.Add(new FretAnchor { Position = new FractionalPosition(i), Fret = 1 });
                    if (i % 16 == 0) level.HandShapes.Add(new HandShape { Position = new FractionalPosition(i) });
                }

                arrangement.Levels.Add(level);
            }

            project.Arrangements.Add(arrangement);
        }

        return project;
    }
}
