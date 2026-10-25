using DotCharter.Services.Editing;
using DotCharter.Services.Editing.ChordShapes;
using DotCharter.Tests.Presentation.Dialogs;

namespace DotCharter.Tests.Services.Editing.ChordShapes;

/// <summary>
///     Merging chord shapes resolves duplicates: three identical templates used by different
///     chords, hand shapes and a notation voice become ONE template, every use repointed, nothing
///     left behind as an unused (and still "duplicate") entry.
/// </summary>
public sealed class ChordShapeMergeDuplicatesTests
{
    private static readonly Dictionary<int, int> Frets = new() { [1] = 0, [2] = 2, [3] = 2, [4] = 1, [5] = 0 };
    private static readonly Dictionary<int, int> Fingers = new() { [2] = 2, [3] = 3, [4] = 1 };

    private readonly HistoryService _history = new(new FakeAppLogger());
    private readonly IChartEditorSurface _editor = RecordingProxy.Create<IChartEditorSurface>().Proxy;
    private readonly Arrangement _arrangement = new() { Name = "Lead", StringCount = 6 };
    private readonly ChordTemplate _first;
    private readonly ChordTemplate _second;
    private readonly ChordTemplate _third;
    private readonly ChordTemplate _other;

    public ChordShapeMergeDuplicatesTests()
    {
        // The unrelated shape sits BETWEEN the duplicates, so a merge has ids to renumber.
        _first = Template(0, "Am");
        _other = new ChordTemplate { Id = 1, Name = "E5", Frets = { [0] = 0, [1] = 2 }, Fingers = { [1] = 1 } };
        _second = Template(2, "Am");
        _third = Template(3, "Am");
        foreach (var t in new[] { _first, _other, _second, _third }) _arrangement.ChordTemplates.Add(t);

        var level0 = new ChartLevel { DifficultyIndex = 0 };
        var level1 = new ChartLevel { DifficultyIndex = 1 };
        _arrangement.Levels.Add(level0);
        _arrangement.Levels.Add(level1);
        level0.Chords.Add(ChordShapesFixture.ChordOn(_first, 0));
        level0.Chords.Add(ChordShapesFixture.ChordOn(_second, 2));
        level1.Chords.Add(ChordShapesFixture.ChordOn(_second, 4));
        level1.Chords.Add(ChordShapesFixture.ChordOn(_third, 6));
        level1.Chords.Add(ChordShapesFixture.ChordOn(_third, 8));
        level1.Chords.Add(ChordShapesFixture.ChordOn(_other, 10));
        level0.HandShapes.Add(HandShapeOn(_second, 2));
        level1.HandShapes.Add(HandShapeOn(_third, 6));
        var voice = new NotationVoice();
        voice.Chords.Add(ChordShapesFixture.ChordOn(_third, 12));
        _arrangement.NotationVoices.Add(voice);
    }

    private IEnumerable<Chord> AllChords =>
        _arrangement.Levels.SelectMany(l => l.Chords).Concat(_arrangement.NotationVoices.SelectMany(v => v.Chords));

    private IEnumerable<HandShape> AllShapes => _arrangement.Levels.SelectMany(l => l.HandShapes);

    [Fact]
    public void MergingTheDuplicatesOneByOne_LeavesOneTemplate_WithEveryUseRepointed()
    {
        var shapeSpans = AllShapes.Select(h => (h.Position, h.EndPosition)).ToList();
        var chordCount = AllChords.Count();

        Merge(_second, _first);
        Merge(_third, _first);

        Assert.Equal(2, _arrangement.ChordTemplates.Count);
        Assert.Same(_first, _arrangement.ChordTemplates[0]);
        Assert.Equal([0, 1], _arrangement.ChordTemplates.Select(t => t.Id));
        Assert.Equal(chordCount, AllChords.Count());
        Assert.Equal(6, AllChords.Count(c => c.TemplateId == _first.Id));
        Assert.Equal(1, AllChords.Count(c => c.TemplateId == _other.Id));
        Assert.All(AllShapes, h => Assert.Equal(_first.Id, h.TemplateId));
        Assert.Equal(shapeSpans, AllShapes.Select(h => (h.Position, h.EndPosition)));
        Assert.Empty(ChordShapeEdits.UnusedTemplates(_arrangement));
        var catalog = ChordShapeCatalog.Build(_arrangement, _arrangement.Levels[1]);
        Assert.DoesNotContain(catalog, e => e.IsDuplicate);
    }

    [Fact]
    public void AMerge_RepointsTheNotationVoiceChordsToo()
    {
        Merge(_third, _first);

        var voiceChord = Assert.Single(_arrangement.NotationVoices[0].Chords);
        Assert.Equal(_first.Id, voiceChord.TemplateId);
        Assert.DoesNotContain(_arrangement.ChordTemplates, t => ReferenceEquals(t, _third));
    }

    [Fact]
    public void AShapeOnlyANotationVoiceUses_CanStillBeMerged()
    {
        // The third shape's only user is the voice chord once the level chords and hand shapes move away.
        foreach (var chord in _arrangement.Levels.SelectMany(l => l.Chords).Where(c => c.TemplateId == _third.Id)) chord.TemplateId = _other.Id;
        foreach (var shape in AllShapes.Where(h => h.TemplateId == _third.Id)) shape.TemplateId = _other.Id;

        Merge(_third, _first);

        Assert.Equal(_first.Id, _arrangement.NotationVoices[0].Chords[0].TemplateId);
        Assert.DoesNotContain(_arrangement.ChordTemplates, t => ReferenceEquals(t, _third));
    }

    [Fact]
    public void AMerge_IsOneUndoNode_ThatRestoresTheTemplateItsIdsAndEveryUse()
    {
        var idsBefore = _arrangement.ChordTemplates.Select(t => t.Id).ToList();
        var chordIds = AllChords.Select(c => c.TemplateId).ToList();
        var shapeIds = AllShapes.Select(h => h.TemplateId).ToList();

        Merge(_second, _first);
        Assert.Single(_history.GetTree(), n => n.ParentId is not null);
        _history.Undo();

        Assert.Equal(4, _arrangement.ChordTemplates.Count);
        Assert.Same(_second, _arrangement.ChordTemplates[2]);
        Assert.Same(_third, _arrangement.ChordTemplates[3]);
        Assert.Equal(idsBefore, _arrangement.ChordTemplates.Select(t => t.Id));
        Assert.Equal(chordIds, AllChords.Select(c => c.TemplateId));
        Assert.Equal(shapeIds, AllShapes.Select(h => h.TemplateId));
        Assert.False(_history.CanUndo);

        _history.Redo();
        Assert.Equal(3, _arrangement.ChordTemplates.Count);
        // The first and third shapes are still duplicates of each other: one merge, one shape.
        Assert.Equal(2, ChordShapeCatalog.Build(_arrangement, null).Count(e => e.IsDuplicate));
    }

    private void Merge(ChordTemplate source, ChordTemplate target)
    {
        var plan = ChordShapeEdits.PlanMerge(_arrangement, source, target, _editor);
        Assert.False(plan.IsRefused, plan.Refusal.ToString());
        Assert.True(_history.Do(plan.Action!));
    }

    private static ChordTemplate Template(int id, string name) =>
        new() { Id = id, Name = name, Frets = new Dictionary<int, int>(Frets), Fingers = new Dictionary<int, int>(Fingers) };

    private static HandShape HandShapeOn(ChordTemplate template, int beat) => new()
    {
        Position = new FractionalPosition(beat),
        EndPosition = new FractionalPosition(beat + 1),
        TemplateId = template.Id
    };
}
