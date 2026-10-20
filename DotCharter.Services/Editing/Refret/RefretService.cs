using DotCharter.Services.Editing.Fingering;

namespace DotCharter.Services.Editing.Refret;

/// <summary>
///     Orchestrates the refret: plans each level with <see cref="RefretPlanner" />,
///     writes the chosen assignments back, finds-or-creates chord templates for
///     re-fretted shapes (existing templates are NEVER mutated — other chords and
///     levels reference them), repoints hand shapes whose chords moved, and
///     regenerates FHP anchors for the affected range so the 3D camera and CDLC
///     export never see stale positions.
/// </summary>
public sealed class RefretService : IRefretService
{
    private readonly IFretAnchorGenerationService _anchorService;
    private readonly IFingeringGenerationService _fingeringService;

    public RefretService(
        IFretAnchorGenerationService anchorService,
        IFingeringGenerationService fingeringService)
    {
        _anchorService = anchorService;
        _fingeringService = fingeringService;
    }

    public RefretResult RefretArrangement(
        Arrangement arrangement,
        IList<BeatMarker> beats,
        RefretOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        var opt = options ?? RefretOptions.Default;
        var result = new RefretResult();
        foreach (var level in arrangement.Levels)
            result.Accumulate(RefretLevel(arrangement, level, beats, null, null, opt));
        return result;
    }

    public RefretResult RefretRange(
        Arrangement arrangement,
        ChartLevel level,
        IList<BeatMarker> beats,
        FractionalPosition rangeStart,
        FractionalPosition rangeEnd,
        RefretOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        ArgumentNullException.ThrowIfNull(level);
        var opt = options ?? RefretOptions.Default;
        return RefretLevel(arrangement, level, beats, rangeStart, rangeEnd, opt);
    }

    /// <summary>
    ///     Passes to a fixpoint: the collision sweep can revert a unit whose
    ///     blocker only moved away in the same pass, so re-planning may find the
    ///     freed spot. Every applied pass strictly improves the model cost (the
    ///     planner refuses zero-gain plans), so this converges — in practice in
    ///     one or two passes.
    /// </summary>
    private const int MaxPasses = 5;

    private RefretResult RefretLevel(
        Arrangement arrangement,
        ChartLevel level,
        IList<BeatMarker> beats,
        FractionalPosition? rangeStart,
        FractionalPosition? rangeEnd,
        RefretOptions opt)
    {
        var noteSnapshot = level.Notes
            .Select(n => (Note: n, n.StringIndex, n.Fret))
            .ToList();
        var chordSnapshot = level.Chords
            .Select(c => (Chord: c, c.TemplateId,
                Parts: c.ChordNotes.Select(cn => (cn.StringIndex, cn.Fret)).ToList()))
            .ToList();
        var templateCountBefore = arrangement.ChordTemplates.Count;

        var result = new RefretResult();
        for (var pass = 0; pass < MaxPasses; pass++)
        {
            var plan = RefretPlanner.Plan(arrangement, level, beats, rangeStart, rangeEnd, opt);
            if (pass == 0)
            {
                result.SoundsConsidered = plan.Units.Sum(u => (u.NoteMembers ?? u.ChordMembers)!.Count);
                result.CostBefore = plan.CostBefore;
                result.TravelBefore = plan.TravelBefore;
            }

            result.CostAfter = plan.CostAfter;
            result.TravelAfter = plan.TravelAfter;

            if (!ApplyPlan(arrangement, level, plan)) break;
        }

        result.NotesChanged = noteSnapshot
            .Count(s => s.Note.StringIndex != s.StringIndex || s.Note.Fret != s.Fret);
        result.ChordsChanged = chordSnapshot.Count(s =>
            s.Chord.TemplateId != s.TemplateId ||
            !s.Chord.ChordNotes.Select(cn => (cn.StringIndex, cn.Fret)).SequenceEqual(s.Parts));
        result.TemplatesAdded = arrangement.ChordTemplates.Count - templateCountBefore;

        if (result.HasChanges && level.Anchors.Count > 0)
            result.AnchorsRegenerated = RegenerateAnchors(arrangement, level, rangeStart, rangeEnd);

        return result;
    }

    // ─── Write-back ─────────────────────────────────────────────────────────

    /// <summary>Writes the chosen assignments back. Returns true when anything changed.</summary>
    private bool ApplyPlan(Arrangement arrangement, ChartLevel level, RefretPlan plan)
    {
        var changed = false;
        foreach (var unit in plan.Units)
        {
            if (unit.ChosenIndex == 0) continue;
            var candidate = unit.Candidates[unit.ChosenIndex];

            if (unit.NoteMembers != null)
            {
                for (var i = 0; i < unit.NoteMembers.Count; i++)
                {
                    var note = unit.NoteMembers[i].Note!;
                    var newFret = candidate.Frets![i];
                    if (note.StringIndex == candidate.StringIndex && note.Fret == newFret) continue;

                    if (note.SlideTo is > 0) note.SlideTo = candidate.SlideTos![i];
                    note.StringIndex = candidate.StringIndex;
                    note.Fret = newFret;
                    changed = true;
                }

                continue;
            }

            changed |= ApplyChordGroup(arrangement, level, unit, candidate);
        }

        return changed;
    }

    private bool ApplyChordGroup(
        Arrangement arrangement,
        ChartLevel level,
        RefretUnit unit,
        RefretCandidate candidate)
    {
        var map = candidate.ShapeMap!;
        var sourceTemplate = unit.ChordMembers![0].Template;
        var oldTemplateId = unit.ChordMembers[0].Chord!.TemplateId;

        ChordTemplate? newTemplate = null;
        if (sourceTemplate != null)
            newTemplate = FindOrCreateTemplate(arrangement, sourceTemplate, map);

        var changed = false;
        foreach (var member in unit.ChordMembers)
        {
            var chord = member.Chord!;

            foreach (var cn in chord.ChordNotes)
            {
                if (!map.TryGetValue(cn.StringIndex, out var target)) continue;
                if (cn.StringIndex == target.Str && cn.Fret == target.Fret) continue;

                if (cn.SlideTo is > 0) cn.SlideTo += (target.Fret - cn.Fret);
                cn.StringIndex = target.Str;
                cn.Fret = target.Fret;
                changed = true;
            }

            if (newTemplate != null && chord.TemplateId != newTemplate.Id)
            {
                chord.TemplateId = newTemplate.Id;
                changed = true;
            }
        }

        // Hand shapes over this group's chords follow the shape's new template.
        // Membership is the one shared rule HandShape.Covers (End is EXCLUSIVE).
        // This open-coded `<=`, so a chord that merely STARTS on the shape's end —
        // i.e. belongs to the NEXT shape — repointed the PREVIOUS shape's
        // TemplateId at its template.
        if (newTemplate != null && newTemplate.Id != oldTemplateId)
        {
            foreach (var shape in level.HandShapes)
            {
                if (shape.TemplateId != oldTemplateId) continue;
                var covers = unit.ChordMembers.Any(m => shape.Covers(m.Position));
                if (covers) shape.TemplateId = newTemplate.Id;
            }
        }

        return changed;
    }

    /// <summary>
    ///     Existing templates are never mutated in place — other chords (and
    ///     other difficulty levels) may reference them. A matching template
    ///     (same name, arpeggio flag and frets) is reused; otherwise a new one
    ///     is appended with a generated fingering.
    /// </summary>
    private ChordTemplate FindOrCreateTemplate(
        Arrangement arrangement,
        ChordTemplate source,
        Dictionary<int, (int Str, int Fret)> map)
    {
        var newFrets = new Dictionary<int, int>(source.Frets.Count);
        foreach (var kv in source.Frets)
        {
            var target = map[kv.Key];
            newFrets[target.Str] = target.Fret;
        }

        // The arpeggio half of the key is BOTH flags (ArpeggioFlavour): the mint
        // below already carries both, so comparing only `Arpeggio` could hand
        // the refretted chord an existing template whose `ForceArpeggioInRs`
        // disagrees — flipping its `-arp` export name behind the user's back.
        var flavour = ArpeggioFlavour.Of(source);
        foreach (var existing in arrangement.ChordTemplates)
        {
            // Nop rides with the name: refretting keeps the chord's
            // authored display, and Nop is an exported part of it.
            if (existing.Name == source.Name && existing.NopInRs == source.NopInRs && flavour.Matches(existing) &&
                existing.HasFrets(newFrets))
            {
                return existing;
            }
        }

        var nextId = arrangement.ChordTemplates.Count;
        if (arrangement.ChordTemplates.Any(t => t.Id >= nextId))
            nextId = arrangement.ChordTemplates.Max(t => t.Id) + 1;

        var created = new ChordTemplate
        {
            Id = nextId,
            Name = source.Name,
            NopInRs = source.NopInRs,
            Frets = newFrets
        };
        flavour.ApplyTo(created); // both flags, or the mint carries half a flavour
        ApplyFingering(created, arrangement);

        arrangement.ChordTemplates.Add(created);
        return created;
    }

    private void ApplyFingering(ChordTemplate template, Arrangement arrangement)
    {
        var suggested = _fingeringService.SuggestFingeringFor(arrangement, template);
        if (suggested != null && suggested.Fingers.Count > 0)
        {
            template.Fingers = new Dictionary<int, int>(suggested.Fingers);
            return;
        }

        // Fallback: barre-aware positional fingering — the lowest fret takes
        // finger 1 (shared across a barre), higher frets step up to the pinky.
        var fretted = template.Frets.Where(kv => kv.Value > 0).ToList();
        if (fretted.Count == 0) return;
        var minFret = fretted.Min(kv => kv.Value);
        foreach (var kv in fretted)
            template.Fingers[kv.Key] = Math.Clamp(kv.Value - minFret + 1, 1, 4);
    }

    // ─── Anchor regeneration ────────────────────────────────────────────────

    private int RegenerateAnchors(
        Arrangement arrangement,
        ChartLevel level,
        FractionalPosition? rangeStart,
        FractionalPosition? rangeEnd)
    {
        var generated = _anchorService
            .GenerateAnchors(level, rangeStart, rangeEnd, arrangement.ChordTemplates, arrangement.Capo,
                ArrangementInstrument.IsBassPart(arrangement), tuning: arrangement)
            .ToList();
        if (generated.Count == 0) return 0;

        var stale = level.Anchors
            .Where(a => (!rangeStart.HasValue || a.Position >= rangeStart.Value) &&
                        (!rangeEnd.HasValue || a.Position <= rangeEnd.Value))
            .ToList();
        foreach (var anchor in stale) level.Anchors.Remove(anchor);

        // INSERT SORTED, never append. For RefretArrangement (both bounds null)
        // every anchor is removed first, so appending happened to be ordered —
        // but for RefretRange (the selection path) the out-of-range anchors keep
        // their places at the FRONT and the regenerated ones landed at the END,
        // leaving level.Anchors out of order unless the selection was at the very
        // end of the chart. TimelineMath.GetVisibleRange binary-searches this
        // collection, so anchors past the splice point vanished from (or
        // duplicated in) the 2D/3D viewport; ChartSpacingEnforcer only re-sorts
        // on the compatibility path, which the highway edit path does not run, so
        // the disorder survived until the next save/load — and
        // RemoveRedundantAnchors compares CONSECUTIVE anchors, so on an unsorted
        // list it deletes the wrong ones.
        foreach (var anchor in generated) InsertSorted(level.Anchors, anchor);
        return generated.Count;
    }

    private static void InsertSorted(IList<FretAnchor> anchors, FretAnchor anchor)
    {
        var index = 0;
        while (index < anchors.Count && anchors[index].Position < anchor.Position) index++;
        anchors.Insert(index, anchor);
    }
}
