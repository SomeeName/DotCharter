using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Fixing;
using DotCharter.Services.Editing.FretAnchors;
using DotCharter.Services.Editing.Maintenance;
using DotCharter.Services.Editing.Phrases;
using DotCharter.Services.Editing.Refret;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Services.Export;
using DotCharter.Services.Export.DynamicDifficulty;
using DotCharter.Services.Export.Validation;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.Services.Editing.Maintenance;

/// <summary>
///     The chart's "Apply DLC Builder fixes" (the one way DLC Builder's fixes and generated levels
///     get INTO a project; every export applies them to its copy anyway) and the per-finding Fix
///     (#184), on the REAL services: a single-level chart gets levels, the whole run is ONE undo
///     step that restores the chart exactly, and an arrangement it was not asked about is untouched.
/// </summary>
public sealed class ApplyDlcBuilderFixesTests
{
    private const double BeatMs = 500;

    [Fact]
    public void ASingleLevelChart_GetsDlcBuildersLevels_InTheProject()
    {
        var h = new Harness(NotesOnly());

        var changed = h.Maintenance.ApplyDlcBuilderFixes();

        Assert.Equal(1, changed);
        Assert.True(h.Project.Arrangements[0].Levels.Count > 1);
        // What the export would ship: the same steps on the same chart give the same ladder.
        var same = WithStructure(NotesOnly());
        var expected = DlcBuilderBuildSteps.Apply(same, same.Arrangements[0], null, new DlcBuilderSteps(true, true));
        Assert.NotNull(expected);
        Assert.Equal(expected.Levels.Count, h.Project.Arrangements[0].Levels.Count);
    }

    [Fact]
    public void TheRun_IsOneUndoStep_ThatRestoresTheChartExactly()
    {
        var h = new Harness(NotesOnly());
        var before = ChartProjectCloner.Clone(h.Project);

        h.Maintenance.ApplyDlcBuilderFixes();
        Assert.True(h.History.CanUndo);
        h.History.Undo();

        Assert.False(h.History.CanUndo); // ONE node, not one per step
        var diffs = DeepCompare.Diff("project", before, h.Project, new HashSet<string>());
        Assert.True(diffs.Count == 0, "one Undo left the chart different:\n" + string.Join("\n", diffs.Take(20)));
    }

    [Fact]
    public void AnArrangementItWasNotAskedAbout_IsUntouched()
    {
        var project = NotesOnly();
        var rhythm = NotesOnly().Arrangements[0];
        rhythm.Name = "Rhythm";
        project.Arrangements.Add(rhythm);
        var h = new Harness(project);
        var untouched = ChartProjectCloner.Clone(h.Project).Arrangements[1];

        h.Maintenance.ApplyDlcBuilderFixes();

        var diffs = DeepCompare.Diff("rhythm", untouched, h.Project.Arrangements[1], new HashSet<string>());
        Assert.True(diffs.Count == 0, "the run touched an arrangement it was not asked to:\n" + string.Join("\n", diffs.Take(10)));
    }

    private static ChartProject WithStructure(ChartProject project)
    {
        PhraseSectionGenerator.GenerateIfAbsent(project.Arrangements[0], project.Beats, 0);
        return project;
    }

    /// <summary>One fixture per chart-editing fix kind; <see cref="EveryChartFix_HasAFixture" /> keeps the list whole.</summary>
    private static readonly string[] FixableKeyList =
    [
        ChartExportValidator.KeyMissingFhpCoverage,
        ChartExportValidator.KeyOverlappingNotes,
        ChartExportValidator.KeyNoteBelowCapo,
        ChartExportValidator.KeyNoPhrases
    ];

    public static TheoryData<string> FixableKeys() => [.. FixableKeyList];

    [Fact]
    public void EveryChartFix_HasAFixture()
    {
        var covered = FixableKeyList.ToHashSet();
        var chartFixes = ExportFindingFixes.Fixes.Where(kv => kv.Value != ExportFixKind.OpenToneDesigner).Select(kv => kv.Key);
        Assert.DoesNotContain(chartFixes, k => !covered.Contains(k));
    }

    [Theory]
    [MemberData(nameof(FixableKeys))]
    public void EachFix_ClearsItsFinding_AsOneUndoStep(string key)
    {
        var h = new Harness(FixtureFor(key));
        var finding = Assert.Single(h.Findings(), f => f.MessageKey == key);
        var before = ChartProjectCloner.Clone(h.Project);

        var changes = h.Maintenance.FixExportFinding(finding.Fix, h.Arrangement);

        Assert.True(changes > 0);
        Assert.DoesNotContain(h.Findings(), f => f.MessageKey == key);
        h.History.Undo();
        Assert.False(h.History.CanUndo);
        Assert.Empty(DeepCompare.Diff("project", before, h.Project, new HashSet<string>()));
    }

    [Fact]
    public void TheFhpFix_KeepsEveryPlacedPosition()
    {
        var h = new Harness(FixtureFor(ChartExportValidator.KeyMissingFhpCoverage));
        var placed = h.Arrangement.Levels[0].Anchors.Select(a => (a.Position, a.Fret, a.Width)).ToList();

        h.Maintenance.FixExportFinding(ExportFixKind.FillFhp, h.Arrangement);

        var after = h.Arrangement.Levels[0].Anchors.Select(a => (a.Position, a.Fret, a.Width)).ToList();
        Assert.All(placed, p => Assert.Contains(p, after));
        Assert.True(after.Count > placed.Count);
    }

    [Fact]
    public void AToneFix_IsNotAChartEdit()
    {
        var h = new Harness(NotesOnly());

        Assert.Equal(0, h.Maintenance.FixExportFinding(ExportFixKind.OpenToneDesigner, h.Arrangement));
        Assert.False(h.History.CanUndo);
    }

    // ---- fixtures ----------------------------------------------------------------

    /// <summary>Notes only: no anchors, no phrases, one level. The notes start at beat 4 (2 s).</summary>
    private static ChartProject NotesOnly()
    {
        var project = new ChartProject();
        for (var i = 0; i < 64; i++)
            project.Beats.Add(new BeatMarker { Time = i * BeatMs, Bpm = 120, BeatsInMeasure = 4, NoteDenominator = 4, IsMeasure = i % 4 == 0 });

        var arrangement = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead, StringCount = 6 };
        for (var s = 0; s < 6; s++) arrangement.TuningPitches.Add(new TuningOffset(0));
        var level = new ChartLevel();
        for (var beat = 4; beat < 40; beat++)
            level.Notes.Add(new GuitarNote { Position = new FractionalPosition(beat), StringIndex = beat % 6, Fret = 3 + beat % 5 });
        arrangement.Levels.Add(level);
        project.Arrangements.Add(arrangement);
        return project;
    }

    private static ChartProject FixtureFor(string key)
    {
        var project = NotesOnly();
        var arrangement = project.Arrangements[0];
        var level = arrangement.Levels[0];
        if (key != ChartExportValidator.KeyNoPhrases)
        {
            // Phrased and anchored, so ONLY the finding under test fires.
            arrangement.EventPoints.Add(new EventPoint { Position = FractionalPosition.Zero, Phrase = "COUNT" });
            arrangement.EventPoints.Add(new EventPoint { Position = new FractionalPosition(4), Phrase = "verse", Section = "verse" });
            arrangement.EventPoints.Add(new EventPoint { Position = new FractionalPosition(44), Phrase = "END" });
            level.Anchors.Add(new FretAnchor(new FractionalPosition(4), 3, 4));
        }

        switch (key)
        {
            case ChartExportValidator.KeyMissingFhpCoverage:
                level.Anchors.Clear();
                level.Anchors.Add(new FretAnchor(new FractionalPosition(12), 9, 3));
                break;
            case ChartExportValidator.KeyOverlappingNotes:
                // Beat 4 is on string 4 and rings into beat 10, also on string 4.
                level.Notes[0].Sustain = new FractionalPosition(8);
                break;
            case ChartExportValidator.KeyNoteBelowCapo:
                arrangement.Capo = 3;
                level.Anchors[0].Fret = 4;
                foreach (var note in level.Notes) note.Fret = Math.Max(note.Fret, 4);
                level.Notes[3].Fret = 2;
                break;
        }

        return project;
    }

    private sealed class Harness
    {
        public Harness(ChartProject project, ChartLevel? active = null)
        {
            var logger = new FakeAppLogger();
            History = new HistoryService(logger);
            var config = new FakeConfigurationService();
            State.ActiveProject = project;
            State.ActiveArrangement = project.Arrangements[0];
            State.ActiveLevel = active ?? project.Arrangements[0].Levels[0];

            var selection = new SelectionService();
            var transport = new TransportViewModel(new FakeAudioPlaybackService(), State, config, FakeLocalization.KeysOnly());
            var editor = new ChartEditorViewModel(State, transport, History, selection, new TempoService(logger),
                new TrackContextRegistry(State, selection), FakeLocalization.KeysOnly());
            var anchors = new FretAnchorGenerationService();
            Maintenance = new ChartMaintenanceService(config, new ChartImprovementService(), new LevelSquashService(),
                new LevelGenerationService(), new PhraseIterationPropagationService(History, new EditingScope(State), logger),
                anchors, new ChartPowerToolsTests.NoOpProjectSessionService(), History, logger, State, editor,
                new ArrangementAutoFixService(), new RefretService(anchors, new FingeringGenerationService()),
                FakeLocalization.English());
        }

        public ProjectState State { get; } = new();
        public HistoryService History { get; }
        public ChartMaintenanceService Maintenance { get; }
        public ChartProject Project => State.ActiveProject!;
        public Arrangement Arrangement => Project.Arrangements[0];

        public IReadOnlyList<ExportValidationFinding> Findings() => new ChartExportValidator().Validate(Project);
    }
}
