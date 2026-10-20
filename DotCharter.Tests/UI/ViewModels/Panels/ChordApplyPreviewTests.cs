using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.Tests.UI.ViewModels.Panels;

/// <summary>
///     The chord panel's bulk-edit preview (#485), between the resolver and the
///     editor: it publishes <see cref="ChartEditorViewModel.UseHighlight" /> only
///     while the rung reaches MORE than the selection and a panel is on screen,
///     counts the uses on other levels apart, and steps the playhead between the
///     marks. The set itself is <see cref="ChordApplyScope.Preview" />'s —
///     <see cref="ChordApplyPreviewParityTests" /> pins it against the commit.
/// </summary>
public sealed class ChordApplyPreviewTests
{
    private readonly ProjectState _state = new();
    private readonly SelectionService _selection = new();
    private readonly Arrangement _arr = new() { Name = "Lead", ArrangementType = ArrangementType.Lead };
    private readonly ChartEditorViewModel _editor;
    private readonly ChordTemplateEditPlanner _planner;
    private readonly ChordApplyPreview _preview;
    private readonly ChordTemplate _a5;
    private readonly List<Chord> _here = [];
    private readonly object _host = new();

    public ChordApplyPreviewTests()
    {
        var logger = new FakeAppLogger();
        var history = new HistoryService(logger);
        var project = new ChartProject();
        _arr.Levels.Add(new ChartLevel());
        _arr.Levels.Add(new ChartLevel());
        project.Arrangements.Add(_arr);
        _state.ActiveProject = project;
        _state.ActiveArrangement = _arr;
        _state.ActiveLevel = _arr.Levels[0];
        for (var i = 0; i < 64; i++) _state.Beats.Add(new BeatMarker { Time = i * 500.0, Bpm = 120 });

        var config = new FakeConfigurationService();
        var transport = new TransportViewModel(new FakeAudioPlaybackService(), _state, config, FakeLocalization.KeysOnly());
        _editor = new ChartEditorViewModel(_state, transport, history, _selection, new TempoService(logger),
            new TrackContextRegistry(_state, _selection), FakeLocalization.KeysOnly());
        _planner = new ChordTemplateEditPlanner(_editor, history, new FingeringGenerationService(), config, logger);
        _preview = new ChordApplyPreview(_editor, FakeLocalization.KeysOnly());

        _a5 = new ChordTemplate { Id = 0, Name = "A5" };
        _a5.Frets[0] = 5;
        _a5.Frets[1] = 7;
        _arr.ChordTemplates.Add(_a5);
        foreach (var beat in new[] { 4, 12, 20 }) _here.Add(AddChord(0, beat));
        AddChord(1, 4);
        AddChord(1, 8);
    }

    [Fact]
    public void EveryUse_IsMarked_AndTheOtherLevelIsCountedApart()
    {
        _preview.SetHostShown(_host, true);

        Update(ChordApplyRung.EveryUse, [_here[0]]);

        Assert.True(_preview.IsActive);
        Assert.Equal(5, _editor.UseHighlight.Count);
        Assert.Equal(3, _editor.UseHighlight.On(_editor.PlacedChords).Count);
        Assert.Equal("ChordTemplate_Preview_Marked(3) · ChordTemplate_Preview_OtherLevels(2)", _preview.Summary);
        Assert.True(_preview.CanStep);
    }

    [Fact]
    public void ARungThatReachesOnlyTheSelection_MarksNothing()
    {
        _preview.SetHostShown(_host, true);

        Update(ChordApplyRung.Selected, [_here[0], _here[1]]);

        Assert.False(_preview.IsActive);
        Assert.True(_editor.UseHighlight.IsEmpty);
        Assert.Equal(string.Empty, _preview.Summary);
    }

    [Fact]
    public void WhileNoPanelIsOnScreen_NothingIsPublished_AndShowingItPublishes()
    {
        Update(ChordApplyRung.EveryUse, [_here[0]]);

        Assert.True(_editor.UseHighlight.IsEmpty, "a panel nobody can see must not mark the chart");
        Assert.Equal(5, _preview.Resolved.Count);

        _preview.SetHostShown(_host, true);
        Assert.Equal(5, _editor.UseHighlight.Count);

        _preview.SetHostShown(_host, false);
        Assert.True(_editor.UseHighlight.IsEmpty);
    }

    [Fact]
    public void TheMarksTrackTheRung()
    {
        _preview.SetHostShown(_host, true);
        Update(ChordApplyRung.EveryUse, [_here[0]]);

        Update(ChordApplyRung.ThisChord, [_here[0]]);

        Assert.True(_editor.UseHighlight.IsEmpty);
    }

    [Fact]
    public void NextAndPrevious_StepThePlayheadBetweenTheMarksOnScreen()
    {
        _preview.SetHostShown(_host, true);
        Update(ChordApplyRung.EveryUse, [_here[0]]);
        _state.CurrentTimeMs = 0;

        _preview.NextTargetCommand.Execute(null);
        Assert.Equal(2000, _state.CurrentTimeMs, 3);
        _preview.NextTargetCommand.Execute(null);
        Assert.Equal(6000, _state.CurrentTimeMs, 3);
        _preview.PreviousTargetCommand.Execute(null);
        Assert.Equal(2000, _state.CurrentTimeMs, 3);
        _preview.PreviousTargetCommand.Execute(null);
        Assert.Equal(10000, _state.CurrentTimeMs, 3); // wraps to the last
    }

    [Fact]
    public void PublishingTheSameMembersTwice_RaisesOneChange()
    {
        var raised = 0;
        _editor.UseHighlightChanged += (_, _) => raised++;
        _preview.SetHostShown(_host, true);

        Update(ChordApplyRung.EveryUse, [_here[0]]);
        Update(ChordApplyRung.EveryUse, [_here[1]]);

        Assert.Equal(1, raised);
    }

    private void Update(ChordApplyRung rung, List<Chord> selected)
    {
        var scope = _planner.ResolveReach(_a5, new ChordApplySounds([selected[0]], []), new ChordApplySounds(selected, []));
        _preview.Update(scope, rung, new ChordApplySounds(selected, []));
    }

    private Chord AddChord(int level, int beat)
    {
        var chord = new Chord { TemplateId = _a5.Id, Position = new FractionalPosition(beat) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 5 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 7 });
        _arr.Levels[level].Chords.Add(chord);
        return chord;
    }
}
