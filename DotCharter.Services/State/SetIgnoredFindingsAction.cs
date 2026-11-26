namespace DotCharter.Services.State;

/// <summary>
///     Ignores or stops ignoring chart-check warnings in the Issues tab. The list lives on the project and
///     is saved with it, so a change is an undoable edit that marks the project dirty. Adds skip entries already
///     present; removes skip entries already gone, so <see cref="HasEffect" /> is false for a no-op.
/// </summary>
public sealed class SetIgnoredFindingsAction : IChartAction, ILinkPolicyAction
{
    private readonly IList<IgnoredFinding> _target;
    private readonly List<IgnoredFinding> _added;
    private readonly List<(int Index, IgnoredFinding Entry)> _removed = [];

    public SetIgnoredFindingsAction(IList<IgnoredFinding> target, IEnumerable<IgnoredFinding> add, IEnumerable<IgnoredFinding> remove)
    {
        _target = target;
        var drop = remove.ToHashSet();
        _added = [.. add.Distinct().Where(entry => !target.Contains(entry) && !drop.Contains(entry))];
        for (var i = 0; i < target.Count; i++)
        {
            if (drop.Contains(target[i])) _removed.Add((i, target[i]));
        }
    }

    /// <summary>Linked measures: no chart content changes.</summary>
    public LinkPolicy LinkPolicy => LinkPolicy.Retain;

    public bool HasEffect => _added.Count > 0 || _removed.Count > 0;

    public void Execute()
    {
        for (var i = _removed.Count - 1; i >= 0; i--) _target.RemoveAt(_removed[i].Index);
        foreach (var entry in _added) _target.Add(entry);
    }

    public void Undo()
    {
        for (var i = 0; i < _added.Count; i++) _target.RemoveAt(_target.Count - 1);
        foreach (var (index, entry) in _removed) _target.Insert(index, entry);
    }
}
