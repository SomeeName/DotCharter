using DotCharter.Services.Editing.ChordShapes;
using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.Tests.UI.ViewModels.Panels;

/// <summary>
///     "Name only the first chord of a hand shape" points a repeat at an unnamed twin of the named grip.
///     A fingering or fret edit to either one of the pair reaches the other in the same undo step, so the
///     two never drift apart.
/// </summary>
public sealed class ChordTemplateTwinEditTests
{
    private readonly HistoryService _history = new(new FakeAppLogger());
    private readonly ProjectState _state = new();
    private readonly Arrangement _arrangement = new() { Name = "Lead", ArrangementType = ArrangementType.Lead };
    private readonly ChordTemplateEditPlanner _planner;
    private readonly ChartEditorViewModel _editor;
    private readonly ChordTemplate _named = Grip(0, "A5");
    private readonly ChordTemplate _twin = Grip(1, string.Empty);
    private readonly Chord _first = Strum(0, 0);
    private readonly Chord _repeat = Strum(1, 1);

    public ChordTemplateTwinEditTests()
    {
        var logger = new FakeAppLogger();
        var selection = new SelectionService();
        var project = new ChartProject();
        _twin.Unnamed = true;
        _arrangement.ChordTemplates.Add(_named);
        _arrangement.ChordTemplates.Add(_twin);
        _arrangement.Levels.Add(new ChartLevel { Chords = [_first, _repeat] });
        project.Arrangements.Add(_arrangement);
        _state.ActiveProject = project;
        _state.ActiveArrangement = _arrangement;
        _state.ActiveLevel = _arrangement.Levels[0];

        var config = new FakeConfigurationService();
        var transport = new TransportViewModel(new FakeAudioPlaybackService(), _state, config, FakeLocalization.KeysOnly());
        var editor = _editor = new ChartEditorViewModel(_state, transport, _history, selection, new TempoService(logger),
            new TrackContextRegistry(_state, selection), FakeLocalization.KeysOnly());
        _planner = new ChordTemplateEditPlanner(editor, _history, new FingeringGenerationService(), config, logger);
    }

    [Fact]
    public void AFingeringEditOnTheNamedGrip_ReachesItsUnnamedTwin_InOneUndoStep()
    {
        Refinger(_named, _first);

        Assert.Equal(Fingers(3), _named.Fingers);
        Assert.Equal(Fingers(3), _twin.Fingers);
        Assert.Equal(string.Empty, _twin.Name);
        Assert.True(_twin.Unnamed);
        Assert.Equal("A5", _named.Name);

        _history.Undo();
        Assert.False(_history.CanUndo);
        Assert.Equal(Fingers(1), _named.Fingers);
        Assert.Equal(Fingers(1), _twin.Fingers);
    }

    [Fact]
    public void AFingeringEditOnTheUnnamedTwin_ReachesTheNamedGrip()
    {
        Refinger(_twin, _repeat);

        Assert.Equal(Fingers(3), _twin.Fingers);
        Assert.Equal(Fingers(3), _named.Fingers);
        Assert.Equal("A5", _named.Name);
    }

    [Fact]
    public void AFretEditOnTheNamedGrip_ReshapesTheTwinAndItsChords()
    {
        var edited = _named.Clone();
        edited.Frets[2] = 9;
        edited.Fingers[2] = 4;
        Apply(_named, edited, _first);

        Assert.Equal(9, _twin.Frets[2]);
        Assert.Equal(4, _twin.Fingers[2]);
        Assert.Contains(_repeat.ChordNotes, note => note is { StringIndex: 2, Fret: 9 });

        _history.Undo();
        Assert.Equal(7, _twin.Frets[2]);
        Assert.Contains(_repeat.ChordNotes, note => note is { StringIndex: 2, Fret: 7 });
    }

    [Fact]
    public void SuggestFingeringOnTheNamedGrip_ReachesItsTwin_InOneUndoStep()
    {
        foreach (var grip in new[] { _named, _twin })
        {
            grip.Pinned = false;
            grip.Fingers = new Dictionary<int, int> { [1] = 2, [2] = 4 };
        }

        _twin.Pinned = true;
        var plan = ChordShapeEdits.PlanSuggestFingers(_arrangement, _named, _editor);
        _history.Do(plan.Action!);

        Assert.NotEqual(2, _named.Fingers[1]);
        Assert.Equal(_named.Fingers, _twin.Fingers);
        _history.Undo();
        Assert.Equal(2, _twin.Fingers[1]);
    }

    [Fact]
    public void ANamedPairOrADifferentFingering_IsNotATwin()
    {
        var otherName = Grip(2, "E/A");
        var otherFingers = Grip(3, string.Empty);
        otherFingers.Fingers[2] = 4;

        Assert.True(ChordTemplateTwins.AreTwins(_named, _twin));
        Assert.True(ChordTemplateTwins.AreTwins(_twin, _named));
        Assert.False(ChordTemplateTwins.AreTwins(_named, otherName));
        Assert.False(ChordTemplateTwins.AreTwins(_named, otherFingers));
        Assert.False(ChordTemplateTwins.AreTwins(_named, _named));
    }

    private void Refinger(ChordTemplate template, Chord sound)
    {
        var edited = template.Clone();
        edited.Fingers[1] = 3;
        Apply(template, edited, sound);
    }

    private void Apply(ChordTemplate template, ChordTemplate edited, Chord sound)
    {
        var sounds = new ChordApplySounds([sound], []);
        var scope = _planner.ResolveReach(template, sounds, sounds)!;
        Assert.NotNull(_planner.ApplyEdit(template, edited, scope, ChordApplyRung.EveryUse));
    }

    private static Dictionary<int, int> Fingers(int first) => new() { [1] = first, [2] = 3 };

    private static ChordTemplate Grip(int id, string name) => new()
    {
        Id = id,
        Name = name,
        Pinned = true,
        Frets = new Dictionary<int, int> { [1] = 5, [2] = 7 },
        Fingers = new Dictionary<int, int> { [1] = 1, [2] = 3 }
    };

    private static Chord Strum(int templateId, int beat) => new()
    {
        TemplateId = templateId,
        Position = new FractionalPosition(beat),
        ChordNotes = [new ChordNote { StringIndex = 1, Fret = 5 }, new ChordNote { StringIndex = 2, Fret = 7 }]
    };
}
