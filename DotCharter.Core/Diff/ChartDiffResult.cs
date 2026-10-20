using DotCharter.Core.Domain.Guitar;
using DotCharter.Core.Domain.Primitives;

namespace DotCharter.Core.Diff;

public enum ChartDiffChange
{
    Added,
    Removed,
    Changed
}

public enum ChartDiffSoundKind
{
    Note,
    Chord
}

/// <summary>Options for <see cref="ChartDiffEngine"/>.</summary>
public sealed class ChartDiffOptions
{
    /// <summary>Two sounds on the same string (or two chords) whose positions
    /// differ by at most this many beats are treated as the SAME slot and
    /// compared field-by-field; farther apart they become a remove + add.
    /// Default: 1/64 beat.</summary>
    public Fraction PositionTolerance { get; init; } = new(1, 64);
}

/// <summary>
/// One difference between the old and the new chart. For
/// <see cref="ChartDiffChange.Changed"/> entries both sides are present and
/// <see cref="ChangedFields"/> names what differs; Added carries only the new
/// sound, Removed only the old one. <see cref="Position"/> is the new side's
/// position when available, else the old side's.
/// </summary>
public sealed class ChartSoundDiff
{
    public ChartSoundDiff(ChartDiffChange change, ChartDiffSoundKind kind, FractionalPosition position,
        int stringIndex, GuitarNote? oldNote = null, GuitarNote? newNote = null,
        Chord? oldChord = null, Chord? newChord = null, IReadOnlyList<string>? changedFields = null)
    {
        Change = change;
        Kind = kind;
        Position = position;
        StringIndex = stringIndex;
        OldNote = oldNote;
        NewNote = newNote;
        OldChord = oldChord;
        NewChord = newChord;
        ChangedFields = changedFields ?? Array.Empty<string>();
    }

    public ChartDiffChange Change { get; }
    public ChartDiffSoundKind Kind { get; }
    public FractionalPosition Position { get; }

    /// <summary>String of a note diff; -1 for chord diffs.</summary>
    public int StringIndex { get; }

    public GuitarNote? OldNote { get; }
    public GuitarNote? NewNote { get; }
    public Chord? OldChord { get; }
    public Chord? NewChord { get; }

    /// <summary>Field names that differ (Changed entries only), e.g.
    /// "Fret", "Sustain", "Slide", "Bends", "Notes", "Template".</summary>
    public IReadOnlyList<string> ChangedFields { get; }
}

/// <summary>Element classes that carry chart STRUCTURE rather than sound.</summary>
public enum ChartDiffElementKind
{
    Anchor,
    HandShape,
    FretHandMute,
    EventPoint,
    ToneChange,
    ChordTemplate,
    PhraseIteration,

    /// <summary>
    ///     The arrangement HEADER — tuning, capo, cent offset, string count,
    ///     arrangement type/subtype, scroll speed, tone slots. It has no
    ///     timeline position, so it is reported once as a
    ///     <see cref="ChartDiffChange.Changed" /> entry naming the fields.
    /// </summary>
    ArrangementHeader
}

/// <summary>
///     One difference in an element that is not a sound — an anchor, hand shape,
///     fret-hand mute, phrase/section marker, tone change, phrase iteration or chord
///     template. These are chart edits in every sense that matters to a peer deciding
///     whether it has an update, so they count toward
///     <see cref="ChartDiffResult.IsEmpty" />.
/// </summary>
public sealed class ChartStructureDiff
{
    public ChartStructureDiff(ChartDiffChange change, ChartDiffElementKind kind, FractionalPosition position,
        int difficultyIndex, string label, IReadOnlyList<string>? changedFields = null)
    {
        Change = change;
        Kind = kind;
        Position = position;
        DifficultyIndex = difficultyIndex;
        Label = label;
        ChangedFields = changedFields ?? Array.Empty<string>();
    }

    public ChartDiffChange Change { get; }
    public ChartDiffElementKind Kind { get; }

    /// <summary>Timeline position, or <see cref="FractionalPosition.Zero" /> for an
    /// element that has none (a chord template).</summary>
    public FractionalPosition Position { get; }

    /// <summary>Difficulty level the element belongs to; <c>-1</c> for
    /// arrangement-level elements.</summary>
    public int DifficultyIndex { get; }

    /// <summary>Short identifying text — the section name, tone name, template name
    /// with frets, or the anchor fret.</summary>
    public string Label { get; }

    /// <summary>Field names that differ (Changed entries only).</summary>
    public IReadOnlyList<string> ChangedFields { get; }
}

/// <summary>Diff of one difficulty level (position-sorted entries).</summary>
public sealed class ChartLevelDiff
{
    public ChartLevelDiff(int difficultyIndex, IReadOnlyList<ChartSoundDiff> entries)
    {
        DifficultyIndex = difficultyIndex;
        Entries = entries;
    }

    public int DifficultyIndex { get; }
    public IReadOnlyList<ChartSoundDiff> Entries { get; }
    public bool IsEmpty => Entries.Count == 0;
}

/// <summary>Compact structural diff of two arrangements, level by level
/// (levels paired by <c>DifficultyIndex</c>).</summary>
public sealed class ChartDiffResult
{
    public ChartDiffResult(IReadOnlyList<ChartLevelDiff> levels,
        IReadOnlyList<ChartStructureDiff>? structure = null)
    {
        Levels = levels;
        Structure = structure ?? Array.Empty<ChartStructureDiff>();

        var added = 0;
        var removed = 0;
        var changed = 0;

        foreach (var change in levels.SelectMany(l => l.Entries).Select(e => e.Change)
                     .Concat(Structure.Select(e => e.Change)))
        {
            switch (change)
            {
                case ChartDiffChange.Added:
                    added++;
                    break;
                case ChartDiffChange.Removed:
                    removed++;
                    break;
                default:
                    changed++;
                    break;
            }
        }

        AddedCount = added;
        RemovedCount = removed;
        ChangedCount = changed;
    }

    public IReadOnlyList<ChartLevelDiff> Levels { get; }

    /// <summary>Arrangement- and level-scoped differences that are not sounds:
    /// anchors, hand shapes, fret-hand mutes, phrase/section markers, tone changes,
    /// phrase iterations and chord templates.</summary>
    public IReadOnlyList<ChartStructureDiff> Structure { get; }

    public int AddedCount { get; }
    public int RemovedCount { get; }
    public int ChangedCount { get; }
    public int TotalCount => AddedCount + RemovedCount + ChangedCount;

    /// <summary>True when the two charts have identical sound content, ignoring
    /// every structural element. Kept separate from <see cref="IsEmpty" /> because
    /// "did any NOTE change" is a different question from "is this an update".</summary>
    public bool SoundsIdentical => Levels.All(l => l.IsEmpty);

    /// <summary>True when the two charts are identical in sounds AND structure —
    /// the signal the LAN "peer has an update" flow cares about. A re-sectioned,
    /// re-anchored or re-hand-shaped chart is unambiguously an update, so binding
    /// that check to sounds alone reported a changed chart as unchanged.</summary>
    public bool IsEmpty => TotalCount == 0;
}
