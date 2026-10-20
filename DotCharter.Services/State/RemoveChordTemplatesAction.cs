namespace DotCharter.Services.State;

/// <summary>
///     Removes exactly these template INSTANCES from an arrangement's template list and, on undo,
/// puts each back at its own index (the templates a level squash or regeneration left
///     with no user, <c>LevelTemplateOrphans</c>). The caller guarantees nothing resolves to them.
///     <para>
///         Identity, never value equality: <see cref="ChordTemplate" />'s <c>Equals</c> ignores the
///         id, so a value-equal twin elsewhere in the list is somebody else's template and must stay
///         (<see cref="ChordTemplateCollectionOps" />). Restoring each instance at its index keeps
///         every other template the same instance in the same slot, which is what the snapshot
///         reconcile it is composed with (index-wise) and the chord panels holding instances need.
///     </para>
/// </summary>
public sealed class RemoveChordTemplatesAction : IChartAction
{
    private readonly IList<ChordTemplate> _collection;
    private readonly List<(int Index, ChordTemplate Template)> _removed = [];
    private readonly List<ChordTemplate> _templates;

    public RemoveChordTemplatesAction(IList<ChordTemplate> collection, IEnumerable<ChordTemplate> templates)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(templates);
        _collection = collection;
        _templates = templates.ToList();
    }

    public bool HasEffect => _templates.Count > 0;

    public void Execute()
    {
        _removed.Clear();
        // From the back, so every recorded index is the one the template held in the list
        // as it stands once the templates before it are back.
        for (var i = _collection.Count - 1; i >= 0; i--)
        {
            var template = _collection[i];
            if (!ChordTemplateCollectionOps.ContainsInstance(_templates, template)) continue;
            _removed.Add((i, template));
            _collection.RemoveAt(i);
        }
    }

    public void Undo()
    {
        // Ascending index order: each insert lands where the template was.
        for (var i = _removed.Count - 1; i >= 0; i--)
        {
            var (index, template) = _removed[i];
            _collection.Insert(Math.Min(index, _collection.Count), template);
        }

        _removed.Clear();
    }
}
