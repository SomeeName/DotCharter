namespace DotCharter.Services.State;

/// <summary>
///     An action that changes which chord template a chord or hand shape points at, or adds or
///     removes a chord or hand shape that does. <c>StrandedShapes</c> asks every action of a
///     gesture before it runs, so the templates the gesture leaves with no user can leave in the
///     SAME undo node.
/// </summary>
public interface ITemplateUseReporter
{
    /// <summary>Records, for each chord or hand shape the action touches, the id it has now and the id it will have.</summary>
    void ReportTemplateUses(TemplateUseDelta delta);
}

/// <summary>
///     What a gesture does to the chord and hand shape users of chord templates: per user (by
///     reference), the template id it points at before the gesture and the one it points at after.
///     <para>
///         <see cref="None" /> stands for "not a user": a chord an action adds starts at
///         <see cref="None" />, a chord an action removes ends there. A user touched by several
///         actions keeps the id of its FIRST report as its start and the id of its LAST as its end,
///         so a chord that two actions repoint in turn counts once.
///     </para>
/// </summary>
public sealed class TemplateUseDelta
{
    /// <summary>The id of a user that is not part of the chart: one an action adds starts here, one it removes ends here.</summary>
    public const int None = int.MinValue;

    private readonly Dictionary<object, (int Before, int After)> _users = new(ReferenceEqualityComparer.Instance);

    /// <summary>The users whose template id differs after the gesture.</summary>
    public IEnumerable<(object User, int Before, int After)> Changes =>
        _users.Where(u => u.Value.Before != u.Value.After).Select(u => (u.Key, u.Value.Before, u.Value.After));

    /// <summary>True when <paramref name="user" /> has been reported.</summary>
    public bool Knows(object user) => _users.ContainsKey(user);

    /// <summary>A chord or hand shape moves from one template id to another.</summary>
    public void Move(object user, int before, int after) =>
        _users[user] = _users.TryGetValue(user, out var known) ? (known.Before, after) : (before, after);

    /// <summary>
    ///     Each chord or hand shape of <paramref name="targets" /> changes from the template id of the
    ///     same-index element of <paramref name="before" /> to that of <paramref name="after" />.
    /// </summary>
    public void Moved<T>(IReadOnlyList<T> targets, IReadOnlyList<T> before, IReadOnlyList<T> after)
    {
        if (before.Count != targets.Count || after.Count != targets.Count) return;
        for (var i = 0; i < targets.Count; i++)
        {
            if (IdOf(before[i]) is { } from && IdOf(after[i]) is { } to) Move(targets[i]!, from, to);
        }
    }

    /// <summary>The chords and hand shapes among <paramref name="items" /> stop existing.</summary>
    public void Removed<T>(IEnumerable<T> items)
    {
        foreach (var item in items)
        {
            if (IdOf(item) is { } id) Move(item!, id, None);
        }
    }

    /// <summary>The chords and hand shapes among <paramref name="items" /> start existing.</summary>
    public void Added<T>(IEnumerable<T> items)
    {
        foreach (var item in items)
        {
            if (IdOf(item) is { } id) Move(item!, None, id);
        }
    }

    /// <summary>The template id of a chord or hand shape; null for anything else.</summary>
    public static int? IdOf(object? user) => user switch
    {
        Chord chord => chord.TemplateId,
        HandShape handShape => handShape.TemplateId,
        _ => null
    };
}
