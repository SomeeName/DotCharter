using System.Text.Json;
using System.Text.Json.Nodes;
using DotCharter.Services.State;
using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Services.Editing.Tones;

/// <summary>Why <see cref="ToneKeyEdits.PlanRename" /> did or did not build a rename.</summary>
public enum ToneKeyRenameOutcome
{
    /// <summary><see cref="ToneKeyRenamePlan.Action" /> renames every place the key is stored.</summary>
    Ready,

    /// <summary>The new key is the old one, spelled exactly the same: nothing to do.</summary>
    Unchanged,

    /// <summary>The new key is not a tone key (<see cref="RsToneAuthoring.IsValidKey" />).</summary>
    InvalidKey,

    /// <summary>The project neither defines nor references the old key.</summary>
    NotFound,

    /// <summary>The new key is another tone already. Tones are never merged.</summary>
    Taken
}

/// <summary>The answer of <see cref="ToneKeyEdits.PlanRename" />: an action only when <see cref="Outcome" /> is Ready.</summary>
public sealed record ToneKeyRenamePlan(ToneKeyRenameOutcome Outcome, string NewKey, IChartAction? Action = null)
{
    public bool IsReady => Outcome == ToneKeyRenameOutcome.Ready && Action is not null;
}

/// <summary>
///     The two verbs that change a tone's IDENTITY — rename and delete — as ONE undoable
/// action each. A tone key is stored in five places, and each place that renamed
///     or deletes one would otherwise update a different subset of them:
///     <list type="number">
///         <item><see cref="ChartProject.ToneDefinitions" /> (<see cref="ToneDefinition.Key" />,
///         and the <c>Key</c> inside its manifest body);</item>
///         <item>each arrangement's <see cref="Arrangement.ToneBase" /> and A–D slots;</item>
///         <item>its <see cref="Arrangement.StartingTone" />;</item>
///         <item>its derived <see cref="Arrangement.ToneNames" />;</item>
///         <item>every <see cref="ToneChange.ToneName" />.</item>
///     </list>
///     Matching is trim- and case-insensitive, like <see cref="UpsertToneDefinitionAction" />
///     and the export check. Pure: nothing changes until the history runs the action.
///     <para>
///         A rename's undo node ends with a <see cref="ToneKeyRenameJournalStep" />: the store OUTSIDE
///         the project (<c>SongToneEditStore</c>, the player's tone edits and instruments) follows the
///         key only when a SAVE writes it (<see cref="ToneKeyRenameJournal" />, <c>ProjectSaver</c>).
///     </para>
///     <para>
///         <b>A collision is refused, never merged.</b> The new key is TAKEN when another
///         tone has a definition under it, or when it is placed/declared and the renamed
///         tone has no definition either. The one target that is allowed although it is in
///         use is a key that is referenced but UNDEFINED while the renamed tone IS defined:
///         that rename gives those references the body they were missing, which is how a
///         project broken by the old partial renames is repaired.
///     </para>
/// </summary>
public static class ToneKeyEdits
{
    /// <summary>Plans renaming <paramref name="oldKey" /> to <paramref name="newKey" /> everywhere.
    /// <paramref name="replacement" />, when given, is the definition body to store under the
    /// new key (the Tone designer commits its buffered edit with the rename, as one step).</summary>
    public static ToneKeyRenamePlan PlanRename(ChartProject project, string oldKey, string newKey,
        ToneDefinition? replacement = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        var from = (oldKey ?? "").Trim();
        var to = (newKey ?? "").Trim();
        if (string.Equals(from, to, StringComparison.Ordinal)) return new ToneKeyRenamePlan(ToneKeyRenameOutcome.Unchanged, to);
        if (!RsToneAuthoring.IsValidKey(to)) return new ToneKeyRenamePlan(ToneKeyRenameOutcome.InvalidKey, to);

        var definition = FindDefinition(project, from);
        if (definition is null && !IsReferenced(project, from))
            return new ToneKeyRenamePlan(ToneKeyRenameOutcome.NotFound, to);

        // A case-only change renames the tone onto itself: never a collision.
        if (!Same(from, to) &&
            (FindDefinition(project, to) is not null || (definition is null && IsReferenced(project, to))))
        {
            return new ToneKeyRenamePlan(ToneKeyRenameOutcome.Taken, to);
        }

        var actions = new List<IChartAction>();
        if (definition is not null)
        {
            var renamed = replacement is not null
                ? new ToneDefinition { Key = to, Name = replacement.Name, Json = replacement.Json }
                : Renamed(definition, from, to);
            actions.Add(new UpsertToneDefinitionAction(project, definition.Key, renamed));
        }

        AddReferenceEdits(project, from, to, actions);
        // Tells the project's journal, which the save reads to move the player's tone edit and
        // instrument with the key. The node is named by its LabelKey, not by this child.
        actions.Add(new ToneKeyRenameJournalStep(project, from, to));
        return new ToneKeyRenamePlan(ToneKeyRenameOutcome.Ready, to, new CompositeAction(actions) { LabelKey = "History_RenameTone" });
    }

    /// <summary>Deletes <paramref name="key" /> everywhere: its definition, its tone changes, and
    /// every slot, starting tone and declared name that names it. Null when nothing names it.</summary>
    public static IChartAction? BuildDelete(ChartProject project, string key)
    {
        ArgumentNullException.ThrowIfNull(project);
        var target = (key ?? "").Trim();
        if (target.Length == 0) return null;

        var actions = new List<IChartAction>();
        if (FindDefinition(project, target) is { } definition)
            actions.Add(new UpsertToneDefinitionAction(project, definition.Key, null));

        foreach (var arrangement in project.Arrangements)
        {
            var doomed = arrangement.ToneChanges.Where(t => Same(t.ToneName, target)).ToList();
            if (doomed.Count > 0) actions.Add(new RemoveToneChangesAction(arrangement.ToneChanges, doomed));
            var before = ToneSlotAssignment.Capture(arrangement);
            var after = Replace(before, target, "");
            if (!before.SameAs(after)) actions.Add(new AssignArrangementTonesAction(arrangement, before, after));
        }

        var action = new CompositeAction(actions) { LabelKey = "History_DeleteTone" };
        return action.HasEffect ? action : null;
    }

    /// <summary>Every tone key the project defines or references, each once (first spelling wins),
    /// with whether it has a definition. Definitions come first, in their order.</summary>
    public static IReadOnlyList<(string Key, bool IsDefined)> Keys(ChartProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keys = new List<(string, bool)>();
        foreach (var definition in project.ToneDefinitions)
        {
            var key = definition.Key.Trim();
            if (key.Length > 0 && seen.Add(key)) keys.Add((key, true));
        }

        foreach (var key in References(project))
        {
            if (seen.Add(key)) keys.Add((key, false));
        }

        return keys;
    }

    /// <summary>Trim- and case-insensitive: the one way every tone-key comparison here is made.</summary>
    public static bool Same(string? a, string? b) =>
        string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    private static ToneDefinition? FindDefinition(ChartProject project, string key) =>
        project.ToneDefinitions.FirstOrDefault(d => Same(d.Key, key));

    private static bool IsReferenced(ChartProject project, string key) => References(project).Any(k => Same(k, key));

    private static IEnumerable<string> References(ChartProject project)
    {
        foreach (var arrangement in project.Arrangements)
        {
            foreach (var slot in new[]
                     {
                         arrangement.ToneBase, arrangement.ToneA, arrangement.ToneB, arrangement.ToneC,
                         arrangement.ToneD, arrangement.StartingTone
                     })
            {
                if (!string.IsNullOrWhiteSpace(slot)) yield return slot.Trim();
            }

            foreach (var name in arrangement.ToneNames)
            {
                if (!string.IsNullOrWhiteSpace(name)) yield return name.Trim();
            }

            foreach (var change in arrangement.ToneChanges)
            {
                if (!string.IsNullOrWhiteSpace(change.ToneName)) yield return change.ToneName.Trim();
            }
        }
    }

    private static void AddReferenceEdits(ChartProject project, string from, string to, List<IChartAction> actions)
    {
        foreach (var arrangement in project.Arrangements)
        {
            var before = ToneSlotAssignment.Capture(arrangement);
            var after = Replace(before, from, to);
            if (!before.SameAs(after)) actions.Add(new AssignArrangementTonesAction(arrangement, before, after));

            var changes = arrangement.ToneChanges.Where(t => Same(t.ToneName, from)).ToList();
            if (changes.Count > 0) actions.Add(new RetargetToneChangesAction(changes, to));
        }
    }

    /// <summary>The slot state with every <paramref name="from" /> swapped for <paramref name="to" />
    /// ("" clears it). ToneNames stays distinct: a rename onto a declared name keeps one entry.</summary>
    private static ToneSlotAssignment Replace(ToneSlotAssignment slots, string from, string to)
    {
        string Swap(string value) => Same(value, from) ? to : value;

        var names = new List<string>(slots.ToneNames.Count);
        foreach (var name in slots.ToneNames.Select(Swap))
        {
            if (name.Length > 0 && !names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
        }

        return new ToneSlotAssignment(Swap(slots.ToneBase), Swap(slots.ToneA), Swap(slots.ToneB), Swap(slots.ToneC),
            Swap(slots.ToneD), names, Swap(slots.StartingTone));
    }

    /// <summary>The definition under its new key. The display name follows only when it was the
    /// old key (a mirror of it, as a definition made from a key is); a real name is kept.</summary>
    private static ToneDefinition Renamed(ToneDefinition definition, string from, string to)
    {
        var nameFollows = string.IsNullOrWhiteSpace(definition.Name) || Same(definition.Name, from);
        var name = nameFollows ? to : definition.Name;
        return new ToneDefinition { Key = to, Name = name, Json = RekeyBody(definition.Json, to, nameFollows ? to : null) };
    }

    /// <summary>The manifest body with its <c>Key</c> (and, when given, <c>Name</c>) rewritten.
    /// Everything else stays as it was — numbers keep their raw text. A body that is not a JSON
    /// object is returned verbatim: it was opaque before and the definition's own key still answers.</summary>
    internal static string RekeyBody(string json, string key, string? name)
    {
        if (string.IsNullOrWhiteSpace(json)) return json;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root) return json;
            root["Key"] = key;
            if (name is not null && root.ContainsKey("Name")) root["Name"] = name;
            return root.ToJsonString();
        }
        catch (JsonException)
        {
            return json;
        }
    }
}

/// <summary>Points tone changes at another key; undo gives each one back ITS OWN former spelling
/// (a case-insensitive rename may have gathered "Clean" and "clean").</summary>
public sealed class RetargetToneChangesAction : IChartAction
{
    private readonly (ToneChange Change, string Before)[] _targets;
    private readonly string _after;

    public RetargetToneChangesAction(IEnumerable<ToneChange> changes, string after)
    {
        _targets = changes.Select(c => (c, c.ToneName)).ToArray();
        _after = after;
    }

    public bool HasEffect => _targets.Any(t => !string.Equals(t.Before, _after, StringComparison.Ordinal));

    public void Execute()
    {
        foreach (var (change, _) in _targets) change.ToneName = _after;
    }

    public void Undo()
    {
        foreach (var (change, before) in _targets) change.ToneName = before;
    }
}
