using DotCharter.Presentation.Dialogs;
using DotCharter.Presentation.Shared;
using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Fixing;
using DotCharter.Services.Editing.FretAnchors;
using DotCharter.Services.Editing.Maintenance;
using DotCharter.Services.Editing.Phrases;
using DotCharter.Services.Editing.Refret;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Services.Export.Validation;
using DotCharter.Tests.Presentation.Dialogs;
using DotCharter.Tone.Audio.Dsp.Tones;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.Services.Editing.Maintenance;

/// <summary>
///     The export finding "no definition for tone X" (#195), through the Fix the export dialog and
///     the Inspector's readiness block share (<see cref="RocksmithFixes" />), on the REAL maintenance
///     service: when the project leaves no doubt the Fix renames the tone in place as ONE undo step
///     and the re-check is clean; otherwise it keeps opening the Tone designer and edits nothing.
/// </summary>
public sealed class UndefinedToneKeyFixTests
{
    private const string Old = "lead_dist";
    private const string New = "lead_crunch";

    [Fact]
    public async Task AnUnambiguousFinding_IsRenamedInPlace_AsOneUndoStep_AndTheRecheckIsClean()
    {
        var h = new Harness(SongTabBroken());
        var before = ChartProjectCloner.Clone(h.Project);
        var finding = Assert.Single(h.Findings(), f => f.MessageKey == ChartExportValidator.KeyToneNotDefined);
        Assert.Equal(New, finding.Detail);

        Assert.True(h.Fixes.CanFix(finding));
        var tip = h.Fixes.Describe(finding);
        Assert.Contains(Old, tip);
        Assert.Contains(New, tip);

        await h.Fixes.FixAsync(finding);

        Assert.DoesNotContain(h.Findings(), f => f.MessageKey == ChartExportValidator.KeyToneNotDefined);
        Assert.Equal(New, Assert.Single(h.Project.ToneDefinitions).Key);
        Assert.Empty(h.Dialogs.Calls);
        Assert.Contains(Old, h.State.MainDisplayMessage);

        h.History.Undo();
        Assert.False(h.History.CanUndo, "the repair must be ONE undo step");
        Assert.Empty(DeepCompare.Diff("project", before, h.Project, new HashSet<string>()));
    }

    /// <summary>Two undefined keys: which defined tone either one meant is a guess, so the finding
    /// stays a report and its Fix is the Tone designer, as before.</summary>
    [Fact]
    public async Task AnAmbiguousFinding_KeepsOpeningTheToneDesigner_AndEditsNothing()
    {
        var project = SongTabBroken();
        project.Arrangements[0].ToneB = "solo";
        project.Arrangements[0].ToneChanges.Add(new ToneChange { Position = Pos(6000, project), ToneName = "solo" });
        var h = new Harness(project);
        var before = ChartProjectCloner.Clone(project);
        var finding = h.Findings().First(f => f.MessageKey == ChartExportValidator.KeyToneNotDefined);

        Assert.True(h.Fixes.CanFix(finding));
        Assert.Equal(FakeLocalization.English().Format("Export_FixTip_Tones", "Lead"), h.Fixes.Describe(finding));

        await h.Fixes.FixAsync(finding);

        Assert.Contains(h.Dialogs.Calls, c => c.StartsWith(nameof(IDialogs.ShowAsync), StringComparison.Ordinal));
        Assert.False(h.History.CanUndo);
        Assert.Empty(DeepCompare.Diff("project", before, h.Project, new HashSet<string>()));
    }

    [Fact]
    public void TheVerb_RefusesAKeyTheRepairIsNotFor()
    {
        var h = new Harness(SongTabBroken());

        Assert.Null(h.Maintenance.RepairUndefinedToneKey(Old));
        Assert.False(h.History.CanUndo);
    }

    /// <summary>What the Inspector's Song tab used to leave: the tone changes and the starting tone
    /// on the new key, the definition and the slots on the old one.</summary>
    private static ChartProject SongTabBroken()
    {
        var project = new ChartProject { Chart = { SongName = "Tone Song", Artist = "DotCharter" } };
        for (var i = 0; i < 16; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500.0, IsMeasure = i % 4 == 0, Bpm = 120, BeatsInMeasure = 4, NoteDenominator = 4 });
        project.ToneDefinitions.Add(new ToneDefinition
        {
            Key = Old, Name = Old, Json = RsToneJsonWriter.Write(RsToneAuthoring.CreateDefaultTone(Old, Old, RsGearCatalog.Shared))
        });
        var lead = new Arrangement { ArrangementType = ArrangementType.Lead, Name = "Lead", ToneBase = Old, ToneA = Old, StartingTone = New };
        lead.ToneNames.Add(Old);
        lead.ToneChanges.Add(new ToneChange { Position = Pos(4000, project), ToneName = New });
        var level = new ChartLevel { DifficultyIndex = 0 };
        level.Notes.Add(new GuitarNote { Position = Pos(2000, project), StringIndex = 0, Fret = 3 });
        lead.Levels.Add(level);
        project.Arrangements.Add(lead);
        return project;
    }

    private static FractionalPosition Pos(double ms, ChartProject project) => TimelineMath.PositionFromMs(ms, project.Beats);

    private sealed class Harness
    {
        public Harness(ChartProject project)
        {
            var logger = new FakeAppLogger();
            History = new HistoryService(logger);
            var config = new FakeConfigurationService();
            State.ActiveProject = project;
            State.ActiveArrangement = project.Arrangements[0];
            State.ActiveLevel = project.Arrangements[0].Levels[0];

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
            (var surface, _) = RecordingProxy.Create<IChartEditorSurface>();
            (var dialogs, Dialogs) = RecordingProxy.Create<IDialogs>();
            Fixes = new RocksmithFixes(Maintenance, surface, State, dialogs, FakeLocalization.English(), logger);
        }

        public ProjectState State { get; } = new();
        public HistoryService History { get; }
        public ChartMaintenanceService Maintenance { get; }
        public RocksmithFixes Fixes { get; }
        public RecordingProxy Dialogs { get; }
        public ChartProject Project => State.ActiveProject!;

        public IReadOnlyList<ExportValidationFinding> Findings() => new ChartExportValidator().Validate(Project);
    }
}
