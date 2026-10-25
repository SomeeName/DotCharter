using DotCharter.Services.Editing.Fingering;
using DotCharter.UI.Input;
using DotCharter.UI.Input.Actions;

namespace DotCharter.Tests.UI.Input;

/// <summary>
///     A wheel resize that moves a bar onto the template of the chord it now covers leaves the bar's
///     private template with no user. That template leaves in the same undo node as the resize, and
///     one undo puts the span, the bar's template id and the template back.
/// </summary>
public sealed class StrandedShapeResizeTests
{
    private readonly HistoryService _history = new(new FakeAppLogger());
    private readonly Arrangement _arrangement = new() { Name = "Lead", ArrangementType = ArrangementType.Lead };

    private ChartLevel Level => _arrangement.Levels[0];

    public StrandedShapeResizeTests() => _arrangement.Levels.Add(new ChartLevel());

    [Fact]
    public void ABarThatTakesTheChordsTemplate_TakesItsPrivateTemplateOutWithIt()
    {
        var (chordGrip, own, bar) = BarOverAChordAfterGrowing();

        Assert.True(_history.Do(Resize(bar, 8)));

        Assert.Equal(new FractionalPosition(8), bar.EndPosition);
        Assert.Same(chordGrip, Assert.Single(_arrangement.ChordTemplates));
        Assert.Equal((0, 0), (chordGrip.Id, bar.TemplateId));
        Assert.DoesNotContain(own, _arrangement.ChordTemplates);
        Assert.Single(_history.GetUndoSteps());
    }

    [Fact]
    public void Undo_PutsTheSpanTheBarsTemplateAndTheTemplateBack_AndRedoRepeatsIt()
    {
        var (chordGrip, own, bar) = BarOverAChordAfterGrowing();
        var chord = Level.Chords.Single();
        _history.Do(Resize(bar, 8));

        _history.Undo();

        Assert.Equal(new FractionalPosition(4), bar.EndPosition);
        Assert.Equal([chordGrip, own], _arrangement.ChordTemplates);
        Assert.Equal([0, 1], _arrangement.ChordTemplates.Select(t => t.Id));
        Assert.Equal((1, 0), (bar.TemplateId, chord.TemplateId));

        _history.Redo();

        Assert.Same(chordGrip, Assert.Single(_arrangement.ChordTemplates));
        Assert.Equal(0, bar.TemplateId);
    }

    [Fact]
    public void ABarThatRewritesItsOwnGrip_KeepsItsTemplate()
    {
        var chordGrip = NewTemplate(0, (0, 3), (1, 5));
        var own = NewTemplate(1, (0, 3));
        AddNote(0, 0, 3);
        AddNote(2, 2, 7);
        AddChord(6, 0, (0, 3), (1, 5));
        var bar = AddBar(0, 4, 1);
        _arrangement.ChordTemplates.Add(chordGrip);
        _arrangement.ChordTemplates.Add(own);

        _history.Do(Resize(bar, 8));

        Assert.Equal(2, _arrangement.ChordTemplates.Count);
        Assert.Equal(1, bar.TemplateId);
    }

    [Fact]
    public void ABarOnASharedTemplate_LeavesItsTemplateInPlace()
    {
        var chordGrip = NewTemplate(0, (0, 3), (1, 5));
        AddChord(6, 0, (0, 3), (1, 5));
        AddChord(10, 0, (0, 3), (1, 5));
        AddNote(0, 0, 3);
        var bar = AddBar(0, 4, 0);
        _arrangement.ChordTemplates.Add(chordGrip);

        _history.Do(Resize(bar, 8));

        Assert.Same(chordGrip, Assert.Single(_arrangement.ChordTemplates));
    }

    private (ChordTemplate ChordGrip, ChordTemplate Own, HandShape Bar) BarOverAChordAfterGrowing()
    {
        var chordGrip = NewTemplate(0, (0, 3), (1, 5));
        var own = NewTemplate(1, (0, 3));
        _arrangement.ChordTemplates.Add(chordGrip);
        _arrangement.ChordTemplates.Add(own);
        AddNote(0, 0, 3);
        AddChord(6, 0, (0, 3), (1, 5));
        return (chordGrip, own, AddBar(0, 4, 1));
    }

    /// <summary>The action one wheel tick commits for <paramref name="bar" /> grown to end at beat <paramref name="end" />.</summary>
    private IChartAction Resize(HandShape bar, int end)
    {
        var old = bar.Clone();
        var next = bar.Clone();
        next.EndPosition = new FractionalPosition(end);
        List<ResizeHandShapesAction.GripRewrite> grips = [];
        List<ChordTemplate> minted = [];
        TimelineWheelAdjuster.RebuildGrips(new FingeringGenerationService(), _arrangement, true,
            Level.Notes, Level.Chords, Level.HandShapes, [bar], [next], grips, minted);
        return TimelineWheelAdjuster.BuildResize(Level.HandShapes, _arrangement, [bar], [old], [next], grips, minted,
            null);
    }

    private static ChordTemplate NewTemplate(int id, params (int stringIdx, int fret)[] frets)
    {
        var t = new ChordTemplate { Id = id };
        foreach (var (stringIdx, fret) in frets) t.Frets[stringIdx] = fret;
        return t;
    }

    private HandShape AddBar(int start, int end, int templateId)
    {
        var shape = new HandShape
        {
            Position = new FractionalPosition(start),
            EndPosition = new FractionalPosition(end),
            TemplateId = templateId
        };
        Level.HandShapes.Add(shape);
        return shape;
    }

    private void AddNote(int beat, int stringIndex, int fret) =>
        Level.Notes.Add(new GuitarNote { Position = new FractionalPosition(beat), StringIndex = stringIndex, Fret = fret });

    private void AddChord(int beat, int templateId, params (int stringIndex, int fret)[] frets)
    {
        var chord = new Chord { Position = new FractionalPosition(beat), TemplateId = templateId };
        foreach (var (stringIndex, fret) in frets)
            chord.ChordNotes.Add(new ChordNote { StringIndex = stringIndex, Fret = fret });
        Level.Chords.Add(chord);
    }
}
