using DotCharter.Core.Domain.Project;
using DotCharter.Services.Editing.Tones;
using DotCharter.Services.Export.Rocksmith;
using DotCharter.Services.State;
using DotCharter.Tone.Audio.Dsp.Tones;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Logging;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>What <see cref="ToneDesignerCommits.PlanEdit" /> decided: the history action, the key the tone ends up under and its body.</summary>
internal sealed record ToneEditPlan(IChartAction Action, string Key, string Json, bool Renamed);

/// <summary>The history actions behind the Tone designer's edits: one tone's edit, and the arrangements' slot rows.</summary>
internal static class ToneDesignerCommits
{
    /// <summary>Ticks of a drag or of typing fold into one undo step by this verb.</summary>
    public const string EditVerb = "tone-edit";

    public const string SlotVerb = "tone-slots";

    /// <summary>
    ///     The action for an edited tone. A same-key edit is one upsert, mergeable when it is a tick of a gesture.
    ///     A KEY edit renames the tone everywhere it is stored, body included, as ONE undo step; a refused
    ///     rename leaves the edit under the old key.
    /// </summary>
    public static ToneEditPlan PlanEdit(ChartProject project, string oldKey, string key, ToneDefinition definition,
        bool tick, ToneEditorViewModel editor, IAppLogger logger)
    {
        if (string.Equals(key, oldKey, StringComparison.Ordinal))
            return new ToneEditPlan(new UpsertToneDefinitionAction(project, oldKey, definition, tick ? EditVerb : ""), key, definition.Json, false);

        var plan = ToneKeyEdits.PlanRename(project, oldKey, key, definition);
        if (plan.IsReady) return new ToneEditPlan(plan.Action!, key, definition.Json, true);

        logger.Warning(AppLogCategory.Project, $"Tone designer: renaming '{oldKey}' to '{key}' refused ({plan.Outcome}); the edit stays under the old key.");
        editor.ShowKey(oldKey);
        var kept = new ToneDefinition { Key = oldKey, Name = definition.Name, Json = RsToneJsonWriter.Write(editor.BuildTone(oldKey)) };
        return new ToneEditPlan(new UpsertToneDefinitionAction(project, oldKey, kept), oldKey, kept.Json, false);
    }

    /// <summary>Edited slot rows as ONE action; an untouched row is never rewritten, so imported orderings survive. Null when nothing differs.</summary>
    public static IChartAction? SlotAction(IEnumerable<ToneDesignerArrangementItem> rows)
    {
        var actions = new List<IChartAction>();
        foreach (var row in rows)
        {
            var a = row.Arrangement;
            if (row.ToneBase.Trim() == a.ToneBase && row.ToneA.Trim() == a.ToneA && row.ToneB.Trim() == a.ToneB &&
                row.ToneC.Trim() == a.ToneC && row.ToneD.Trim() == a.ToneD)
            {
                continue;
            }

            if (row.ShowsResolvedSlots()) continue;
            actions.Add(new AssignArrangementTonesAction(a, ToneSlotAssignment.Capture(a),
                ToneSlotAssignment.FromSlots(row.ToneBase, row.ToneA, row.ToneB, row.ToneC, row.ToneD), SlotVerb));
        }

        if (actions.Count == 0) return null;
        return actions.Count == 1 ? actions[0] : new CompositeAction(actions) { LabelKey = "History_AssignTones" };
    }

    /// <summary>True when the tone list shows exactly the project's definitions (key, name, body), in order.</summary>
    public static bool Matches(IReadOnlyList<ToneDefinition> definitions, IReadOnlyList<ProjectToneItem> items) =>
        definitions.Count == items.Count && definitions.Select((d, i) => (d, i)).All(x =>
            x.d.Key == items[x.i].Key && x.d.Name == items[x.i].Name && x.d.Json == items[x.i].Json);

    public static bool SameKeys(IReadOnlyList<ToneDefinition> definitions, IReadOnlyList<ProjectToneItem> items) =>
        definitions.Count == items.Count && definitions.Select((d, i) => d.Key == items[i].Key).All(x => x);

    /// <summary>Copies name and body of same-keyed definitions into the listed items; false (nothing touched) when the keys differ.</summary>
    public static bool UpdateInPlace(IReadOnlyList<ToneDefinition> definitions, IReadOnlyList<ProjectToneItem> items)
    {
        if (!SameKeys(definitions, items)) return false;
        for (var i = 0; i < items.Count; i++) (items[i].Name, items[i].Json) = (definitions[i].Name, definitions[i].Json);
        return true;
    }

    /// <summary>A row differs from what its arrangement resolves to (an undo, another writer), or the arrangement count changed.</summary>
    public static bool RowsStale(ChartProject project, IReadOnlyList<ToneDesignerArrangementItem> rows) =>
        rows.Count != Slotted(project).Count() || rows.Any(r => !r.ShowsResolvedSlots());

    /// <summary>The per-arrangement slot rows, from the arrangements' CURRENT tone fields.</summary>
    public static IEnumerable<ToneDesignerArrangementItem> BuildRows(ChartProject project, IReadOnlyList<ProjectToneItem> tones)
    {
        foreach (var arrangement in Slotted(project))
        {
            var (toneBase, slots) = RsToneSlots.Resolve(arrangement);
            yield return new ToneDesignerArrangementItem
            {
                Name = arrangement.Name,
                TypeLabel = ArrangementTypeLabel.For(arrangement.Name, arrangement.ArrangementType.ToString()),
                Arrangement = arrangement,
                Tones = tones,
                ToneBase = toneBase,
                ToneA = slots.ElementAtOrDefault(0) ?? "",
                ToneB = slots.ElementAtOrDefault(1) ?? "",
                ToneC = slots.ElementAtOrDefault(2) ?? "",
                ToneD = slots.ElementAtOrDefault(3) ?? ""
            };
        }
    }

    private static IEnumerable<Arrangement> Slotted(ChartProject project) => project.Arrangements.Where(a => a.ArrangementType != ArrangementType.Vocal);
}
