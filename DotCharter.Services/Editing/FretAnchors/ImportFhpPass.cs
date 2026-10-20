using DotCharter.Services.Editing.Fingering;

namespace DotCharter.Services.Editing.FretAnchors;

/// <summary>
///     The FHP pass a tab importer runs on an arrangement it has just built:
///     generate the anchors with every SUGGESTED chord fingering treated as free,
/// then let those chords' fingerings follow the anchors. An importer
///     has no undo history, so the plan is applied directly, and a template the
///     plan moved every user off is dropped. A grip the tab authored (Guitar Pro's
///     per-note fingers) is never re-fingered and still pins its anchor — also
///     where it happens to equal the suggestion, which the importer says through
///     <c>isAuthored</c>.
/// </summary>
public static class ImportFhpPass
{
    /// <summary>Replaces <paramref name="level" />'s anchors, repoints the chords
    /// (and the hand shapes over them) whose fingering followed, and drops the
    /// templates that left unused.</summary>
    /// <param name="arrangement">The arrangement being imported; owns the templates.</param>
    /// <param name="level">The level just built (it need not be in <c>Levels</c> yet).</param>
    /// <param name="bassArrangement">Selects the bass rules of the walk.</param>
    /// <param name="isAuthored">
    ///     Templates the file fingered (<c>GpAuthoredFingering.IsAuthoredTemplate</c>);
    ///     null when the source carries no fingering.
    /// </param>
    public static void Run(Arrangement arrangement, ChartLevel level, bool bassArrangement,
        Func<ChordTemplate, bool>? isAuthored = null)
    {
        // A pair the file left unfingered plays the way the file's own fingered
        // ones do, before anything reads the suggestion as "free".
        if (isAuthored != null) ArrangementFingeringStyle.FollowAuthored(arrangement, isAuthored);
        var freeGrip = ChordFingeringFollower.FreeGripResolver(arrangement, isAuthored);
        var anchors = new FretAnchorGenerationService()
            .GenerateAnchors(level, null, null, arrangement.ChordTemplates, arrangement.Capo, bassArrangement, freeGrip,
                arrangement);
        level.Anchors.Clear();
        foreach (var a in anchors) level.Anchors.Add(a);
        var plan = ChordFingeringFollower.Plan(arrangement, level, anchors, freeGrip, new FingeringGenerationService());
        plan.ApplyTo(arrangement);
        DropOrphanedTemplates(arrangement, level, plan);
    }

    // A template the plan moved EVERY chord and hand shape off would ship as an
    // unused chord in the export. Nothing else can refer to it yet (no undo
    // history, no editor), so it goes — and the ids above it close the gap, so
    // an arrangement whose ids were its list positions keeps them (the 3D
    // highway indexes templates by id). 0 is a real id, never "unset": only ids
    // of templates still in the list are renumbered, and -1 is never touched.
    private static void DropOrphanedTemplates(Arrangement arrangement, ChartLevel level, FingeringFollowPlan plan)
    {
        if (plan.IsEmpty) return;

        var levels = arrangement.Levels.Contains(level) ? arrangement.Levels.ToList() : [.. arrangement.Levels, level];
        var chords = levels.SelectMany(l => l.Chords).Concat(arrangement.NotationVoices.SelectMany(v => v.Chords)).ToList();
        var shapes = levels.SelectMany(l => l.HandShapes).ToList();

        var orphaned = plan.Chords.Select(c => c.OldId).Concat(plan.HandShapes.Select(s => s.OldId)).ToHashSet();
        foreach (var chord in chords) orphaned.Remove(chord.TemplateId);
        foreach (var shape in shapes) orphaned.Remove(shape.TemplateId);
        if (orphaned.Count == 0) return;

        for (var i = arrangement.ChordTemplates.Count - 1; i >= 0; i--)
        {
            if (orphaned.Contains(arrangement.ChordTemplates[i].Id)) arrangement.ChordTemplates.RemoveAt(i);
        }

        var removed = orphaned.Order().ToList();
        var remap = new Dictionary<int, int>();
        foreach (var template in arrangement.ChordTemplates)
        {
            if (template.Id < 0) continue;
            var below = removed.Count(id => id < template.Id);
            if (below == 0) continue;
            remap.TryAdd(template.Id, template.Id - below);
            template.Id -= below;
        }

        if (remap.Count == 0) return;
        foreach (var chord in chords)
        {
            if (remap.TryGetValue(chord.TemplateId, out var id)) chord.TemplateId = id;
        }

        foreach (var shape in shapes)
        {
            if (remap.TryGetValue(shape.TemplateId, out var id)) shape.TemplateId = id;
        }
    }
}
