using DotCharter.Services.Editing;
using DotCharter.Services.Editing.ChordShapes;
using DotCharter.Tests.Presentation.Dialogs;

namespace DotCharter.Tests.Services.Editing.ChordShapes;

/// <summary>
///     The Chord shapes list's template verbs (#486): merge, split and rename, each ONE undo node,
///     none of them reshaping a shared template.
/// </summary>
public sealed class ChordShapeEditsTests
{
    private readonly ChordShapesFixture _f = new();
    private readonly HistoryService _history = new(new FakeAppLogger());
    private readonly IChartEditorSurface _editor = RecordingProxy.Create<IChartEditorSurface>().Proxy;

    private IEnumerable<Chord> AllChords => _f.Arrangement.Levels.SelectMany(l => l.Chords);
    private IEnumerable<HandShape> AllShapes => _f.Arrangement.Levels.SelectMany(l => l.HandShapes);

    [Fact]
    public void Merge_MovesEveryChordAndHandShapeOnEveryLevel_AndLeavesTheSourceUnused()
    {
        var plan = ChordShapeEdits.PlanMerge(_f.Arrangement, _f.AmAlt, _f.Am, _editor);
        Assert.False(plan.IsRefused);
        Assert.Equal((3, 1), (plan.ChordsMoved, plan.HandShapesMoved));
        var before = AllChords.Select(c => (c, c.ChordNotes.Select(n => (n.StringIndex, n.Fret)).ToList())).ToList();

        Assert.True(_history.Do(plan.Action!));

        Assert.DoesNotContain(AllChords, c => c.TemplateId == _f.AmAlt.Id);
        Assert.DoesNotContain(AllShapes, h => h.TemplateId == _f.AmAlt.Id);
        Assert.Equal(7, AllChords.Count(c => c.TemplateId == _f.Am.Id));
        Assert.Equal(2, AllShapes.Count(h => h.TemplateId == _f.Am.Id));
        Assert.Equal(0, ChordShapeCatalog.Build(_f.Arrangement, _f.Level1).Single(e => e.Template == _f.AmAlt).Uses.All);
        // The source stays declared; nothing reshaped a chord or the shared templates.
        Assert.Contains(_f.AmAlt, _f.Arrangement.ChordTemplates);
        Assert.All(before, b => Assert.Equal(b.Item2, b.c.ChordNotes.Select(n => (n.StringIndex, n.Fret)).ToList()));
        Assert.True(_f.Am.HasFrets(ChordShapesFixture.AmFrets) && _f.AmAlt.HasFrets(ChordShapesFixture.AmFrets));
    }

    [Fact]
    public void Merge_IsOneUndoNode_AndOneUndoRestoresEveryUse()
    {
        var chordIds = AllChords.Select(c => c.TemplateId).ToList();
        var shapeIds = AllShapes.Select(h => h.TemplateId).ToList();

        _history.Do(ChordShapeEdits.PlanMerge(_f.Arrangement, _f.AmAlt, _f.Am, _editor).Action!);
        var node = Assert.Single(_history.GetTree(), n => n.ParentId is not null);
        Assert.Contains(ChordShapeEdits.MergeLabelKey, node.Description, StringComparison.Ordinal);
        _history.Undo();

        Assert.Equal(chordIds, AllChords.Select(c => c.TemplateId));
        Assert.Equal(shapeIds, AllShapes.Select(h => h.TemplateId));
        Assert.False(_history.CanUndo);
    }

    [Fact]
    public void Merge_IsRefused_WhenTheFretsDiffer()
    {
        var plan = ChordShapeEdits.PlanMerge(_f.Arrangement, _f.C, _f.Am, _editor);

        Assert.True(plan.IsRefused);
        Assert.Equal(ChordShapeEditRefusal.DifferentFrets, plan.Refusal);
    }

    [Fact]
    public void Merge_IsRefused_AcrossArpeggioFlavours_EvenOnTheSameFrets()
    {
        var plan = ChordShapeEdits.PlanMerge(_f.Arrangement, _f.AmArp, _f.Am, _editor);

        Assert.Equal(ChordShapeEditRefusal.DifferentArpeggio, plan.Refusal);
    }

    [Theory]
    [InlineData("same")]
    [InlineData("twin-id")]
    [InlineData("foreign")]
    [InlineData("unused")]
    public void Merge_IsRefused_ForEveryShapeThatHasNothingToMove(string kind)
    {
        var (source, target, expected) = kind switch
        {
            "same" => (_f.Am, _f.Am, ChordShapeEditRefusal.SameTemplate),
            "twin-id" => (_f.AmAlt, Twin(_f.AmAlt, _f.AmAlt.Id), ChordShapeEditRefusal.SameTemplate),
            "foreign" => (_f.AmAlt, _f.Am.Clone(), ChordShapeEditRefusal.NotInArrangement),
            _ => (Twin(_f.Am, 77), _f.Am, ChordShapeEditRefusal.Unused)
        };

        Assert.Equal(expected, ChordShapeEdits.PlanMerge(_f.Arrangement, source, target, _editor).Refusal);
    }

    [Fact]
    public void Split_GivesTheSelectedUsesTheirOwnCopy_AndOneUndoRemovesIt()
    {
        var selected = _f.Level1.Chords.Where(c => c.TemplateId == _f.Am.Id).Take(2).ToList();
        var others = AllChords.Where(c => c.TemplateId == _f.Am.Id).Except(selected).ToList();
        var templates = _f.Arrangement.ChordTemplates.ToList();

        var plan = ChordShapeEdits.PlanSplit(_f.Arrangement, _f.Am, selected, [], _editor);
        Assert.False(plan.IsRefused);
        _history.Do(plan.Action!);

        var minted = plan.Minted!;
        Assert.Contains(minted, _f.Arrangement.ChordTemplates);
        Assert.Equal(templates.Max(t => t.Id) + 1, minted.Id);
        Assert.True(minted.HasSameShapeAs(_f.Am));
        Assert.Equal(_f.Am.Name, minted.Name);
        Assert.All(selected, c => Assert.Equal(minted.Id, c.TemplateId));
        Assert.All(others, c => Assert.Equal(_f.Am.Id, c.TemplateId));

        _history.Undo();

        Assert.Equal(templates, _f.Arrangement.ChordTemplates);
        Assert.All(selected, c => Assert.Equal(_f.Am.Id, c.TemplateId));
        Assert.False(_history.CanUndo);
    }

    [Fact]
    public void Split_MovesASelectedHandShapeToo_AndIgnoresSelectedUsesOfOtherShapes()
    {
        var shape = _f.Level1.HandShapes.Single();
        var cChord = _f.Level1.Chords.Single(c => c.TemplateId == _f.C.Id);

        var plan = ChordShapeEdits.PlanSplit(_f.Arrangement, _f.AmAlt, [cChord], [shape], _editor);
        _history.Do(plan.Action!);

        Assert.Equal((0, 1), (plan.ChordsMoved, plan.HandShapesMoved));
        Assert.Equal(plan.Minted!.Id, shape.TemplateId);
        Assert.Equal(_f.C.Id, cChord.TemplateId);
    }

    [Fact]
    public void Split_IsRefused_WithNoSelectedUse_OrWhenTheSelectionIsEveryUse()
    {
        Assert.Equal(ChordShapeEditRefusal.NothingSelected,
            ChordShapeEdits.PlanSplit(_f.Arrangement, _f.Am, [_f.Level1.Chords.Single(c => c.TemplateId == _f.C.Id)], [], _editor).Refusal);

        var every = AllChords.Where(c => c.TemplateId == _f.Am.Id).ToList();
        var shapes = AllShapes.Where(h => h.TemplateId == _f.Am.Id).ToList();
        Assert.Equal(ChordShapeEditRefusal.SelectionIsEveryUse,
            ChordShapeEdits.PlanSplit(_f.Arrangement, _f.Am, every, shapes, _editor).Refusal);
    }

    [Fact]
    public void Rename_RenamesTheSharedTemplate_AsOneNode_AndATrimmedSameNameIsNoEdit()
    {
        Assert.Equal(ChordShapeEditRefusal.SameName,
            ChordShapeEdits.PlanRename(_f.Arrangement, _f.Am, "  Am ", _editor).Refusal);

        _history.Do(ChordShapeEdits.PlanRename(_f.Arrangement, _f.Am, " Am7sus (open) ", _editor).Action!);
        Assert.Equal("Am7sus (open)", _f.Am.Name);
        Assert.True(_f.Am.HasFrets(ChordShapesFixture.AmFrets));

        _history.Undo();
        Assert.Equal("Am", _f.Am.Name);
        Assert.False(_history.CanUndo);
    }

    private ChordTemplate Twin(ChordTemplate template, int id)
    {
        var twin = template.Clone();
        twin.Id = id;
        _f.Arrangement.ChordTemplates.Add(twin);
        return twin;
    }
}
