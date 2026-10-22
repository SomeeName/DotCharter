using DotCharter.Services.State;

namespace DotCharter.Services.Editing.Improvement;

/// <summary>
///     The chord templates a whole-arrangement LEVEL operation — Squash levels, Generate levels —
/// left with no user, and which of them it may drop. Graded chord reduction
///     mints a template per reduced grip; a squash throws the levels that played them away and a
///     regeneration replaces them, so without this the arrangement collects templates nothing
///     plays.
///     <para>
///         <b>The rule.</b> <see cref="Capture" /> runs on the arrangement BEFORE the operation,
///         <see cref="Find" /> on the same arrangement after it. A template is dropped only when
///         nothing uses it afterwards AND either
///         (a) the operation itself minted it (it was not declared before) — the reduction's own
///         grips, named or not; or
///         (b) something used it before, the operation took that use away, and it has NO name.
///         A named template that lost its last use, and any template that had no user before the
///         operation (an import's spare, a shape typed in the template editor and not placed yet),
///         stays: neither is the operation's to judge. Reduced grips are minted unnamed, as
/// official ladders leave them, which is what (b) relies on — no persisted marker.
///     </para>
///     <para>
///         <b>A use</b> is a chord or hand shape on ANY level, or a chord of a notation voice,
///         whose <c>TemplateId</c> resolves to the template through
///         <see cref="ChordTemplateResolver.IndexById" /> (first template with that id wins).
///     </para>
///     <para>
///         <b>Ids stay POSITIONS.</b> Ids are not positions in the editor, but the <c>.dcproj</c> is
///         positional: the parser numbers templates by their index. <see cref="Apply" /> keeps the
///         live chart that way: survivors are renumbered to their final positions and every user is
///         remapped, and it does nothing at all on an arrangement whose ids were not positional to
///         begin with. (<c>RscpTemplateManager.RegenerateTemplates</c> renumbers by
///         index on save and remaps the users, so a gap cannot re-point the let-ring bars after
///         it. The positional guard stays because <see cref="RemapUsers" /> is
///         keyed by OLD ID — two templates sharing an id would collide in it — and leaves a dangling
///         id alone on the strength of the ids being positions.)
///     </para>
///     <para>
///         By INSTANCE throughout (<see cref="ChordTemplate" /> has value equality —
///         <see cref="ChordTemplateCollectionOps" />).
///     </para>
/// </summary>
public sealed class LevelTemplateOrphans
{
    private readonly List<ChordTemplate> _declared;
    private readonly HashSet<ChordTemplate> _declaredSet;
    private readonly HashSet<ChordTemplate> _usedBefore;

    private LevelTemplateOrphans(List<ChordTemplate> declared, HashSet<ChordTemplate> usedBefore)
    {
        _declared = declared;
        _declaredSet = new HashSet<ChordTemplate>(declared, ReferenceEqualityComparer.Instance);
        _usedBefore = usedBefore;
    }

    /// <summary>Records what <paramref name="arrangement" /> declares and uses, before the operation.</summary>
    public static LevelTemplateOrphans Capture(Arrangement arrangement)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        return new LevelTemplateOrphans([.. arrangement.ChordTemplates], UsedTemplates(arrangement));
    }

    /// <summary>
    ///     The templates of <paramref name="arrangement" /> — the one passed to <see cref="Capture" />,
    ///     after the operation — that the rule drops, in collection order. Changes nothing.
    /// </summary>
    public List<ChordTemplate> Find(Arrangement arrangement)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        var used = UsedTemplates(arrangement);
        var orphans = new List<ChordTemplate>();
        foreach (var template in arrangement.ChordTemplates)
        {
            if (used.Contains(template)) continue;
            var minted = !_declaredSet.Contains(template);
            var lostItsUsers = _usedBefore.Contains(template) && string.IsNullOrWhiteSpace(template.Name);
            if (minted || lostItsUsers) orphans.Add(template);
        }

        return orphans;
    }

    /// <summary>
    ///     Applies the rule to <paramref name="after" /> (the arrangement passed to
    ///     <see cref="Capture" />, after the operation, about to be restored into a live arrangement
    ///     whose templates are <paramref name="liveTemplates" />), and returns the LIVE instances
    ///     the caller removes once that restore has run.
    ///     <para>
    ///         An orphan the operation minted leaves <paramref name="after" /> outright: it sits
    ///         behind every declared template and has no live instance. A DECLARED orphan stays in
    ///         its slot with the <c>-1</c> sentinel id ("parked"), so <paramref name="after" /> still
    ///         lines up index for index with the live list the restore reconciles it into — taking
    ///         it out of the middle would shift every later template's values into a different live
    ///         instance — and its live instance is what this returns. Every other template is
    ///         renumbered to the position it ends up at, and every chord and hand shape on every
    ///         level, and every notation-voice chord, is remapped to match.
    ///     </para>
    ///     <para>
    ///         Does nothing (and returns nothing) unless the ids were positional before and after
    ///         the operation, the operation kept the declared templates first and in order, and
    ///         <paramref name="liveTemplates" /> is the list <see cref="Capture" /> saw (same count,
    ///         same ids). Anything else is a chart this rule was not written for, and a template
    ///         kept is never a loss.
    ///     </para>
    /// </summary>
    public List<ChordTemplate> Apply(Arrangement after, IReadOnlyList<ChordTemplate> liveTemplates)
    {
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(liveTemplates);
        if (!CanApply(after, liveTemplates)) return [];

        var orphans = Find(after);
        if (orphans.Count == 0) return [];
        var orphanSet = new HashSet<ChordTemplate>(orphans, ReferenceEqualityComparer.Instance);
        foreach (var orphan in orphans)
        {
            if (!_declaredSet.Contains(orphan))
                ChordTemplateCollectionOps.RemoveInstance(after.ChordTemplates, orphan);
        }

        var newIds = new Dictionary<int, int>();
        var parked = new List<ChordTemplate>();
        var next = 0;
        for (var i = 0; i < after.ChordTemplates.Count; i++)
        {
            var template = after.ChordTemplates[i];
            if (orphanSet.Contains(template))
            {
                // Declared, so i is its index in the live list too (CanApply).
                parked.Add(liveTemplates[i]);
                template.Id = -1;
                continue;
            }

            newIds[template.Id] = next;
            template.Id = next++;
        }

        RemapUsers(after, newIds);
        return parked;
    }

    /// <summary>Every template instance a chord or hand shape of <paramref name="arrangement" /> resolves to.</summary>
    public static HashSet<ChordTemplate> UsedTemplates(Arrangement arrangement)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        var byId = ChordTemplateResolver.IndexById(arrangement.ChordTemplates);
        var used = new HashSet<ChordTemplate>(ReferenceEqualityComparer.Instance);
        foreach (var level in arrangement.Levels)
        {
            foreach (var chord in level.Chords) Use(chord.TemplateId);
            foreach (var handShape in level.HandShapes) Use(handShape.TemplateId);
        }

        foreach (var voice in arrangement.NotationVoices)
        {
            foreach (var chord in voice.Chords) Use(chord.TemplateId);
        }

        return used;

        void Use(int templateId)
        {
            if (byId.TryGetValue(templateId, out var template)) used.Add(template);
        }
    }

    private bool CanApply(Arrangement after, IReadOnlyList<ChordTemplate> liveTemplates)
    {
        if (liveTemplates.Count != _declared.Count || after.ChordTemplates.Count < _declared.Count) return false;
        for (var i = 0; i < _declared.Count; i++)
        {
            if (!ReferenceEquals(after.ChordTemplates[i], _declared[i]) || liveTemplates[i].Id != i) return false;
        }

        for (var i = 0; i < after.ChordTemplates.Count; i++)
        {
            if (after.ChordTemplates[i].Id != i) return false;
        }

        return true;
    }

    /// <summary>
    ///     Repoints every user through <paramref name="newIds" />. An id with no entry is left alone:
    ///     a negative one is the sentinel, and a dangling one is at least the old template count —
    ///     the ids were positional — so it cannot meet a renumbered (smaller) id.
    /// </summary>
    private static void RemapUsers(Arrangement arrangement, Dictionary<int, int> newIds)
    {
        foreach (var level in arrangement.Levels)
        {
            foreach (var chord in level.Chords) chord.TemplateId = Remapped(chord.TemplateId);
            foreach (var handShape in level.HandShapes) handShape.TemplateId = Remapped(handShape.TemplateId);
        }

        foreach (var voice in arrangement.NotationVoices)
        {
            foreach (var chord in voice.Chords) chord.TemplateId = Remapped(chord.TemplateId);
        }

        int Remapped(int templateId) => newIds.TryGetValue(templateId, out var id) ? id : templateId;
    }
}
