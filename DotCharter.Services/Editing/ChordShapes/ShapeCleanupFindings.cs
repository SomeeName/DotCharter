namespace DotCharter.Services.Editing.ChordShapes;

/// <summary>How two near-duplicate shapes differ; the frets and the arpeggio flavour are the same.</summary>
public enum ShapeCleanupDifference
{
    /// <summary>Other fingers on some strings.</summary>
    Fingers,

    /// <summary>Same grip, two different names.</summary>
    Name,

    /// <summary>Same grip and name, other flags (no fingering, no-op in Rocksmith).</summary>
    Flags
}

/// <summary>The answer to a near-duplicate pair.</summary>
public enum ShapeCleanupChoice
{
    /// <summary>Nothing changes.</summary>
    KeepBoth,

    /// <summary>The second shape's uses move onto the first, which keeps its fingers and name.</summary>
    UseFirst,

    /// <summary>The first shape's uses move onto the second, which keeps its fingers and name.</summary>
    UseSecond
}

/// <summary>
///     Identical shapes (frets, fingers, flags) that become one: <see cref="Keeper" /> keeps the name,
///     every <see cref="Copies" /> use moves onto it and the copy goes. <see cref="Chords" /> and
///     <see cref="HandShapes" /> count what moves.
/// </summary>
public sealed record ShapeCleanupMerge(
    ChordTemplate Keeper, IReadOnlyList<ChordTemplate> Copies, int Chords, int HandShapes)
{
    /// <summary>The keeper and its copies.</summary>
    public int Count => Copies.Count + 1;
}

/// <summary>Two used shapes with the same frets that are not identical. Nothing happens until a charter picks.</summary>
public sealed record ShapeCleanupNearDuplicate(
    ChordTemplate First, ChordTemplate Second, int FirstUses, int SecondUses,
    ShapeCleanupDifference Difference, IReadOnlyList<int> Strings);

/// <summary>What Clean up found in one arrangement. Nothing here has been changed.</summary>
public sealed record ShapeCleanupFindings(
    IReadOnlyList<ShapeCleanupMerge> Merges,
    IReadOnlyList<ChordTemplate> Unused,
    IReadOnlyList<ShapeCleanupNearDuplicate> NearDuplicates)
{
    public static readonly ShapeCleanupFindings None = new([], [], []);

    public bool IsTidy => Count == 0;

    /// <summary>Duplicate groups, unused shapes and pairs to review.</summary>
    public int Count => Merges.Count + Unused.Count + NearDuplicates.Count;
}
