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

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ImportedGraphs_AreChargedAndPrunedByTheByteBudget(int kind)
    {
        var project = new ChartProject();
        var arrangement = new Arrangement();
        var level = new ChartLevel();
        arrangement.Levels.Add(level);
        var vocals = new VocalTrack();
        var phrase = new LyricPhrase();
        vocals.Phrases.Add(phrase);
        var beats = new List<BeatMarker>();
        var lights = new List<Showlight>();
        for (var i = 0; i < 100; i++)
        {
            level.Notes.Add(new GuitarNote { Position = new FractionalPosition(i) });
            phrase.Lyrics.Add(new LyricEvent { Text = "word", Position = new FractionalPosition(i) });
            beats.Add(new BeatMarker { Time = i * 500 });
            lights.Add(new Showlight { Position = new FractionalPosition(i) });
        }

        var plan = new DotCharter.Services.Parsing.Xml.RsXmlImportPlan
        {
            Kind = kind == 0 ? DotCharter.Services.Parsing.Xml.RsXmlImportKind.Song :
                kind == 1 ? DotCharter.Services.Parsing.Xml.RsXmlImportKind.Vocals : DotCharter.Services.Parsing.Xml.RsXmlImportKind.Showlights,
            Arrangement = kind == 0 ? arrangement : null,
            VocalTrack = kind == 1 ? vocals : null,
            Beats = beats,
            Showlights = kind == 2 ? lights : []
        };
        var action = (IChartAction)new DotCharter.Services.Parsing.Xml.ImportRsXmlAction(project, plan, () => { });
        var charge = action.EstimatedBytes;
        Assert.True(charge > 10_000);
        var history = History(charge - 1);
        history.Do(action);
        Assert.Equal(charge, history.RetainedBytes);
        // The current step stays undoable; the next edit makes the import eligible for pruning.
        history.Do(new SizedAction("following edit", 0, []));
        Assert.Equal(1, UndoAll(history));
        Assert.Equal(0, history.RetainedBytes);
        Assert.True(kind == 0 ? project.Arrangements.Count == 1 :
            kind == 1 ? project.VocalTracks.Count == 1 : project.Showlights.Count == 100);
    }

    [Fact]
    public void NotationVoicesAndTheirOwnedSoundsIncreaseTheFootprintByTheirExpectedBytes()
    {
        var arrangement = new Arrangement();
        var before = ChartProjectFootprint.EstimateBytes(arrangement);
        var voice = new NotationVoice();
        voice.Notes.Add(new GuitarNote());
        var chord = new Chord();
        chord.ChordNotes.Add(new ChordNote());
        voice.Chords.Add(chord);
        arrangement.NotationVoices.Add(voice);

        Assert.Equal(1500 + 300 + 270 + 270, ChartProjectFootprint.EstimateBytes(arrangement) - before);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SettingsChargeGrowsForBothBeforeAndAfterSnapshots(bool growBefore)
    {
        var before = new Arrangement();
        var after = new Arrangement();
        var baseline = SettingsCharge(before, after);
        var changed = growBefore ? before : after;
        changed.NotationVoices.Add(new NotationVoice());
        // A sealed snapshot also owns its collection/family table (512) and
        // identity entry (192), with the estimator's two-graph reserve.
        Assert.Equal(2 * (1500 + 512 + 192), SettingsCharge(before, after) - baseline);
    }

    private static long SettingsCharge(Arrangement before, Arrangement after) =>
        new DotCharter.Services.Editing.Arrangements.MutateArrangementSettingsAction(
            new Arrangement(), before, after, null).EstimatedBytes;

    [Fact]
    public void ArrangementSettingsSnapshots_ChargeTheirRetainedGraphs()
    {
        var live = new ChartPowerToolsTests.Harness().State.ActiveArrangement!;
        var before = ChartProjectCloner.CloneArrangement(live, retainIdentities: true);
        var after = ChartProjectCloner.CloneArrangement(live, retainIdentities: true);
        after.Capo = 3;
        var action = (IChartAction)new DotCharter.Services.Editing.Arrangements.MutateArrangementSettingsAction(
            live, before, after, null);

        Assert.Equal(ChartProjectFootprint.EstimateHistoryBytes(before) + ChartProjectFootprint.EstimateHistoryBytes(after),
            action.EstimatedBytes);
    }

    [Fact]
    public void EmptyBendCollections_DoNotAddPerNoteIdentityTablesToTheHistoryCharge()
    {
        var arrangement = new Arrangement();
        var level = new ChartLevel();
        arrangement.Levels.Add(level);
        for (var i = 0; i < 5000; i++) level.Notes.Add(new GuitarNote { Position = new FractionalPosition(i) });
        var plain = ChartProjectCloner.CloneArrangement(arrangement);
        var snapshot = ChartProjectCloner.CloneArrangement(arrangement, retainIdentities: true);

        // Includes the conservative original-graph reserve, but excludes thousands of empty CWTs.
        Assert.True(ChartProjectFootprint.EstimateHistoryBytes(snapshot) < 4 * ChartProjectFootprint.EstimateBytes(plain));
    }

    [Fact]
    public void IdentityMetadata_IsIncludedInTheFootprint()
    {
        var project = new ChartPowerToolsTests.Harness().State.ActiveProject!;
        var ordinary = ChartProjectCloner.Clone(project);
        var snapshot = ChartProjectCloner.Clone(project, retainIdentities: true);

        Assert.True(ChartProjectFootprint.EstimateBytes(snapshot) > ChartProjectFootprint.EstimateBytes(ordinary));
    }

    [Fact]
    public void DetachedOriginalLevels_AreStillChargedWhileUndoRetainsThem()
    {
        var arrangement = new ChartPowerToolsTests.Harness().State.ActiveArrangement!;
        var snapshot = ChartProjectCloner.CloneArrangement(arrangement, retainIdentities: true);
        var populated = ChartProjectFootprint.EstimateBytes(arrangement);
        arrangement.Levels.Clear();

        Assert.True(ChartProjectFootprint.EstimateBytes(arrangement) >= populated);
        GC.KeepAlive(snapshot);
    }

    [Fact]
    public void WorkingSnapshots_ReleaseMappingsForRemovedLevels()
    {
        var arrangement = new ChartPowerToolsTests.Harness().State.ActiveArrangement!;
        var before = ChartProjectCloner.CloneArrangement(arrangement, retainIdentities: true);
        var working = ChartProjectCloner.CloneArrangement(arrangement, retainIdentities: true);
        working.Levels.Clear();
        _ = ChartProjectFootprint.EstimateBytes(working);
        var identities = typeof(ChartProjectCloner).Assembly.GetType("DotCharter.Core.Utilities.ChartSnapshotIdentities")!;
        var tables = identities.GetField("Collections", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null)!;
        var args = new object?[] { working.Levels, null };
        Assert.True((bool)tables.GetType().GetMethod("TryGetValue")!.Invoke(tables, args)!);
        var instances = (System.Collections.IEnumerable)args[1]!.GetType().GetProperty("Instances")!.GetValue(args[1])!;

        Assert.Empty(instances.Cast<object>());
        GC.KeepAlive(before);
    }

    [Fact]
    public void SnapshotSteps_ChargeTheOriginalGraphAsWellAsTheTwoCopies()
    {
        var h = new ChartPowerToolsTests.Harness();
        var bare = ChartProjectFootprint.EstimateBytes(ChartProjectCloner.Clone(h.State.ActiveProject!));
        h.Maintenance.RunAsSingleUndoStep(() => h.State.ActiveArrangement!.Name = "Edited");

        Assert.True(h.History.RetainedBytes > 3 * bare);
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

    /// <summary>A real whole-project tool's undo step reports its snapshots and their retained identities (a tempo
    /// tool moves the shared beat grid, so it snapshots the whole project).</summary>
    [Fact]
    public void AWholeProjectToolStep_ReportsItsTwoProjectClones()
    {
        var h = new ChartPowerToolsTests.Harness();
        var before = ChartProjectFootprint.EstimateHistoryBytes(
            ChartProjectCloner.Clone(h.State.ActiveProject!, retainIdentities: true));

        h.Maintenance.AddMeasuresAtEnd(2);
        Assert.True(h.History.CanUndo);

        var after = ChartProjectFootprint.EstimateHistoryBytes(
            ChartProjectCloner.Clone(h.State.ActiveProject!, retainIdentities: true));
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
