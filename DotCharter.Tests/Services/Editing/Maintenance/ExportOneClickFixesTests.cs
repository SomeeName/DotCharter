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
///     The two warnings every export repeats get a one-click Fix in the export dialog's findings,
///     through <see cref="RocksmithFixes" /> on the REAL maintenance service. Each is ONE undo step and
///     the re-check is clean afterwards.
/// </summary>
public sealed class ExportOneClickFixesTests
{
    [Fact]
    public async Task NoShowLights_FixStoresTheDefaultSet_AsOneUndoStep_AndTheWarningIsGone()
    {
        var h = new Harness(Project(withTones: true));
        var finding = Assert.Single(h.Findings(), f => f.MessageKey == ChartExportValidator.KeyNoShowlights);
        Assert.True(h.Fixes.CanFix(finding));
        Assert.Contains("show lights", h.Fixes.Describe(finding), StringComparison.OrdinalIgnoreCase);

        await h.Fixes.FixAsync(finding);

        // Generated from the lead's notes (one pitch class), no sections: fog + beam + closing fog + lasers on/off.
        var generated = h.Project.Showlights.Count;
        Assert.True(generated >= 3, "fog, beam and the closing fog at least");
        Assert.Contains(h.Project.Showlights, l => l.Type == "BEAMS_TEAL");
        Assert.DoesNotContain(h.Findings(), f => f.MessageKey == ChartExportValidator.KeyNoShowlights);

        h.History.Undo();
        Assert.Empty(h.Project.Showlights);
        Assert.False(h.History.CanUndo, "the fix must be ONE undo step");
        h.History.Redo();
        Assert.Equal(generated, h.Project.Showlights.Count);
    }

    [Fact]
    public async Task NoShowLights_AProjectThatHasSome_HasNoFinding_AndTheFixChangesNothing()
    {
        var project = Project(withTones: true);
        project.Showlights.Add(new Showlight { Position = Pos(0, project), Type = "FOG_RED", Note = 29 });
        var h = new Harness(project);

        Assert.DoesNotContain(h.Findings(), f => f.MessageKey == ChartExportValidator.KeyNoShowlights);
        Assert.Equal(0, h.Maintenance.GenerateShowlights());
        Assert.False(h.History.CanUndo);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task MissingTones_FixCreatesADefinitionPerName_AsOneUndoStep_AndTheWarningsAreGone()
    {
        var project = Project(withTones: false);
        var h = new Harness(project);
        var findings = h.Findings().Where(f => f.MessageKey == ChartExportValidator.KeyToneNotDefined).ToList();
        Assert.Equal(2, findings.Count);
        Assert.True(h.Fixes.CanFix(findings[0]));
        Assert.Contains("Lead", h.Fixes.Describe(findings[0]));

        await h.Fixes.FixAsync(findings[0]);

        Assert.Equal(["clean_verse", "heavy_chorus"], h.Project.ToneDefinitions.Select(t => t.Key).Order().ToArray());
        Assert.All(h.Project.ToneDefinitions, t => Assert.True(RsToneJsonParser.TryParse(t.Json, out _)));
        Assert.DoesNotContain(h.Findings(), f => f.MessageKey == ChartExportValidator.KeyToneNotDefined);

        h.History.Undo();
        Assert.Empty(h.Project.ToneDefinitions);
        Assert.False(h.History.CanUndo, "the fix must be ONE undo step");
        h.History.Redo();
        Assert.Equal(2, h.Project.ToneDefinitions.Count);
    }

    [Fact]
    public void MissingTones_ADefinedToneIsLeftAlone()
    {
        var project = Project(withTones: false);
        project.ToneDefinitions.Add(new ToneDefinition { Key = "clean_verse", Name = "Mine", Json = "{\"mine\":true}" });
        var h = new Harness(project);

        Assert.Equal(1, h.Maintenance.CreateMissingToneDefinitions(project.Arrangements[0]));
        Assert.Equal("{\"mine\":true}", project.ToneDefinitions.Single(t => t.Key == "clean_verse").Json);
    }

    private static ChartProject Project(bool withTones)
    {
        var project = new ChartProject { Chart = { SongName = "Tone Song", Artist = "DotCharter" } };
        for (var i = 0; i < 16; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500.0, IsMeasure = i % 4 == 0, Bpm = 120, BeatsInMeasure = 4, NoteDenominator = 4 });
        var lead = new Arrangement { ArrangementType = ArrangementType.Lead, Name = "Lead", ToneBase = "clean_verse", ToneA = "heavy_chorus", StartingTone = "clean_verse" };
        lead.ToneChanges.Add(new ToneChange { Position = Pos(4000, project), ToneName = "heavy_chorus" });
        var level = new ChartLevel { DifficultyIndex = 0 };
        level.Notes.Add(new GuitarNote { Position = Pos(2000, project), StringIndex = 0, Fret = 3 });
        lead.Levels.Add(level);
        project.Arrangements.Add(lead);
        if (!withTones) return project;
        foreach (var key in new[] { "clean_verse", "heavy_chorus" })
        {
            project.ToneDefinitions.Add(new ToneDefinition
            {
                Key = key, Name = key, Json = RsToneJsonWriter.Write(RsToneAuthoring.CreateDefaultTone(key, key, RsGearCatalog.Shared))
            });
        }

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
