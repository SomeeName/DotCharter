using DotCharter.UI.ViewModels.Editor;
using DotCharter.Services.Editing.ChordShapes;

namespace DotCharter.UI.ViewModels.Panels;

/// <summary>
///     The chord template panel's "Generate fingerings" pass, as a plan of undoable
///     actions the panel commits as ONE history node: give every untemplated chord
///     and hand shape a template, finger every template that needs it (the
///     arrangement's style), and name every EMPTY name. A user-authored name or
///     fingering is never overwritten. Lifted out of
/// <see cref="ChordTemplateEditorViewModel" /> whole, which was at its
///     size budget; the behaviour is pinned through the panel's command by
///     <c>ChordTemplateNamingStyleTests</c> and <c>ChordTemplatePairFingeringStyleTests</c>.
/// </summary>
internal static class ChordTemplateFingeringBackfill
{
    public static List<IChartAction> Plan(Arrangement arrangement, IFingeringGenerationService fingeringService,
        ChartEditorViewModel editor, ChordNamingOptions naming)
    {
        var actions = new List<IChartAction>();

        // Step 1: every chord/handshape that has no template (or an invalid one)
        // gets one synthesised from its notes / its enclosed range. Reuse an
        // existing equivalent template when possible so we don't bloat the list.
        var newTemplates = new List<ChordTemplate>();
        var nextId = fingeringService.NextTemplateId(arrangement);

        int? EnsureTemplateForSeed(ChordTemplate seed)
        {
            var existing = fingeringService.FindEquivalentTemplate(arrangement, seed);
            if (existing != null) return existing.Id;
            var freshMatch = newTemplates.FirstOrDefault(t =>
                !t.Arpeggio &&
                t.Frets.Count == seed.Frets.Count &&
                t.Frets.All(kv => seed.Frets.TryGetValue(kv.Key, out var v) && v == kv.Value));
            if (freshMatch != null) return freshMatch.Id;
            seed.Id = nextId++;
            newTemplates.Add(seed);
            return seed.Id;
        }

        var chordIdReassignments = new List<(Chord chord, int oldId, int newId)>();
        var hsIdReassignments = new List<(HandShape hs, int oldId, int newId)>();

        bool TemplateExists(int id) => arrangement.ChordTemplates.Any(t => t.Id == id);

        foreach (var level in arrangement.Levels)
        {
            foreach (var chord in level.Chords)
            {
                if (TemplateExists(chord.TemplateId)) continue;
                var seed = fingeringService.BuildTemplateForChord(chord);
                if (seed == null) continue;
                var id = EnsureTemplateForSeed(seed);
                if (id == null || id == chord.TemplateId) continue;
                chordIdReassignments.Add((chord, chord.TemplateId, id.Value));
            }

            foreach (var hs in level.HandShapes)
            {
                if (TemplateExists(hs.TemplateId)) continue;
                var seed = fingeringService.BuildTemplateForRange(arrangement, level.Notes, level.Chords,
                    hs.Position, hs.EndPosition);
                if (seed == null) continue;
                var id = EnsureTemplateForSeed(seed);
                if (id == null || id == hs.TemplateId) continue;
                hsIdReassignments.Add((hs, hs.TemplateId, id.Value));
            }
        }

        if (newTemplates.Count > 0)
            actions.Add(new AddChordTemplatesAction(arrangement.ChordTemplates, newTemplates));
        if (chordIdReassignments.Count > 0)
            actions.Add(new SetTemplateIdsAction(chordIdReassignments, editor));
        if (hsIdReassignments.Count > 0)
            actions.Add(new SetHandShapeTemplateIdsAction(hsIdReassignments, editor));

        // Step 2: shape-recognised fingering for every template (existing or
        // just-built) that still needs one. We can't ask the service to walk
        // the arrangement again because the new templates have not been added
        // yet, so we union them in manually.
        actions.AddRange(FingerActions(arrangement, fingeringService, editor, newTemplates, chordIdReassignments,
            hsIdReassignments));

        // Step 3: fill EMPTY names from the pitch-based suggester in the same
        // pass — one click leaves every template fingered AND named. A
        // user-authored name is never overwritten, a name the charter chose to
        // leave out is never filled (NewChordNames.MayFill), and with "Name new
        // chords automatically" off nothing is named. Templates created in step 1
        // aren't in the arrangement yet, so they are named directly.
        foreach (var template in arrangement.ChordTemplates)
        {
            if (!NewChordNames.MayFill(template) || template.Frets.Count == 0 || template.IsPinned) continue;
            var name = NewChordNames.For(arrangement, template.Frets, naming);
            if (string.IsNullOrEmpty(name)) continue;
            actions.Add(new SetTemplateNameAction(template, template.Name, name, editor));
        }

        foreach (var template in newTemplates.Where(t => string.IsNullOrEmpty(t.Name) && t.Frets.Count > 0))
            template.Name = NewChordNames.For(arrangement, template.Frets, naming);

        return actions;
    }

    // Step 2's actions: the suggestion for every template that needs one,
    // or the fingers of the neighbour grip it is part of.
    private static List<IChartAction> FingerActions(Arrangement arrangement, IFingeringGenerationService fingeringService,
        ChartEditorViewModel editor, List<ChordTemplate> newTemplates,
        List<(Chord chord, int oldId, int newId)> chordIdReassignments,
        List<(HandShape hs, int oldId, int newId)> hsIdReassignments)
    {
        var existingNeedingFingering = fingeringService.FindTemplatesNeedingFingering(arrangement);
        var songStyle = ArrangementFingeringStyle.OfSong(editor.ActiveProject, arrangement);
        var suggested = new Dictionary<ChordTemplate, IReadOnlyDictionary<int, int>>(ReferenceEqualityComparer.Instance);
        foreach (var template in existingNeedingFingering.Concat(newTemplates))
        {
            // A pinned grip (authored by hand, or holding ghost gems) is never re-fingered.
            if (template.IsPinned) continue;
            var suggestion = fingeringService.SuggestFingeringFor(arrangement, template, songStyle);
            if (suggestion != null) suggested[template] = suggestion.Fingers;
        }

        // A grip that is part of the grip next to it takes that grip's fingers.
        var neighbours = NeighbourFingers(arrangement, newTemplates, chordIdReassignments, hsIdReassignments, suggested);
        return suggested.Select(kv => (IChartAction)new SetTemplateFingersAction(kv.Key,
            new Dictionary<int, int>(kv.Key.Fingers),
            new Dictionary<int, int>(neighbours.TryGetValue(kv.Key.Id, out var taken) ? taken : kv.Value),
            editor)).ToList();
    }

    // The neighbour fingers (NeighbourGripFingering) for the templates this pass
    // fingers, read over every level with step 1's repointing already in place.
    private static Dictionary<int, Dictionary<int, int>> NeighbourFingers(Arrangement arrangement,
        List<ChordTemplate> newTemplates, List<(Chord chord, int oldId, int newId)> chordIds,
        List<(HandShape hs, int oldId, int newId)> shapeIds,
        Dictionary<ChordTemplate, IReadOnlyDictionary<int, int>> suggested)
    {
        if (suggested.Count == 0) return [];
        var chordId = new Dictionary<Chord, int>(ReferenceEqualityComparer.Instance);
        foreach (var (chord, _, id) in chordIds) chordId[chord] = id;
        var shapeId = new Dictionary<HandShape, int>(ReferenceEqualityComparer.Instance);
        foreach (var (shape, _, id) in shapeIds) shapeId[shape] = id;
        var byId = ChordTemplateResolver.IndexById(arrangement.ChordTemplates.Concat(newTemplates));
        var sequences = arrangement.Levels.Select(l => (IReadOnlyList<int>)NeighbourGripFingering.GripSequence(l,
            c => chordId.TryGetValue(c, out var id) ? id : c.TemplateId,
            h => shapeId.TryGetValue(h, out var id) ? id : h.TemplateId));
        return NeighbourGripFingering.Plan(sequences, byId, suggested.ContainsKey,
            t => suggested.TryGetValue(t, out var f) ? f : t.Fingers, Math.Max(0, arrangement.Capo));
    }
}
