namespace DotCharter.Services.Editing.Fingering;

public sealed class FingeringGenerationService : IFingeringGenerationService
{
    public IReadOnlyList<ChordTemplate> FindTemplatesNeedingFingering(Arrangement arrangement) => arrangement.ChordTemplates.Where(NeedsFingering).ToList();

    public ChordTemplate? SuggestFingeringFor(ChordTemplate template, int stringCount)
    {
        if (!HasFrettedStrings(template)) return null;

        var clone = template.Clone();
        ChordTemplateFingerSetter.SetSuggestedFingers(clone, Math.Max(1, stringCount));

        return MapEquality.SameEntries(clone.Fingers, template.Fingers) ? null : clone;
    }

    public ChordTemplate? SuggestFingeringFor(Arrangement arrangement, ChordTemplate template)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        if (!HasFrettedStrings(template)) return null;

        var clone = template.Clone();
        ArrangementFingeringStyle.SetSuggestedFingers(arrangement, clone, StringCountOf(arrangement));

        return MapEquality.SameEntries(clone.Fingers, template.Fingers) ? null : clone;
    }

    private static int StringCountOf(Arrangement arrangement) =>
        Math.Max(1, arrangement.Tuning.Length > 0 ? arrangement.Tuning.Length : arrangement.StringCount);

    public IReadOnlyList<ChordTemplate> FindMatchingTemplatesNeedingFingering(
        Arrangement arrangement, ChordTemplate source)
    {
        if (!HasFingeringForAllFrettedStrings(source)) return Array.Empty<ChordTemplate>();

        return arrangement.ChordTemplates
            .Where(t => !ReferenceEquals(t, source))
            .Where(t => t.HasFrets(source.Frets))
            .Where(NeedsFingering)
            .ToList();
    }

    private static bool NeedsFingering(ChordTemplate template)
    {
        // Deliberately fingerless (EOF's eflag): nothing is missing.
        if (template.Fingerless || !HasFrettedStrings(template)) return false;
        foreach (var (stringIdx, fret) in template.Frets)
        {
            if (fret <= 0) continue;
            if (!template.Fingers.ContainsKey(stringIdx)) return true;
        }
        return false;
    }

    private static bool HasFrettedStrings(ChordTemplate template)
    {
        foreach (var fret in template.Frets.Values)
        {
            if (fret > 0)
                return true;
        }

        return false;
    }

    private static bool HasFingeringForAllFrettedStrings(ChordTemplate template)
    {
        var anyFretted = false;
        foreach (var (stringIdx, fret) in template.Frets)
        {
            if (fret <= 0) continue;
            anyFretted = true;
            if (!template.Fingers.ContainsKey(stringIdx)) return false;
        }
        return anyFretted;
    }

    public ChordTemplate? BuildTemplateForChord(Chord chord)
    {
        if (chord.ChordNotes.Count == 0) return null;
        var template = new ChordTemplate { Name = string.Empty };
        foreach (var cn in chord.ChordNotes)
        {
            if (!template.Frets.ContainsKey(cn.StringIndex))
                template.Frets[cn.StringIndex] = cn.Fret;
        }

        return template.Frets.Count == 0 ? null : template;
    }

    public ChordTemplate? BuildTemplateForRange(Arrangement arrangement,
        IEnumerable<GuitarNote> notes, IEnumerable<Chord> chords,
        FractionalPosition start, FractionalPosition end)
    {
        var template = new ChordTemplate { Name = string.Empty };

        foreach (var n in notes.Where(n => n.Position >= start && n.Position < end))
        {
            if (!template.Frets.ContainsKey(n.StringIndex))
                template.Frets[n.StringIndex] = n.Fret;
        }

        foreach (var c in chords.Where(c => c.Position >= start && c.Position < end))
        {
            var existing = arrangement.ChordTemplates.FirstOrDefault(t => t.Id == c.TemplateId);
            if (existing != null)
            {
                foreach (var kv in existing.Frets)
                {
                    if (!template.Frets.ContainsKey(kv.Key))
                        template.Frets[kv.Key] = kv.Value;
                    if (!template.Fingers.ContainsKey(kv.Key) &&
                        existing.Fingers.TryGetValue(kv.Key, out var f))
                    {
                        template.Fingers[kv.Key] = f;
                    }
                }
            }
            else
            {
                foreach (var cn in c.ChordNotes)
                {
                    if (!template.Frets.ContainsKey(cn.StringIndex))
                        template.Frets[cn.StringIndex] = cn.Fret;
                }
            }
        }

        return template.Frets.Count == 0 ? null : template;
    }

    public ChordTemplate? FindEquivalentTemplate(Arrangement arrangement, ChordTemplate template)
    {
        return arrangement.ChordTemplates.FirstOrDefault(t =>
            !t.IsArpeggio && // both spellings — an RS-XML arpeggio is ForceArpeggioInRs
            t.HasFrets(template.Frets));
    }

    /// <inheritdoc />
    public ChordTemplate? FindIdenticalTemplate(Arrangement arrangement, ChordTemplate template)
    {
        var flavour = ArpeggioFlavour.Of(template);
        return arrangement.ChordTemplates.FirstOrDefault(t =>
            flavour.Matches(t) && // BOTH flags: "identical" cannot ignore one of them
            t.NopInRs == template.NopInRs && // an exported mask bit: a Nop-only edit must not resolve to its source
            t.Fingerless == template.Fingerless && // an EOF eflag: turning it on must not resolve to its source
            string.Equals(t.Name, template.Name, StringComparison.Ordinal) &&
            t.HasSameShapeAs(template));
    }

    public int NextTemplateId(Arrangement arrangement) => arrangement.ChordTemplates.Count == 0 ? 0 : arrangement.ChordTemplates.Max(t => t.Id) + 1;
}
