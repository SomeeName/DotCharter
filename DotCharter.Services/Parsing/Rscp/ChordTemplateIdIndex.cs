namespace DotCharter.Services.Parsing.Rscp;

/// <summary>
///     An arrangement's chord templates keyed by <see cref="ChordTemplate.Id" />,
///     for a pass that resolves ids in bulk.
/// </summary>
/// <remarks>
///     <para>
///     <c>RscpTemplateManager.FindTemplate</c> was
///     <c>arr.ChordTemplates.FirstOrDefault(t =&gt; t.Id == templateId)</c> — a
///     linear scan with a delegate per element — called ONCE PER CHORD (the
///     validity check) and TWICE PER HAND SHAPE (the link pass), across every
///     level of the arrangement. `RegenerateTemplates` runs inside
///     <c>RscpWriter</c>, i.e. on every project save: a DD-generated chart with
///     ~16,000 chords across its levels and ~200 templates paid ~3.2M delegate
///     invocations for a dictionary lookup.
///     </para>
///     <para>
///     Semantics are the scan's, exactly: a negative id is the "untemplated"
///     sentinel and resolves to null, a dangling id resolves to null, and when
///     two templates somehow share an id the FIRST one wins — which is what
///     <c>FirstOrDefault</c> returned. Templates minted during the pass are
///     added through <see cref="Add" /> so later lookups see them, the way they
///     saw an appended list element.
///     </para>
/// </remarks>
internal sealed class ChordTemplateIdIndex
{
    private readonly Dictionary<int, ChordTemplate> _byId;

    /// <summary>Reads <paramref name="templates" /> exactly once.</summary>
    public ChordTemplateIdIndex(IReadOnlyList<ChordTemplate> templates)
    {
        ArgumentNullException.ThrowIfNull(templates);

        _byId = new Dictionary<int, ChordTemplate>(templates.Count);
        for (var i = 0; i < templates.Count; i++)
        {
            var template = templates[i];
            // TryAdd, not the indexer: FirstOrDefault kept the FIRST match.
            _byId.TryAdd(template.Id, template);
        }
    }

    /// <summary>The template carrying <paramref name="templateId" />, or null —
    /// including for the -1 untemplated sentinel and for a dangling id.</summary>
    public ChordTemplate? Find(int templateId) =>
        templateId >= 0 && _byId.TryGetValue(templateId, out var template) ? template : null;

    /// <summary>Records a template appended to the arrangement during the pass.</summary>
    public void Add(ChordTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        _byId.TryAdd(template.Id, template);
    }
}
