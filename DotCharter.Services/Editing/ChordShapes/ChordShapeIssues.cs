namespace DotCharter.Services.Editing.ChordShapes;

/// <summary>What is wrong with a chord shape, in the one way the Shapes panel names.</summary>
public enum ChordShapeIssueKind
{
    None,

    /// <summary>Another shape has the same frets and arpeggio flavour and more uses: merge into it.</summary>
    SameFrets,

    /// <summary>A fretted string has no finger.</summary>
    NoFingering
}

/// <summary>A shape's issue; <see cref="Keeper" /> is the shape a <see cref="ChordShapeIssueKind.SameFrets" /> merge keeps.</summary>
public readonly record struct ChordShapeIssue(ChordShapeIssueKind Kind, ChordTemplate? Keeper = null)
{
    public static readonly ChordShapeIssue None = new(ChordShapeIssueKind.None);

    public bool IsIssue => Kind != ChordShapeIssueKind.None;
}

/// <summary>
///     The one issue each shape of the Shapes panel carries, and where a copy's uses go. Pure:
///     reads catalog entries, writes nothing.
///     <para>
///         A group of same-fret copies keeps the shape with the most uses (the earlier one on a
///         tie); every other USED copy names that keeper. An unused shape has no issue — it is
///         deleted, not fixed. A shape that is both a copy and unfingered names the copy, since
///         merging usually fixes both.
///     </para>
/// </summary>
public sealed class ChordShapeIssues
{
    private readonly Dictionary<ChordTemplate, ChordShapeEntry> _entries = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ChordTemplate, int> _order = new(ReferenceEqualityComparer.Instance);

    public ChordShapeIssues(IReadOnlyList<ChordShapeEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        for (var i = 0; i < entries.Count; i++)
        {
            _entries[entries[i].Template] = entries[i];
            _order[entries[i].Template] = i;
        }
    }

    /// <summary>The issue of <paramref name="template" />; none for a template the entries do not hold.</summary>
    public ChordShapeIssue For(ChordTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (!_entries.TryGetValue(template, out var entry) || entry.Uses.All == 0) return ChordShapeIssue.None;

        if (entry.IsDuplicate)
        {
            var keeper = Keeper([template, .. entry.Duplicates]);
            if (!ReferenceEquals(keeper, template)) return new ChordShapeIssue(ChordShapeIssueKind.SameFrets, keeper);
        }

        return entry.FingerState == ChordFingerState.Missing
            ? new ChordShapeIssue(ChordShapeIssueKind.NoFingering)
            : ChordShapeIssue.None;
    }

    /// <summary>
    ///     The same-fret shape <paramref name="template" /> would be merged into: the other copy
    ///     with the most uses, then list order. Null when it has no copy.
    /// </summary>
    public ChordTemplate? MergeTargetFor(ChordTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return _entries.TryGetValue(template, out var entry) && entry.IsDuplicate ? Keeper(entry.Duplicates) : null;
    }

    private ChordTemplate Keeper(IEnumerable<ChordTemplate> group) => group
        .OrderByDescending(t => _entries.TryGetValue(t, out var e) ? e.Uses.All : 0)
        .ThenBy(t => _order.GetValueOrDefault(t, int.MaxValue))
        .First();
}
