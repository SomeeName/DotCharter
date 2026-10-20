using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.Tests.UI.ViewModels.Panels;

/// <summary>
///     The WORDS of the "Apply to" ladder (#484), read off a real
///     <see cref="ChordApplyScope" />: each rung's label and live count, the rung
///     an edit falls through to, and the status line after a commit.
/// </summary>
public sealed class ChordApplyLadderTests
{
    private readonly Arrangement _arr = new() { Name = "Lead", ArrangementType = ArrangementType.Lead };
    private readonly ChordTemplate _a5;
    private readonly List<Chord> _chords = [];

    public ChordApplyLadderTests()
    {
        _arr.Levels.Add(new ChartLevel());
        _arr.Levels.Add(new ChartLevel());
        _a5 = Template(0, 5);
        for (var level = 0; level < 2; level++)
        {
            for (var i = 0; i < 3; i++)
            {
                var chord = new Chord { TemplateId = 0, Position = new FractionalPosition(i * 4) };
                foreach (var (s, f) in _a5.Frets) chord.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = f });
                _arr.Levels[level].Chords.Add(chord);
                _chords.Add(chord);
            }
        }

        _arr.Levels[0].HandShapes.Add(new HandShape { TemplateId = 0, Position = new FractionalPosition(0), EndPosition = new FractionalPosition(2) });
    }

    [Fact]
    public void EveryRung_CarriesItsLiveCount()
    {
        Template(1, 7);
        var ladder = Ladder(This(_chords[0], _arr.Levels[0].HandShapes[0]),
            new ChordApplySounds([_chords[0], _chords[1]], [_arr.Levels[0].HandShapes[0]]), ChordApplyRung.Selected);

        Assert.Equal("this chord + its hand shape", ladder.ThisChord.Label);
        Assert.Equal("selected (3)", ladder.Selected.Label);
        Assert.Equal("2 chords · 1 hand shape", ladder.Selected.Detail);
        Assert.Equal("every use of this shape", ladder.EveryUse.Label);
        Assert.Equal("6 chords · 1 hand shape · all levels", ladder.EveryUse.Detail);
        Assert.Equal("+ the same grip elsewhere", ladder.SameGrip.Label);
        Assert.Equal("1 more shape · 0 chords", ladder.SameGrip.Detail);
        Assert.Equal("every use of this shape, 6 chords · 1 hand shape · all levels", ladder.EveryUse.AccessibleName);
        Assert.True(ladder.Selected.IsEffective);
        Assert.All(ladder.Rows, r => Assert.True(r.IsAvailable));
    }

    [Fact]
    public void NothingSelected_DisablesTheSelectionRungs_AndLightsEveryUse()
    {
        var ladder = Ladder(ChordApplySounds.None, ChordApplySounds.None, ChordApplyRung.ThisChord);

        Assert.False(ladder.ThisChord.IsAvailable);
        Assert.False(ladder.Selected.IsAvailable);
        Assert.Equal("Select a chord to edit it alone.", ladder.ThisChord.Tip);
        Assert.False(ladder.SameGrip.IsAvailable);
        Assert.Equal("No other shape in this arrangement has this grip.", ladder.SameGrip.Tip);
        Assert.Equal(ChordApplyRung.EveryUse, ladder.Effective);
        Assert.True(ladder.EveryUse.IsEffective);
        Assert.False(ladder.ThisChord.IsEffective);
    }

    [Fact]
    public void TheStatusLine_SaysWhatChanged()
    {
        var moved = Template(1, 7);
        var scope = Scope(This(_chords[0]), new ChordApplySounds([_chords[0]], []));
        var ladder = new ChordApplyLadder(FakeLocalization.English());

        var fingered = _a5.Clone();
        foreach (var (s, f) in new Dictionary<int, int> { [0] = 1, [1] = 3, [2] = 4 }) fingered.Fingers[s] = f;

        Assert.Equal("Fingering applied to 6 chords · 1 hand shape on 2 shapes",
            ladder.Describe(scope.Resolve(ChordApplyRung.SameGrip, ChordTemplateEditKind.Fingering, fingered)));
        Assert.Equal("Fingering applied to 6 chords · 1 hand shape on 1 shape (The same grip elsewhere keeps its " +
                     "fingering until every fretted string has a finger.)",
            ladder.Describe(scope.Resolve(ChordApplyRung.SameGrip, ChordTemplateEditKind.Fingering)));
        Assert.Equal("Name applied to 1 chord on 1 shape",
            ladder.Describe(scope.Resolve(ChordApplyRung.ThisChord, ChordTemplateEditKind.Name)));
        Assert.Equal("Arpeggio applied to 6 chords · 1 hand shape on 1 shape (Arpeggio never reaches other grips.)",
            ladder.Describe(scope.Resolve(ChordApplyRung.SameGrip, ChordTemplateEditKind.Arpeggio)));
        Assert.NotNull(moved);
    }

    private ChordApplyLadder Ladder(ChordApplySounds thisSounds, ChordApplySounds selected, ChordApplyRung chosen)
    {
        var ladder = new ChordApplyLadder(FakeLocalization.English());
        ladder.Update(Scope(thisSounds, selected), chosen);
        return ladder;
    }

    private ChordApplyScope Scope(ChordApplySounds thisSounds, ChordApplySounds selected) =>
        ChordApplyScope.Prepare(_arr, _a5, thisSounds, selected, _arr.Levels[0].Chords);

    private static ChordApplySounds This(Chord chord, HandShape? bar = null) => new([chord], bar == null ? [] : [bar]);

    private ChordTemplate Template(int id, int root)
    {
        var template = new ChordTemplate { Id = id, Name = $"T{id}" };
        template.Frets[0] = root;
        template.Frets[1] = root + 2;
        template.Frets[2] = root + 2;
        _arr.ChordTemplates.Add(template);
        return template;
    }
}
