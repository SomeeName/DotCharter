using DotCharter.Services.State;

namespace DotCharter.Services.Editing.Tones;

/// <summary>
/// Undoable history actions behind the Tone Designer (plan 2.6): project-owned
/// tone-definition upserts/deletes and arrangement tone-slot assignment. They
/// mutate ONLY the domain graph (<see cref="ChartProject.ToneDefinitions"/> /
/// <see cref="Arrangement"/> tone fields) so undo/redo and the dirty tracker
/// work exactly like every other edit.
/// </summary>
public sealed class UpsertToneDefinitionAction : IChartAction, ILinkPolicyAction
{
    /// <summary>Linked measures: cannot change linked content in beats.</summary>
    public LinkPolicy LinkPolicy => LinkPolicy.Retain;

    private readonly ChartProject _project;
    private readonly string? _removeKey;
    private readonly ToneDefinition? _newValue;

    private ToneDefinition? _removed;
    private int _removedIndex = -1;

    /// <param name="project">The project whose tone set is edited.</param>
    /// <param name="removeKey">Key of the existing definition to replace/delete
    /// (trim + case-insensitive, matching the tone-key rules everywhere else);
    /// null for a pure add.</param>
    /// <param name="newValue">The definition to insert (at the replaced entry's
    /// position, else appended); null for a pure delete.</param>
    public UpsertToneDefinitionAction(ChartProject project, string? removeKey, ToneDefinition? newValue)
    {
        _project = project;
        _removeKey = removeKey;
        _newValue = newValue;
    }

    public bool HasEffect => _removeKey != null || _newValue != null;

    public void Execute()
    {
        var tones = _project.ToneDefinitions;

        _removed = null;
        _removedIndex = -1;
        if (_removeKey is { } key)
        {
            for (var i = 0; i < tones.Count; i++)
            {
                if (!string.Equals(tones[i].Key.Trim(), key.Trim(), StringComparison.OrdinalIgnoreCase))
                    continue;
                _removed = tones[i];
                _removedIndex = i;
                tones.RemoveAt(i);
                break;
            }
        }

        if (_newValue is { } value)
        {
            var index = _removedIndex >= 0 ? _removedIndex : tones.Count;
            tones.Insert(index, value);
        }
    }

    public void Undo()
    {
        var tones = _project.ToneDefinitions;

        if (_newValue is { } value)
        {
            var index = tones.IndexOf(value);
            if (index >= 0) tones.RemoveAt(index);
        }

        if (_removed is { } removed)
            tones.Insert(Math.Min(Math.Max(_removedIndex, 0), tones.Count), removed);
    }
}

/// <summary>The full tone-slot state of one arrangement, captured for
/// undo/redo. <see cref="ToneNames"/> is the derived distinct non-empty A–D
/// list (kept in sync with the slots, mirroring the package reader's SNG mapper);
/// <see cref="StartingTone"/> follows <see cref="ToneBase"/> — the in-game-tone
/// coordinator resolves its base tone from either.</summary>
public sealed record ToneSlotAssignment(
    string ToneBase, string ToneA, string ToneB, string ToneC, string ToneD,
    IReadOnlyList<string> ToneNames, string StartingTone)
{
    public static ToneSlotAssignment Capture(Arrangement arrangement) => new(
        arrangement.ToneBase, arrangement.ToneA, arrangement.ToneB,
        arrangement.ToneC, arrangement.ToneD,
        arrangement.ToneNames.ToList(), arrangement.StartingTone);

    /// <summary>Builds the target assignment from edited slot values: trims
    /// everything, derives ToneNames (distinct non-empty A–D in slot order) and
    /// points StartingTone at the base.</summary>
    public static ToneSlotAssignment FromSlots(string toneBase, string a, string b, string c, string d)
    {
        var names = new List<string>(4);
        foreach (var slot in new[] { a.Trim(), b.Trim(), c.Trim(), d.Trim() })
        {
            if (slot.Length > 0 && !names.Contains(slot, StringComparer.OrdinalIgnoreCase))
                names.Add(slot);
        }

        var trimmedBase = toneBase.Trim();
        return new ToneSlotAssignment(trimmedBase, a.Trim(), b.Trim(), c.Trim(), d.Trim(), names, trimmedBase);
    }

    public bool SameAs(ToneSlotAssignment other) =>
        ToneBase == other.ToneBase && ToneA == other.ToneA && ToneB == other.ToneB &&
        ToneC == other.ToneC && ToneD == other.ToneD && StartingTone == other.StartingTone &&
        ToneNames.SequenceEqual(other.ToneNames, StringComparer.Ordinal);

    public void ApplyTo(Arrangement arrangement)
    {
        arrangement.ToneBase = ToneBase;
        arrangement.ToneA = ToneA;
        arrangement.ToneB = ToneB;
        arrangement.ToneC = ToneC;
        arrangement.ToneD = ToneD;
        arrangement.StartingTone = StartingTone;
        arrangement.ToneNames.Clear();
        foreach (var name in ToneNames) arrangement.ToneNames.Add(name);
    }
}

/// <summary>Assigns tone keys to one arrangement's Base/A–D slots (plus the
/// derived ToneNames + StartingTone sync). Undo restores the captured
/// pre-assignment state verbatim.</summary>
public sealed class AssignArrangementTonesAction : IChartAction, ILinkPolicyAction
{
    /// <summary>Linked measures: cannot change linked content in beats.</summary>
    public LinkPolicy LinkPolicy => LinkPolicy.Retain;

    private readonly Arrangement _arrangement;
    private readonly ToneSlotAssignment _before;
    private readonly ToneSlotAssignment _after;

    public AssignArrangementTonesAction(Arrangement arrangement, ToneSlotAssignment before, ToneSlotAssignment after)
    {
        _arrangement = arrangement;
        _before = before;
        _after = after;
    }

    public bool HasEffect => !_before.SameAs(_after);

    public void Execute() => _after.ApplyTo(_arrangement);

    public void Undo() => _before.ApplyTo(_arrangement);
}
