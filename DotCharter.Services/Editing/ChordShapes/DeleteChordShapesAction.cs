using System.Collections.ObjectModel;
using DotCharter.Services.Editing.Improvement;
using DotCharter.Services.State;

namespace DotCharter.Services.Editing.ChordShapes;

/// <summary>
///     Takes chord templates nothing uses out of an arrangement (the Chord shapes list's Delete and
///     Remove unused). Ids stay POSITIONS when they were (the <c>.dcproj</c> numbers templates by
///     index, <see cref="LevelTemplateOrphans" />): the survivors are renumbered to their new
///     index and every chord, hand shape and notation-voice chord is repointed with them. Undo puts
///     back the exact list, ids and user ids. By INSTANCE throughout (<see cref="ChordTemplate" />
///     has value equality).
/// </summary>
public sealed class DeleteChordShapesAction : IChartAction, ILinkPolicyAction
{
    /// <summary>Linked measures: every placement's ids are repointed alike, so no copy diverges.</summary>
    public LinkPolicy LinkPolicy => LinkPolicy.Retain;

    private readonly Arrangement _arrangement;
    private readonly HashSet<ChordTemplate> _remove;
    private readonly Action? _onChanged;

    private List<ChordTemplate> _templatesBefore = [];
    private List<int> _idsBefore = [];
    private List<(Chord Chord, int Id)> _chordIdsBefore = [];
    private List<(HandShape Shape, int Id)> _shapeIdsBefore = [];

    public DeleteChordShapesAction(Arrangement arrangement, IEnumerable<ChordTemplate> remove, IChartEditorSurface editor)
        : this(arrangement, remove, editor.RequestRedraw)
    {
    }

    /// <summary><paramref name="onChanged" /> runs after every Execute and Undo (a redraw request); null for none.</summary>
    public DeleteChordShapesAction(Arrangement arrangement, IEnumerable<ChordTemplate> remove, Action? onChanged)
    {
        _arrangement = arrangement;
        _remove = new HashSet<ChordTemplate>(remove, ReferenceEqualityComparer.Instance);
        _onChanged = onChanged;
    }

    public void Execute()
    {
        var templates = _arrangement.ChordTemplates;
        _templatesBefore = [.. templates];
        _idsBefore = [.. templates.Select(t => t.Id)];
        _chordIdsBefore = [.. AllChords().Select(c => (c, c.TemplateId))];
        _shapeIdsBefore = [.. AllHandShapes().Select(h => (h, h.TemplateId))];

        var positional = templates.Select((t, i) => t.Id == i).All(p => p);
        foreach (var template in _remove) ChordTemplateCollectionOps.RemoveInstance(templates, template);

        if (positional)
        {
            var newIds = new Dictionary<int, int>();
            for (var i = 0; i < templates.Count; i++)
            {
                newIds[templates[i].Id] = i;
                templates[i].Id = i;
            }

            foreach (var chord in AllChords())
                chord.TemplateId = newIds.TryGetValue(chord.TemplateId, out var id) ? id : chord.TemplateId;
            foreach (var shape in AllHandShapes())
                shape.TemplateId = newIds.TryGetValue(shape.TemplateId, out var id) ? id : shape.TemplateId;
        }

        _onChanged?.Invoke();
    }

    public void Undo()
    {
        Restore(_arrangement.ChordTemplates, _templatesBefore);
        for (var i = 0; i < _templatesBefore.Count; i++) _templatesBefore[i].Id = _idsBefore[i];
        foreach (var (chord, id) in _chordIdsBefore) chord.TemplateId = id;
        foreach (var (shape, id) in _shapeIdsBefore) shape.TemplateId = id;
        _onChanged?.Invoke();
    }

    private static void Restore(ObservableCollection<ChordTemplate> templates, List<ChordTemplate> before)
    {
        templates.Clear();
        foreach (var template in before) templates.Add(template);
    }

    private IEnumerable<Chord> AllChords() =>
        _arrangement.Levels.SelectMany(l => l.Chords).Concat(_arrangement.NotationVoices.SelectMany(v => v.Chords));

    private IEnumerable<HandShape> AllHandShapes() => _arrangement.Levels.SelectMany(l => l.HandShapes);
}
