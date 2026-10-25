using DotCharter.Services.State;

namespace DotCharter.Services.Editing.Phrases;

/// <summary>
///     Rename and delete for ONE phrase of an arrangement, in BOTH phrase stores
///     (<see cref="PhraseMarkers" />): the <see cref="EventPoint.Phrase" /> markers the editor
///     authors, and the <see cref="Arrangement.Phrases" /> + <see cref="Arrangement.PhraseIterations" />
///     an imported chart carries, plus the name-keyed <see cref="Arrangement.PhraseInfos" />.
///     A name matches as <see cref="PhraseMarkers" /> reads it: trimmed, case-insensitive. Each
///     plan is one undo node; null when there is nothing to do.
/// </summary>
public static class PhraseEdits
{
    public const string RenameLabelKey = "History_Phrases_Rename";
    public const string DeleteLabelKey = "History_Phrases_Delete";

    /// <summary>
    ///     Renames <paramref name="oldName" /> to <paramref name="newName" />. Renaming onto a phrase
    ///     that already exists merges the two: the iterations join it, and its metadata wins.
    /// </summary>
    public static IChartAction? PlanRename(Arrangement arrangement, string oldName, string newName)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        var target = newName?.Trim() ?? "";
        if (target.Length == 0 || string.Equals(oldName, target, StringComparison.Ordinal)) return null;
        var action = new EditPhrasesAction(arrangement, a => Rename(a, oldName, target));
        return action.HasEffect ? new CompositeAction([action]) { LabelKey = RenameLabelKey } : null;
    }

    /// <summary>
    ///     Deletes every iteration of <paramref name="name" />: its explicit iterations and list entry,
    ///     and its markers — a marker that carries a section or events keeps those and loses only
    ///     the phrase.
    /// </summary>
    public static IChartAction? PlanDelete(Arrangement arrangement, string name)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        var action = new EditPhrasesAction(arrangement, a => Delete(a, name));
        return action.HasEffect ? new CompositeAction([action]) { LabelKey = DeleteLabelKey } : null;
    }

    private static bool Same(string? phrase, string name) =>
        phrase != null && string.Equals(phrase.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase);

    private static void Rename(Arrangement arrangement, string oldName, string newName)
    {
        var oldId = arrangement.Phrases.FindIndex(p => Same(p, oldName));
        if (oldId >= 0)
        {
            var newId = arrangement.Phrases.FindIndex(p => Same(p, newName));
            if (newId >= 0 && newId != oldId)
            {
                foreach (var iteration in arrangement.PhraseIterations.Where(i => i.PhraseId == oldId))
                    iteration.PhraseId = newId;
                RemovePhraseAt(arrangement, oldId);
            }
            else
            {
                arrangement.Phrases[oldId] = newName;
            }
        }

        foreach (var marker in arrangement.EventPoints.Where(e => Same(e.Phrase, oldName)))
            marker.Phrase = newName;

        var oldKey = arrangement.PhraseInfos.Keys.FirstOrDefault(k => Same(k, oldName));
        if (oldKey == null) return;
        var info = arrangement.PhraseInfos[oldKey];
        arrangement.PhraseInfos.Remove(oldKey);
        if (!arrangement.PhraseInfos.Keys.Any(k => Same(k, newName))) arrangement.PhraseInfos[newName] = info;
    }

    private static void Delete(Arrangement arrangement, string name)
    {
        var id = arrangement.Phrases.FindIndex(p => Same(p, name));
        if (id >= 0)
        {
            foreach (var iteration in arrangement.PhraseIterations.Where(i => i.PhraseId == id).ToList())
                arrangement.PhraseIterations.Remove(iteration);
            RemovePhraseAt(arrangement, id);
        }

        foreach (var marker in arrangement.EventPoints.Where(e => Same(e.Phrase, name)).ToList())
        {
            if (string.IsNullOrEmpty(marker.Section) && string.IsNullOrEmpty(marker.Events))
                arrangement.EventPoints.Remove(marker);
            else
                marker.Phrase = "";
        }

        foreach (var key in arrangement.PhraseInfos.Keys.Where(k => Same(k, name)).ToList())
            arrangement.PhraseInfos.Remove(key);
    }

    /// <summary>Takes a phrase out of the list; the iterations after it move down one id.</summary>
    private static void RemovePhraseAt(Arrangement arrangement, int id)
    {
        arrangement.Phrases.RemoveAt(id);
        foreach (var iteration in arrangement.PhraseIterations.Where(i => i.PhraseId > id))
            iteration.PhraseId--;
    }
}
