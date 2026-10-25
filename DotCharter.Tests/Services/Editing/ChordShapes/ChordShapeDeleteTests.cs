using DotCharter.Services.Editing;
using DotCharter.Services.Editing.ChordShapes;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Tests.Presentation.Dialogs;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Editing.ChordShapes;

/// <summary>
///     Deleting chord shapes: one shape, or every unused one, each ONE undo node. Only a template
///     nothing uses on any level (chords, hand shapes, notation-voice chords) goes; the ids stay
///     positions, so every remaining user still resolves to the same template, and undo restores
///     the exact list and ids.
/// </summary>
public sealed class ChordShapeDeleteTests : IDisposable
{
    private readonly ChordShapesFixture _f = new();
    private readonly HistoryService _history = new(new FakeAppLogger());
    private readonly IChartEditorSurface _editor = RecordingProxy.Create<IChartEditorSurface>().Proxy;
    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter_chordshape_delete_").FullName;

    public void Dispose() => BestEffortDelete.Directory(_dir);

    private IEnumerable<Chord> AllChords => _f.Arrangement.Levels.SelectMany(l => l.Chords);
    private IEnumerable<HandShape> AllShapes => _f.Arrangement.Levels.SelectMany(l => l.HandShapes);

    /// <summary>Which template INSTANCE every chord and hand shape resolves to, in order.</summary>
    private List<ChordTemplate?> Resolved()
    {
        var byId = ChordTemplateResolver.IndexById(_f.Arrangement.ChordTemplates);
        return [.. AllChords.Select(c => c.TemplateId).Concat(AllShapes.Select(h => h.TemplateId))
            .Select(id => byId.GetValueOrDefault(id))];
    }

    private (List<ChordTemplate> Templates, List<int> Ids, List<int> UserIds) Snapshot() =>
        ([.. _f.Arrangement.ChordTemplates], [.. _f.Arrangement.ChordTemplates.Select(t => t.Id)],
            [.. AllChords.Select(c => c.TemplateId).Concat(AllShapes.Select(h => h.TemplateId))]);

    /// <summary>Leaves AmAlt, in the middle of the list, with no user, without removing it.</summary>
    private void EmptyAmAlt()
    {
        foreach (var chord in AllChords.Where(c => c.TemplateId == _f.AmAlt.Id)) chord.TemplateId = _f.Am.Id;
        foreach (var shape in AllShapes.Where(h => h.TemplateId == _f.AmAlt.Id)) shape.TemplateId = _f.Am.Id;
    }

    [Fact]
    public void DeletingAnUnusedShape_RemovesIt_AsOneUndoNode()
    {
        var plan = ChordShapeEdits.PlanDelete(_f.Arrangement, _f.G, _editor);
        Assert.False(plan.IsRefused);

        Assert.True(_history.Do(plan.Action!));

        Assert.DoesNotContain(_f.G, _f.Arrangement.ChordTemplates);
        var node = Assert.Single(_history.GetTree(), n => n.ParentId is not null);
        Assert.Contains(ChordShapeEdits.DeleteLabelKey, node.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void DeletingAUsedShape_IsRefused_WithTheMergeHint()
    {
        var plan = ChordShapeEdits.PlanDelete(_f.Arrangement, _f.AmArp, _editor);

        Assert.True(plan.IsRefused);
        Assert.Equal(ChordShapeEditRefusal.InUse, plan.Refusal);
    }

    /// <summary>A notation-voice chord is a use too: deleting its template would orphan it.</summary>
    [Fact]
    public void AShapeOnlyANotationVoiceUses_IsInUse()
    {
        var voice = new NotationVoice();
        voice.Chords.Add(ChordShapesFixture.ChordOn(_f.G, 3));
        _f.Arrangement.NotationVoices.Add(voice);

        Assert.Equal(ChordShapeEditRefusal.InUse, ChordShapeEdits.PlanDelete(_f.Arrangement, _f.G, _editor).Refusal);
        Assert.DoesNotContain(_f.G, ChordShapeEdits.UnusedTemplates(_f.Arrangement));
    }

    /// <summary>
    ///     After AmAlt in the MIDDLE of the list loses its last use, Remove unused takes it and the
    ///     never-used G, renumbers the survivors to their positions, and every chord and hand shape
    ///     on every level still resolves to the template it did before.
    /// </summary>
    [Fact]
    public void RemoveUnused_TakesExactlyTheZeroUseShapes_AndEveryUserKeepsItsTemplate()
    {
        EmptyAmAlt();
        var resolvedBefore = Resolved();

        var plan = ChordShapeEdits.PlanRemoveUnused(_f.Arrangement, _editor);
        Assert.Equal(2, plan.TemplatesRemoved);
        Assert.True(_history.Do(plan.Action!));

        Assert.Equal([_f.Am, _f.AmArp, _f.C, _f.Open], _f.Arrangement.ChordTemplates);
        Assert.Equal([0, 1, 2, 3], _f.Arrangement.ChordTemplates.Select(t => t.Id));
        Assert.Equal(resolvedBefore, Resolved());
        Assert.All(Resolved(), Assert.NotNull);
    }

    [Fact]
    public void Undo_RestoresTheTemplatesIdsAndUsersExactly_AndRedoAgrees()
    {
        EmptyAmAlt();
        var before = Snapshot();

        _history.Do(ChordShapeEdits.PlanRemoveUnused(_f.Arrangement, _editor).Action!);
        var after = Snapshot();
        _history.Undo();

        var undone = Snapshot();
        Assert.Equal(before.Templates, undone.Templates);
        Assert.Equal(before.Ids, undone.Ids);
        Assert.Equal(before.UserIds, undone.UserIds);

        _history.Redo();
        var redone = Snapshot();
        Assert.Equal(after.Templates, redone.Templates);
        Assert.Equal(after.Ids, redone.Ids);
        Assert.Equal(after.UserIds, redone.UserIds);
    }

    [Fact]
    public void RemoveUnused_WithEveryShapeInUse_IsRefused()
    {
        _history.Do(ChordShapeEdits.PlanDelete(_f.Arrangement, _f.G, _editor).Action!);

        Assert.Equal(ChordShapeEditRefusal.NothingUnused, ChordShapeEdits.PlanRemoveUnused(_f.Arrangement, _editor).Refusal);
    }

    /// <summary>The project after Remove unused saves and reloads with every chord on its shape.</summary>
    [Fact]
    public void RemoveUnused_SurvivesASaveAndReload()
    {
        EmptyAmAlt();
        _history.Do(ChordShapeEdits.PlanRemoveUnused(_f.Arrangement, _editor).Action!);
        var project = new ChartProject();
        project.Arrangements.Add(_f.Arrangement);
        var path = Path.Combine(_dir, "project.rscp");

        new RscpChartSerializer().Save(path, project);
        var reloaded = new RscpChartSerializer().Load(path).Arrangements[0];

        Assert.Equal(_f.Arrangement.ChordTemplates.Select(t => t.Name), reloaded.ChordTemplates.Select(t => t.Name));
        var byId = ChordTemplateResolver.IndexById(reloaded.ChordTemplates);
        var expected = AllChords.Select(c => ChordTemplateResolver.IndexById(_f.Arrangement.ChordTemplates)[c.TemplateId].Name);
        Assert.Equal(expected, reloaded.Levels.SelectMany(l => l.Chords).Select(c => byId[c.TemplateId].Name));
    }
}
