using DotCharter.Services.Editing;
using DotCharter.Services.Editing.ChordShapes;
using DotCharter.Services.Editing.Collisions;
using DotCharter.Services.Editing.Highway;
using DotCharter.Services.Editing.Pen;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Targets;

namespace DotCharter.Tests.Services.Editing.ChordShapes;

/// <summary>
///     The chord-shape palette's driver (#477) over a real chart: the pen position, the selection
///     winning over the pen, one undo node per gesture, the placed chords selected, the status
///     line, and the recent shapes.
/// </summary>
public sealed class ChordShapePlacementServiceTests
{
    private const int Divisor = 4;

    private readonly ChordShapesFixture _f = new();
    private readonly ProjectState _state = new();
    private readonly SelectionService _selection = new();
    private readonly HistoryService _history = new(new FakeAppLogger());
    private readonly ChordShapePlacementService _service;

    public ChordShapePlacementServiceTests()
    {
        var project = new ChartProject();
        for (var i = 0; i < 32; i++) project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120 });
        project.Arrangements.Add(_f.Arrangement);
        _state.ActiveProject = project;
        _state.ActiveArrangement = _f.Arrangement;
        _state.ActiveLevel = _f.Level1;
        _state.TotalAudioLengthMs = 16000;
        _service = Build(_state, _selection, _history);
    }

    /// <summary>The service over real pen, highway and scope services — shared with the screen's tests.</summary>
    internal static ChordShapePlacementService Build(ProjectState state, ISelectionService selection, IHistoryService history)
    {
        var logger = new FakeAppLogger();
        var config = new FakeConfigurationService();
        var collisions = new StrictCollisionService();
        var scope = new EditingScope(state);
        var targets = new EditTargetService(selection, scope, config);
        var highway = new HighwayEditingService(state, selection, history, collisions, config, scope, targets, logger);
        var pen = new ChartPenService(state, scope, history, selection, collisions, new TempoService(logger), highway,
            targets, config, logger);
        return new ChordShapePlacementService(state, scope, history, selection, collisions, highway, pen,
            FakeLocalization.English(), logger);
    }

    private void SeekToBeat(int beat) => _state.CurrentTimeMs = new FractionalPosition(beat).GetPositionMs(_state.Beats);

    private Chord? ChordAt(int beat) => _f.Level1.Chords.SingleOrDefault(c => c.Position == new FractionalPosition(beat));

    [Fact]
    public void WithNothingSelected_TheShapeLandsAtThePen_LeavesTheSelectionEmpty_AndIsSaid()
    {
        SeekToBeat(20);

        var result = _service.PlaceShape(_f.C, Divisor);

        Assert.True(result.Succeeded);
        var chord = ChordAt(20)!;
        Assert.Equal(_f.C.Id, chord.TemplateId);
        Assert.Equal("Placed C at the playhead.", result.Message);
        Assert.Equal(result.Message, _state.MainDisplayMessage);
        Assert.Single(_history.GetUndoSteps());

        // Not selected: the selection wins over the pen, so the next click places a second chord.
        Assert.Empty(_selection.SelectedChords);
        SeekToBeat(22);
        _service.PlaceShape(_f.G, Divisor);
        Assert.Equal(_f.C.Id, chord.TemplateId);
        Assert.Equal(_f.G.Id, ChordAt(22)!.TemplateId);
    }

    [Fact]
    public void WithSoundsSelected_EverySelectedSoundIsReshaped_AsOneUndoStep()
    {
        SeekToBeat(20);
        var first = ChordAt(0)!;
        var second = ChordAt(10)!;
        var note = new GuitarNote { Position = new FractionalPosition(16), StringIndex = 3, Fret = 5 };
        _f.Level1.Notes.Add(note);
        _selection.SelectedChords.Add(first);
        _selection.SelectedChords.Add(second);
        _selection.SelectedNotes.Add(note);

        var result = _service.PlaceShape(_f.G, Divisor);

        Assert.Equal(3, result.Placed);
        Assert.Equal("Reshaped 3 selected sounds to G.", result.Message);
        Assert.All(new[] { first, second, ChordAt(16)! }, c => Assert.Equal(_f.G.Id, c.TemplateId));
        Assert.Null(ChordAt(20));
        Assert.Equal(3, _selection.SelectedChords.Count);
        Assert.Single(_history.GetUndoSteps());

        _history.Undo();
        Assert.Equal(_f.Am.Id, first.TemplateId);
        Assert.Equal(_f.C.Id, second.TemplateId);
        Assert.Contains(note, _f.Level1.Notes);
    }

    [Fact]
    public void ARefusal_IsSaid_AndRecordsNothing()
    {
        _f.Arrangement.Capo = 2;
        SeekToBeat(20);

        var result = _service.PlaceShape(_f.C, Divisor);

        Assert.Equal(ChordShapePlacementRefusal.BelowCapo, result.Refusal);
        Assert.Equal("Not placed: this shape frets a string behind the capo.", result.Message);
        Assert.Empty(_history.GetUndoSteps());
        Assert.Empty(_service.RecentShapes(_f.Arrangement));
    }

    [Fact]
    public void RepeatPreviousChord_PlacesTheEarlierShapeAtThePen_EvenWithASelection()
    {
        SeekToBeat(20);
        _selection.SelectedChords.Add(ChordAt(0)!);

        var result = _service.RepeatPreviousChord(Divisor);

        Assert.True(result.Succeeded);
        Assert.Equal(_f.Open.Id, ChordAt(20)!.TemplateId);
        Assert.Equal(_f.Am.Id, ChordAt(0)!.TemplateId);
        Assert.Equal("Repeated E5 open at the playhead.", result.Message);

        // Pressed again further on, it repeats the chord it just placed.
        SeekToBeat(24);
        _service.RepeatPreviousChord(Divisor);
        Assert.Equal(_f.Open.Id, ChordAt(24)!.TemplateId);
        Assert.Equal(2, _history.GetUndoSteps().Count);
    }

    [Fact]
    public void RecentShapes_AreNewestFirst_Distinct_AndForgetAnUndoneMint()
    {
        SeekToBeat(20);
        _service.PlaceShape(_f.C, Divisor);
        SeekToBeat(22);
        _service.PlaceShape(_f.G, Divisor);
        SeekToBeat(24);
        _service.PlaceShape(_f.C, Divisor);

        Assert.Equal([_f.C, _f.G], _service.RecentShapes(_f.Arrangement));

        _f.Arrangement.ChordTemplates.Remove(_f.G);
        Assert.Equal([_f.C], _service.RecentShapes(_f.Arrangement));
    }

    [Fact]
    public void RecentShapes_KeepAtMostTheStripsCapacity()
    {
        for (var i = 0; i < 10; i++)
        {
            var template = new ChordTemplate { Id = 100 + i, Name = $"S{i}", Frets = new() { [0] = 3 + i, [1] = 5 + i } };
            _f.Arrangement.ChordTemplates.Add(template);
            SeekToBeat(16 + i);
            Assert.True(_service.PlaceShape(template, Divisor).Succeeded);
        }

        var recent = _service.RecentShapes(_f.Arrangement);
        Assert.Equal(ChordShapePlacementService.RecentCapacity, recent.Count);
        Assert.Equal("S9", recent[0].Name);
    }
}
