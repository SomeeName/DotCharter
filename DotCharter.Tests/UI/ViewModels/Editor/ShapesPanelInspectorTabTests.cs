using DotCharter.Presentation.Dialogs;
using DotCharter.Presentation.Screens.Editor;
using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Collisions;
using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Highway;
using DotCharter.Services.Editing.Lyrics;
using DotCharter.Services.Editing.Maintenance;
using DotCharter.Services.Editing.Quality;
using DotCharter.Services.Editing.Targets;
using DotCharter.Services.Import.Lyrics;
using DotCharter.Tests.Presentation.Dialogs;
using DotCharter.Tests.Services.Editing.ChordShapes;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Editor.Inspector;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     The inspector's auto-switch to the Sound tab on a picked chord leaves the Chord shapes tab alone when the pick
///     is made FROM that tab: a tick or a press on its Uses page selects a use and the tab stays. A chord picked
///     anywhere else still brings the Sound tab, so the switch this guards is live.
/// </summary>
public sealed class ShapesPanelInspectorTabTests
{
    private readonly ChartEditHarness _chart = new();
    private readonly ChordShapesFixture _f = new();
    private readonly ShapesPanelViewModel _shapes;
    private readonly InspectorViewModel _inspector;

    public ShapesPanelInspectorTabTests()
    {
        _chart.Project.Arrangements.Add(_f.Arrangement);
        _chart.Editor.ActiveArrangement = _f.Arrangement;
        _chart.State.ActiveArrangement = _f.Arrangement;
        _chart.State.ActiveLevel = _f.Level1;
        var logger = new FakeAppLogger();
        var config = new FakeConfigurationService();
        var english = FakeLocalization.English();
        var ui = new TestUiDispatcher();
        var dialogs = new ScriptedDialogs(_ => throw new InvalidOperationException("no dialog here"), _ => { });
        _shapes = new ShapesPanelViewModel(_chart.Editor, _chart.History, _chart.State, english, ui,
            new ShapesPanelParts(ChordShapePlacementServiceTests.Build(_chart.State, _chart.Editor.Selection, _chart.History),
                _chart.Transport, dialogs));

        var selection = _chart.Editor.Selection;
        var scope = new EditingScope(_chart.State);
        var highway = new HighwayEditingService(_chart.State, selection, _chart.History, new StrictCollisionService(), config,
            scope, new EditTargetService(selection, scope, config), logger);
        var maintenance = RecordingProxy.Create<IChartMaintenanceService>().Proxy;
        var readiness = new ExportReadinessViewModel(_chart.State, new CheckpointsViewModelTests.FakeDocumentService(),
            _chart.Transport, _chart.Editor, english, maintenance, new DialogsSlot(), logger,
            new ChartQualityReporter(new ArrangementValidationService(logger)), ui);
        var stemMixer = new StemMixerViewModel(_chart.State, new FakeAudioPlaybackService(), english, logger, ui);
        _inspector = new InspectorViewModel(
            _chart.Editor,
            _chart.History,
            highway,
            new ChordTemplateEditorViewModel(_chart.Editor, _chart.History, new FingeringGenerationService(), english, ui,
                projectState: _chart.State),
            new SongOverviewViewModel(_chart.State, _chart.History, logger, english, _chart.Editor),
            _chart.Transport,
            _chart.State,
            new LyricsInspectorViewModel(_chart.Editor, _chart.History, _chart.State, logger, new FakeFileStorageService(),
                RecordingProxy.Create<ILyricsFileService>().Proxy, RecordingProxy.Create<ILyricAutoSyncRunner>().Proxy, english),
            stemMixer,
            new AnnotationsInspectorViewModel(_chart.State, _chart.History, _chart.Editor, english),
            readiness,
            new InspectorIssuesViewModel(_chart.State, _chart.Editor, new ArrangementValidationService(logger), maintenance,
                readiness, _chart.History, english, () => null, ui, logger),
            new HandShapeEditorViewModel(_chart.Editor, _chart.History, _chart.Transport, english),
            new StringMaskViewModel(_chart.State, english),
            english,
            config,
            new DotCharter.UI.ViewModels.Shell.StemSeparationViewModel(_chart.State, RecordingProxy.Create<DotCharter.Services.Session.IProjectSessionService>().Proxy, stemMixer,
                RecordingProxy.Create<DotCharter.UI.ViewModels.Shell.IAppDialogService>().Proxy, ui, logger, english),
            chordShapes: _shapes);
        _shapes.CurrentRow = _shapes.AllRows.Single(r => r.Template.Id == _f.AmAlt.Id);
        _inspector.SelectedTabIndex = InspectorViewModel.ChordShapesTabIndex;
    }

    [Fact]
    public void ATickOnTheUsesPage_KeepsTheChordShapesTab()
    {
        _shapes.Uses[2].IsChecked = true;

        Assert.NotEmpty(_chart.Editor.Selection.SelectedChords);
        Assert.Equal(InspectorViewModel.ChordShapesTabIndex, _inspector.SelectedTabIndex);
    }

    [Fact]
    public void APressOnAUse_KeepsTheChordShapesTab()
    {
        _shapes.Uses[2].JumpCommand.Execute(null);

        Assert.NotNull(_chart.Editor.Selection.SelectedChord);
        Assert.Equal(InspectorViewModel.ChordShapesTabIndex, _inspector.SelectedTabIndex);
    }

    [Fact]
    public void AChordPickedElsewhere_StillBringsTheSoundTab()
    {
        var chord = _f.Level1.Chords.First(c => c.TemplateId == _f.C.Id);
        _chart.Editor.Selection.SelectedChords.Add(chord);
        _chart.Editor.Selection.SelectedChord = chord;

        Assert.Equal(InspectorViewModel.SoundTabIndex, _inspector.SelectedTabIndex);
    }
}
