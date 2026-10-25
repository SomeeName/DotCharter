using DotCharter.Services.Editing;
using DotCharter.Services.Editing.ChordShapes;
using DotCharter.Tests.Presentation.Dialogs;

namespace DotCharter.Tests.Services.Editing.ChordShapes;

/// <summary>
///     An edit that leaves a chord template with no user removes it in the SAME undo node: the rule
///     (<see cref="StrandedShapes" />), what counts as a user, the id renumbering that follows, and
///     undo and redo of the whole node. The editing paths that apply it have their own classes.
/// </summary>
public sealed class StrandedShapesTests
{
    private readonly HistoryService _history = new(new FakeAppLogger());
    private readonly IChartEditorSurface _editor = RecordingProxy.Create<IChartEditorSurface>().Proxy;
    private readonly Arrangement _arrangement = new() { Name = "Lead", ArrangementType = ArrangementType.Lead };
    private readonly ChartLevel _level = new();

    public StrandedShapesTests() => _arrangement.Levels.Add(_level);

    [Fact]
    public void ATemplateWhoseLastChordMovesAway_LeavesInTheSameNode()
    {
        var (a, b, spare) = ThreeShapes();
        var chord = AddChord(a.Id);

        Do(Switch([chord], b));

        Assert.Equal([b, spare], _arrangement.ChordTemplates);
        Assert.Single(_history.GetUndoSteps());
    }

    [Fact]
    public void ATemplateAHandShapeStillUses_Stays()
    {
        var (a, b, _) = ThreeShapes();
        var chord = AddChord(a.Id);
        AddBar(a.Id);

        Do(Switch([chord], b));

        Assert.Contains(a, _arrangement.ChordTemplates);
        Assert.Equal(3, _arrangement.ChordTemplates.Count);
    }

    [Fact]
    public void ATemplateANotationVoiceChordStillUses_Stays()
    {
        var (a, b, _) = ThreeShapes();
        var chord = AddChord(a.Id);
        var voice = new NotationVoice { VoiceIndex = 1 };
        voice.Chords.Add(new Chord { TemplateId = a.Id, Position = new FractionalPosition(3) });
        _arrangement.NotationVoices.Add(voice);

        Do(Switch([chord], b));

        Assert.Contains(a, _arrangement.ChordTemplates);
        Assert.Equal(a.Id, voice.Chords[0].TemplateId);
    }

    [Fact]
    public void ATemplateNobodyUsedBeforeTheEdit_Stays()
    {
        // A shape typed in the editor and not placed yet is not the edit's to judge.
        var (a, b, spare) = ThreeShapes();
        var chord = AddChord(a.Id);

        Do(Switch([chord], b));

        Assert.Contains(spare, _arrangement.ChordTemplates);
    }

    [Fact]
    public void TheSurvivorsAreRenumberedToTheirPositions_AndEveryUserFollows()
    {
        var (a, b, spare) = ThreeShapes();
        var moved = AddChord(a.Id);
        var onB = AddChord(b.Id, beat: 4);
        var onSpare = AddChord(spare.Id, beat: 8);
        var bar = AddBar(spare.Id);
        var voice = new NotationVoice { VoiceIndex = 1 };
        var voiceChord = new Chord { TemplateId = spare.Id, Position = new FractionalPosition(2) };
        voice.Chords.Add(voiceChord);
        _arrangement.NotationVoices.Add(voice);

        Do(Switch([moved], b));

        Assert.Equal([0, 1], _arrangement.ChordTemplates.Select(t => t.Id));
        Assert.Equal(0, moved.TemplateId);
        Assert.Equal(0, onB.TemplateId);
        Assert.Equal(1, onSpare.TemplateId);
        Assert.Equal(1, bar.TemplateId);
        Assert.Equal(1, voiceChord.TemplateId);
        Assert.Same(b, _arrangement.ChordTemplates[0]);
        Assert.Same(spare, _arrangement.ChordTemplates[1]);
    }

    [Fact]
    public void Undo_RestoresTheTemplateTheIdsAndEveryUser_AndRedoRemovesItAgain()
    {
        var (a, b, spare) = ThreeShapes();
        var moved = AddChord(a.Id);
        var onSpare = AddChord(spare.Id, beat: 8);
        var bar = AddBar(spare.Id);
        var voiceChord = new Chord { TemplateId = spare.Id, Position = new FractionalPosition(2) };
        _arrangement.NotationVoices.Add(new NotationVoice { Chords = { voiceChord } });
        Do(Switch([moved], b));

        _history.Undo();

        Assert.Equal([a, b, spare], _arrangement.ChordTemplates);
        Assert.Equal([0, 1, 2], _arrangement.ChordTemplates.Select(t => t.Id));
        Assert.Equal((a.Id, onSpare.TemplateId, bar.TemplateId, voiceChord.TemplateId), (0, 2, 2, 2));
        Assert.Equal(0, moved.TemplateId);
        Assert.False(_history.CanUndo);

        _history.Redo();

        Assert.Equal([b, spare], _arrangement.ChordTemplates);
        Assert.Equal((0, 1, 1, 1), (moved.TemplateId, onSpare.TemplateId, bar.TemplateId, voiceChord.TemplateId));
    }

    [Fact]
    public void ChordsRemovedFromTheChart_StrandTheirTemplate()
    {
        var (a, _, _) = ThreeShapes();
        var chord = AddChord(a.Id);

        Do(new RemoveChordsAction(_level.Chords, [chord]));

        Assert.DoesNotContain(a, _arrangement.ChordTemplates);
        _history.Undo();
        Assert.Contains(a, _arrangement.ChordTemplates);
        Assert.Single(_level.Chords);
    }

    [Fact]
    public void AChordMovedByARemoveAndAnAdd_KeepsItsTemplate()
    {
        var (a, _, _) = ThreeShapes();
        var chord = AddChord(a.Id);
        var moved = chord.Clone();
        moved.Position = new FractionalPosition(6);

        Do(new CompositeAction([new RemoveChordsAction(_level.Chords, [chord]), new AddChordsAction(_level.Chords, [moved])]));

        Assert.Contains(a, _arrangement.ChordTemplates);
    }

    [Fact]
    public void AMutateThatKeepsTheTemplateId_StrandsNothing()
    {
        var (a, _, _) = ThreeShapes();
        var chord = AddChord(a.Id);
        var after = chord.Clone();
        after.Accent = true;

        Do(new MutateChordsAction(_level.Chords, [chord], [chord.Clone()], [after]));

        Assert.Equal(3, _arrangement.ChordTemplates.Count);
    }

    [Fact]
    public void IdsThatAreNotPositions_AreLeftAloneWhenATemplateLeaves()
    {
        var (a, b, spare) = ThreeShapes();
        a.Id = 10;
        b.Id = 20;
        spare.Id = 30;
        var moved = AddChord(10);
        var onSpare = AddChord(30, beat: 8);

        Do(Switch([moved], b));

        Assert.Equal([20, 30], _arrangement.ChordTemplates.Select(t => t.Id));
        Assert.Equal((20, 30), (moved.TemplateId, onSpare.TemplateId));
    }

    [Fact]
    public void ATemplateThatIsInUseByTheTimeTheRemovalRuns_Stays()
    {
        var (a, b, _) = ThreeShapes();
        var chord = AddChord(a.Id);
        var action = StrandedShapes.Wrap(_arrangement, Switch([chord], b));
        var latecomer = AddChord(a.Id, beat: 12);

        _history.Do(action);

        Assert.Contains(a, _arrangement.ChordTemplates);
        Assert.Equal(a.Id, latecomer.TemplateId);
    }

    [Fact]
    public void AnEditThatStrandsNothing_IsReturnedAsIs()
    {
        var (a, b, _) = ThreeShapes();
        var chord = AddChord(a.Id);
        AddChord(a.Id, beat: 4);
        var edit = Switch([chord], b);

        Assert.Same(edit, StrandedShapes.Wrap(_arrangement, edit));
        Assert.Same(edit, StrandedShapes.Wrap(null, edit));
    }

    [Fact]
    public void ACompositeKeepsItsLabelAndItsPolicyWhenTheRemovalJoinsIt()
    {
        var (a, b, _) = ThreeShapes();
        var chord = AddChord(a.Id);
        var composite = new CompositeAction([Switch([chord], b)])
        {
            LabelKey = "History_Delete", LinkPolicyOverride = LinkPolicy.UnlinkOnDivergence
        };

        var wrapped = Assert.IsType<CompositeAction>(StrandedShapes.Wrap(_arrangement, composite));

        Assert.Equal("History_Delete", wrapped.LabelKey);
        Assert.Equal(LinkPolicy.UnlinkOnDivergence, wrapped.LinkPolicy);
        Assert.Equal(2, wrapped.Actions.Count);
    }

    [Fact]
    public void ABatchThatRepointsOneChordTwice_CountsItOnce()
    {
        var (a, b, spare) = ThreeShapes();
        var chord = AddChord(a.Id);
        var viaB = Switch([chord], b);
        var viaSpare = Switch([chord], spare);

        var delta = new TemplateUseDelta();
        viaB.ReportTemplateUses(delta);
        viaSpare.ReportTemplateUses(delta);

        var change = Assert.Single(delta.Changes);
        Assert.Equal((a.Id, spare.Id), (change.Before, change.After));
        Assert.Equal([a], StrandedShapes.Find(_arrangement, delta));
    }

    private void Do(IChartAction action) => Assert.True(_history.Do(StrandedShapes.Wrap(_arrangement, action)));

    private (ChordTemplate A, ChordTemplate B, ChordTemplate Spare) ThreeShapes()
    {
        var a = Template(0, 3);
        var b = Template(1, 5);
        var spare = Template(2, 7);
        foreach (var template in new[] { a, b, spare }) _arrangement.ChordTemplates.Add(template);
        return (a, b, spare);
    }

    private static ChordTemplate Template(int id, int fret) =>
        new() { Id = id, Name = $"Shape {id}", Frets = new Dictionary<int, int> { [0] = fret, [1] = fret + 2 } };

    private Chord AddChord(int templateId, int beat = 0)
    {
        var chord = new Chord { TemplateId = templateId, Position = new FractionalPosition(beat) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 3 });
        _level.Chords.Add(chord);
        return chord;
    }

    private HandShape AddBar(int templateId)
    {
        var bar = new HandShape
        {
            TemplateId = templateId, Position = new FractionalPosition(10), EndPosition = new FractionalPosition(12)
        };
        _level.HandShapes.Add(bar);
        return bar;
    }

    private SwitchTemplateAction Switch(List<Chord> chords, ChordTemplate target)
    {
        var moved = chords.Select(c =>
        {
            var next = c.Clone();
            next.TemplateId = target.Id;
            return next;
        }).ToList();
        return new SwitchTemplateAction(chords, moved, [], [], _editor);
    }
}
