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
    /// <param name="barlines">
    ///     Every barline of the score in ascending order, the end of the last measure
    ///     included: repeated measures get one anchor pattern (<see cref="RepeatedMeasureAnchors" />).
    ///     Empty when the caller has no measures.
    /// </param>
    /// <param name="isAuthored">
    ///     Templates the file fingered (<c>GpAuthoredFingering.IsAuthoredTemplate</c>);
    ///     null when the source carries no fingering.
    /// </param>
    /// <param name="songStyle">
    ///     The style the file's OTHER tracks on the same instrument state
    ///     (<see cref="SongStyles" />): a pair this arrangement has no majority for
    ///     follows it. Null: the arrangement alone decides.
    /// </param>
    public static void Run(Arrangement arrangement, ChartLevel level, bool bassArrangement,
        IReadOnlyList<FractionalPosition> barlines, Func<ChordTemplate, bool>? isAuthored = null, ArrangementFingeringStyle? songStyle = null)
    {
        // A pair the file left unfingered plays the way the file's own fingered
        // ones do (or, with no majority there, the song's other tracks), before
        // anything reads the suggestion as "free".
        if (isAuthored != null || songStyle != null)
            ArrangementFingeringStyle.FollowAuthored(arrangement, isAuthored ?? (_ => false), songStyle);
        var freeGrip = ChordFingeringFollower.FreeGripResolver(arrangement, isAuthored,
            ArrangementFingeringStyle.HintsFromOthers(arrangement, songStyle));
        var anchors = new FretAnchorGenerationService()
            .GenerateAnchors(level, null, null, arrangement.ChordTemplates, arrangement.Capo, bassArrangement, freeGrip,
                arrangement);
        anchors = RepeatedMeasureAnchors.Harmonize(level, anchors, barlines, arrangement.ChordTemplates, arrangement.Capo);
        level.Anchors.Clear();
        foreach (var a in anchors) level.Anchors.Add(a);
        var plan = ChordFingeringFollower.Plan(arrangement, level, anchors, freeGrip, new FingeringGenerationService());
        plan.ApplyTo(arrangement);
        var neighbours = TakeNeighbourFingers(arrangement, level, freeGrip);
        neighbours.ApplyTo(arrangement);
        DropOrphanedTemplates(arrangement, level, new FingeringFollowPlan([],
            [.. plan.Chords, .. neighbours.Chords], [.. plan.HandShapes, .. neighbours.HandShapes]));
    }

    // A free grip that is part of the grip next to it takes that grip's
    // fingers (NeighbourGripFingering), on a template of its own: every chord
    // and hand shape of the level playing it moves over.
    private static FingeringFollowPlan TakeNeighbourFingers(Arrangement arrangement, ChartLevel level,
        Func<ChordTemplate, ChordTemplate?> freeGrip)
    {
        var byId = ChordTemplateResolver.IndexById(arrangement.ChordTemplates);
        var refinger = NeighbourGripFingering.Plan([NeighbourGripFingering.GripSequence(level)], byId,
            t => freeGrip(t) != null, t => t.Fingers, Math.Max(0, arrangement.Capo));
        if (refinger.Count == 0) return FingeringFollowPlan.Empty;

        var fingering = new FingeringGenerationService();
        var nextId = fingering.NextTemplateId(arrangement);
        var minted = new List<ChordTemplate>();
        var newIds = new Dictionary<int, int>();
        foreach (var (id, fingers) in refinger)
        {
            var variant = byId[id].Clone();
            variant.Id = -1;
            variant.Fingers.Clear();
            foreach (var (stringIndex, finger) in fingers) variant.Fingers[stringIndex] = finger;
            var existing = fingering.FindIdenticalTemplate(arrangement, variant)
                           ?? minted.FirstOrDefault(m => m.Unnamed == variant.Unnamed && m.Equals(variant));
            if (existing == null)
            {
                variant.Id = nextId++;
                minted.Add(variant);
                existing = variant;
            }

            newIds[id] = existing.Id;
        }

        var chords = level.Chords.Where(c => newIds.ContainsKey(c.TemplateId))
            .Select(c => (c, c.TemplateId, newIds[c.TemplateId])).ToList();
        var shapes = level.HandShapes.Where(h => newIds.ContainsKey(h.TemplateId))
            .Select(h => (h, h.TemplateId, newIds[h.TemplateId])).ToList();
        return new FingeringFollowPlan(minted, chords, shapes);
    }

    /// <summary>
    ///     The song style for each of a file's imported arrangements, before any
    ///     of their FHP passes runs: every arrangement first follows its own
    ///     authored fingers (<see cref="ArrangementFingeringStyle.FollowAuthored" />),
    ///     then each one's style is read from the OTHERS on the same instrument
    ///     (<see cref="ArrangementFingeringStyle.OfOtherArrangements" />). Reading
    ///     them all up front keeps the result independent of the track order.
    /// </summary>
    /// <param name="song">Each imported arrangement with its authored-template test (null: none).</param>
    public static IReadOnlyList<ArrangementFingeringStyle> SongStyles(
        IReadOnlyList<(Arrangement Arrangement, Func<ChordTemplate, bool>? IsAuthored)> song)
    {
        ArgumentNullException.ThrowIfNull(song);
        foreach (var (arrangement, isAuthored) in song)
        {
            if (isAuthored != null) ArrangementFingeringStyle.FollowAuthored(arrangement, isAuthored);
        }

        var arrangements = song.Select(s => s.Arrangement).ToList();
        return [.. arrangements.Select(a => ArrangementFingeringStyle.OfOtherArrangements(arrangements, a))];
    }

    // A template the plan moved EVERY chord and hand shape off would ship as an
    // unused chord in the export. Nothing else can refer to it yet (no undo
    // history, no editor), so it goes — and the ids above it close the gap, so
    // an arrangement whose ids were its list positions keeps them (the 3D
    // highway indexes templates by id). 0 is a real id, never "unset": only ids
    // of templates still in the list are renumbered, and -1 is never touched.
    private static void DropOrphanedTemplates(Arrangement arrangement, ChartLevel level, FingeringFollowPlan plan)
    {
        if (plan.Chords.Count == 0 && plan.HandShapes.Count == 0) return;

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
