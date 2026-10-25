using DotCharter.Services.State;

namespace DotCharter.Services.Editing.ChordShapes;

/// <summary>
///     Chord templates a gesture leaves with no user, and the action that takes them out in the
///     SAME undo node as the gesture.
///     <para>
///         <b>The rule.</b> A template is stranded when, before the gesture, a chord, a hand shape or
///         a notation-voice chord on any level used it, and after the gesture none does. A template
///         nothing used at the start (a shape typed in the template editor and not placed yet, an
///         import's spare) is not the gesture's to judge and stays; so does every template that
///         still has a user, named or not.
///     </para>
///     <para>
///         <b>How it is known before the gesture runs.</b> Each action of the gesture reports the
///         chords and hand shapes it moves, adds or removes (<see cref="ITemplateUseReporter" />);
///         <see cref="Wrap" /> counts users per template id from the arrangement, applies those
///         changes, and the templates that go from some users to none are the stranded ones. The
///         removal itself (<see cref="RemoveStrandedShapesAction" />) is
///         <see cref="DeleteChordShapesAction" />, so ids stay positions and undo puts back the exact
///         list.
///     </para>
/// </summary>
public static class StrandedShapes
{
    /// <summary>
    ///     <paramref name="action" /> with the removal of the templates it strands appended, or
    ///     <paramref name="action" /> itself when it strands none. A composite stays one composite
    ///     (its label and link policy are kept). The result is not mergeable into a held-key run: a
    ///     removal renumbers the ids every later tick would have captured.
    /// </summary>
    public static IChartAction Wrap(Arrangement? arrangement, IChartAction action, Action? onChanged = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (arrangement == null || action is not ITemplateUseReporter reporter) return action;

        var delta = new TemplateUseDelta();
        reporter.ReportTemplateUses(delta);
        var stranded = Find(arrangement, delta);
        if (stranded.Count == 0) return action;

        var remove = new RemoveStrandedShapesAction(arrangement, stranded, onChanged);
        return action is CompositeAction composite
            ? new CompositeAction([.. composite.Actions, remove])
            {
                LabelKey = composite.LabelKey,
                LinkPolicyOverride = composite.LinkPolicyOverride
            }
            : new CompositeAction([action, remove]);
    }

    /// <summary>
    ///     The templates of <paramref name="arrangement" /> that have users now and have none once
    ///     <paramref name="delta" /> is applied, in list order. Changes nothing.
    /// </summary>
    public static List<ChordTemplate> Find(Arrangement arrangement, TemplateUseDelta delta)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        ArgumentNullException.ThrowIfNull(delta);

        var changes = delta.Changes.ToList();
        if (changes.Count == 0) return [];

        var uses = CountUses(arrangement, delta, out var present);
        var projected = new Dictionary<int, int>(uses);
        foreach (var (user, before, after) in changes)
        {
            var counted = before == TemplateUseDelta.None || present.Contains(user);
            if (!counted) continue;
            if (before >= 0) projected[before] = projected.GetValueOrDefault(before) - 1;
            if (after >= 0) projected[after] = projected.GetValueOrDefault(after) + 1;
        }

        var byId = ChordTemplateResolver.IndexById(arrangement.ChordTemplates);
        var stranded = new HashSet<ChordTemplate>(ReferenceEqualityComparer.Instance);
        foreach (var (id, count) in uses)
        {
            if (count > 0 && projected.GetValueOrDefault(id) <= 0 && byId.TryGetValue(id, out var template))
                stranded.Add(template);
        }

        return [.. arrangement.ChordTemplates.Where(stranded.Contains)];
    }

    /// <summary>
    ///     The users of each template id in <paramref name="arrangement" />, and which of the users
    ///     <paramref name="delta" /> names are among them (by reference).
    /// </summary>
    private static Dictionary<int, int> CountUses(
        Arrangement arrangement, TemplateUseDelta delta, out HashSet<object> present)
    {
        var uses = new Dictionary<int, int>();
        var found = new HashSet<object>(ReferenceEqualityComparer.Instance);
        present = found;

        foreach (var level in arrangement.Levels)
        {
            foreach (var chord in level.Chords) Count(chord, chord.TemplateId);
            foreach (var handShape in level.HandShapes) Count(handShape, handShape.TemplateId);
        }

        foreach (var voice in arrangement.NotationVoices)
        {
            foreach (var chord in voice.Chords) Count(chord, chord.TemplateId);
        }

        return uses;

        void Count(object user, int templateId)
        {
            if (delta.Knows(user)) found.Add(user);
            if (templateId >= 0) uses[templateId] = uses.GetValueOrDefault(templateId) + 1;
        }
    }
}

/// <summary>
///     Takes the templates <see cref="StrandedShapes" /> found out of the arrangement, as the last
///     step of the gesture that stranded them. Execute removes only those that STILL have no user
///     (a template the gesture ended up using stays), through <see cref="DeleteChordShapesAction" />,
///     which renumbers the survivors and repoints every user; Undo puts the exact list, ids and
///     users back.
/// </summary>
public sealed class RemoveStrandedShapesAction : IChartAction, ILinkPolicyAction
{
    /// <summary>Linked measures: every placement's ids are repointed alike, so no copy diverges.</summary>
    public LinkPolicy LinkPolicy => LinkPolicy.Retain;

    private readonly Arrangement _arrangement;
    private readonly List<ChordTemplate> _candidates;
    private readonly Action? _onChanged;
    private DeleteChordShapesAction? _removal;

    public RemoveStrandedShapesAction(Arrangement arrangement, IEnumerable<ChordTemplate> candidates, Action? onChanged)
    {
        _arrangement = arrangement;
        _candidates = [.. candidates];
        _onChanged = onChanged;
    }

    public bool HasEffect => _candidates.Count > 0;

    public void Execute()
    {
        var referenced = ReferencedIds();
        var remove = _candidates.Where(t =>
            ChordTemplateCollectionOps.ContainsInstance(_arrangement.ChordTemplates, t) && !referenced.Contains(t.Id)).ToList();
        if (remove.Count == 0)
        {
            _removal = null;
            return;
        }

        _removal = new DeleteChordShapesAction(_arrangement, remove, _onChanged);
        _removal.Execute();
    }

    public void Undo()
    {
        _removal?.Undo();
        _removal = null;
    }

    /// <summary>Every template id a chord, hand shape or notation-voice chord of the arrangement points at.</summary>
    private HashSet<int> ReferencedIds()
    {
        var ids = new HashSet<int>();
        foreach (var level in _arrangement.Levels)
        {
            foreach (var chord in level.Chords) ids.Add(chord.TemplateId);
            foreach (var handShape in level.HandShapes) ids.Add(handShape.TemplateId);
        }

        foreach (var voice in _arrangement.NotationVoices)
        {
            foreach (var chord in voice.Chords) ids.Add(chord.TemplateId);
        }

        return ids;
    }
}
