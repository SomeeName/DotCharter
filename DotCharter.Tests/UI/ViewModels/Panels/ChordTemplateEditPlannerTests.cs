using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.Tests.UI.ViewModels.Panels;

public sealed class ChordTemplateEditPlannerTests
{
    private readonly HistoryService _history = new(new FakeAppLogger());
    private readonly SelectionService _selection = new();
    private readonly ProjectState _state = new();
    private readonly Arrangement _arrangement = new() { Name = "Lead", ArrangementType = ArrangementType.Lead };
    private readonly ChordTemplateEditPlanner _planner;

    public ChordTemplateEditPlannerTests()
    {
        var logger = new FakeAppLogger();
        var project = new ChartProject();
        _arrangement.Levels.Add(new ChartLevel());
        project.Arrangements.Add(_arrangement);
        _state.ActiveProject = project;
        _state.ActiveArrangement = _arrangement;
        _state.ActiveLevel = _arrangement.Levels[0];

        var config = new FakeConfigurationService();
        var transport = new TransportViewModel(new FakeAudioPlaybackService(), _state, config, FakeLocalization.KeysOnly());
        var editor = new ChartEditorViewModel(_state, transport, _history, _selection, new TempoService(logger),
            new TrackContextRegistry(_state, _selection), FakeLocalization.KeysOnly());
        _planner = new ChordTemplateEditPlanner(editor, _history, new FingeringGenerationService(), config, logger);
    }

    [Fact]
    public void SwitchSelection_ProjectsTheTargetsFrets_KeepingEachStringsOwnAttributes()
    {
        var target = Template(id: 7, (3, 7), (4, 7));
        _arrangement.ChordTemplates.Add(target);
        var chord = Chord(templateId: 0, (4, 5), (5, 5));
        chord.ChordNotes.Single(n => n.StringIndex == 4).Accent = true;
        var handShape = new HandShape { TemplateId = 0 };
        _arrangement.Levels[0].Chords.Add(chord);
        _arrangement.Levels[0].HandShapes.Add(handShape);

        _planner.SwitchSelection(target, [chord], [handShape]);

        Assert.Equal(7, chord.TemplateId);
        Assert.Equal(7, handShape.TemplateId);
        Assert.Equal([(3, 7), (4, 7)], chord.ChordNotes.Select(n => (n.StringIndex, n.Fret)).OrderBy(p => p.StringIndex));
        Assert.True(chord.ChordNotes.Single(n => n.StringIndex == 4).Accent);
        Assert.False(chord.ChordNotes.Single(n => n.StringIndex == 3).Accent);
    }

    [Fact]
    public void SwitchSelection_IsOneUndoNode()
    {
        var target = Template(id: 7, (3, 7), (4, 7));
        _arrangement.ChordTemplates.Add(target);
        var chord = Chord(templateId: 0, (4, 5), (5, 5));
        _arrangement.Levels[0].Chords.Add(chord);

        _planner.SwitchSelection(target, [chord], []);
        _history.Undo();

        Assert.False(_history.CanUndo);
        Assert.Equal(0, chord.TemplateId);
        Assert.Equal([(4, 5), (5, 5)], chord.ChordNotes.Select(n => (n.StringIndex, n.Fret)).OrderBy(p => p.StringIndex));
    }

    [Fact]
    public void AReshapeWithNothingSelected_IsRefused_AndWritesNothing()
    {
        var loaded = Template(id: 0, (4, 5), (5, 5));
        _arrangement.ChordTemplates.Add(loaded);
        _arrangement.Levels[0].Chords.Add(Chord(templateId: 0, (4, 5), (5, 5)));
        var reshaped = loaded.Clone();
        reshaped.Frets[5] = 7;

        var scope = _planner.ResolveReach(loaded, ChordApplySounds.None, ChordApplySounds.None)!;
        var shown = _planner.ApplyEdit(loaded, reshaped, scope, ChordApplyRung.EveryUse);

        Assert.Null(shown);
        Assert.False(_history.CanUndo);
        Assert.Equal(5, loaded.Frets[5]);
    }

    [Fact]
    public void AnIsolatedReshape_AnswersTheNewTemplate_AndLeavesTheSharedOneAlone()
    {
        var loaded = Template(id: 0, (4, 5), (5, 5));
        _arrangement.ChordTemplates.Add(loaded);
        var edited = Chord(templateId: 0, (4, 5), (5, 5));
        var sibling = Chord(templateId: 0, (4, 5), (5, 5));
        sibling.Position = new FractionalPosition(8);
        _arrangement.Levels[0].Chords.Add(edited);
        _arrangement.Levels[0].Chords.Add(sibling);
        var reshaped = loaded.Clone();
        reshaped.Frets[5] = 7;

        var sounds = new ChordApplySounds([edited], []);
        var scope = _planner.ResolveReach(loaded, sounds, sounds)!;
        var shown = _planner.ApplyEdit(loaded, reshaped, scope, ChordApplyRung.ThisChord)?.Shown;

        Assert.NotNull(shown);
        Assert.NotSame(loaded, shown);
        Assert.Equal(shown.Id, edited.TemplateId);
        Assert.Equal(0, sibling.TemplateId);
        Assert.Equal(5, loaded.Frets[5]);
    }

    private static ChordTemplate Template(int id, params (int stringIdx, int fret)[] frets)
    {
        var template = new ChordTemplate { Id = id, Name = "Shape" };
        foreach (var (stringIdx, fret) in frets)
        {
            template.Frets[stringIdx] = fret;
        }

        return template;
    }

    private static Chord Chord(int templateId, params (int stringIdx, int fret)[] notes)
    {
        var chord = new Chord { TemplateId = templateId };
        foreach (var (stringIdx, fret) in notes)
        {
            chord.ChordNotes.Add(new ChordNote { StringIndex = stringIdx, Fret = fret });
        }

        return chord;
    }
}
