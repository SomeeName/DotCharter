using DotCharter.Services.Editing.Fingering;

namespace DotCharter.Services.Editing.ChordShapes;

/// <summary>How far a template's fingering is from being authored.</summary>
public enum ChordFingerState
{
    /// <summary>Every fretted string has a finger, and the fingering is not simply the suggester's.</summary>
    Complete,

    /// <summary>Every fretted string has a finger, and they are exactly what the suggester would write.</summary>
    Suggested,

    /// <summary>At least one fretted string has no finger — the "Save &amp; next missing" queue.</summary>
    Missing
}

/// <summary>How many chords and hand shapes point at one template, on the level on screen and on every level.</summary>
public readonly record struct ChordShapeUses(int ChordsOnLevel, int HandShapesOnLevel, int ChordsAll, int HandShapesAll)
{
    public int OnLevel => ChordsOnLevel + HandShapesOnLevel;

    public int All => ChordsAll + HandShapesAll;

    public int OnOtherLevels => All - OnLevel;
}

/// <summary>
///     One template as the Chord shapes list shows it. <see cref="Duplicates" /> are the OTHER
///     templates of the arrangement with the same frets in the same arpeggio flavour — the
///     find-or-create key of <see cref="ChordTemplateResolver" />, so a merge target is always
///     one the resolver itself could have handed out.
/// </summary>
public sealed record ChordShapeEntry(
    ChordTemplate Template,
    ChordShapeUses Uses,
    ChordFingerState FingerState,
    IReadOnlyList<ChordTemplate> Duplicates)
{
    public bool IsDuplicate => Duplicates.Count > 0;
}

/// <summary>
///     The Chord shapes list's model: every template of an arrangement with its use counts,
///     its finger state and its duplicates. Pure — reads the chart, writes nothing.
/// </summary>
/// <remarks>
///     A use is counted against the template its id resolves to through
///     <see cref="ChordTemplateResolver.IndexById" />: the first template with an id wins and a
///     negative id is no use at all, so the counts agree with what the exporter resolves.
/// </remarks>
public static class ChordShapeCatalog
{
    private static readonly FingeringGenerationService Fingering = new();

    public static IReadOnlyList<ChordShapeEntry> Build(Arrangement arrangement, ChartLevel? activeLevel)
    {
        ArgumentNullException.ThrowIfNull(arrangement);

        var templates = arrangement.ChordTemplates;
        var byId = ChordTemplateResolver.IndexById(templates);
        var counts = new Dictionary<ChordTemplate, int[]>(ReferenceEqualityComparer.Instance);
        foreach (var template in templates) counts[template] = new int[4];

        foreach (var level in arrangement.Levels)
        {
            var onScreen = ReferenceEquals(level, activeLevel);
            foreach (var chord in level.Chords)
                Count(chord.TemplateId, 0, onScreen);
            foreach (var shape in level.HandShapes)
                Count(shape.TemplateId, 1, onScreen);
        }

        var entries = new List<ChordShapeEntry>(templates.Count);
        foreach (var template in templates)
        {
            var c = counts[template];
            entries.Add(new ChordShapeEntry(
                template,
                new ChordShapeUses(c[0], c[1], c[2], c[3]),
                FingerStateOf(template, arrangement.StringCount),
                DuplicatesOf(templates, template)));
        }

        return entries;

        void Count(int templateId, int slot, bool onScreen)
        {
            if (!byId.TryGetValue(templateId, out var template)) return;
            var c = counts[template];
            c[slot + 2]++;
            if (onScreen) c[slot]++;
        }
    }

    /// <summary>
    ///     Same frets, same arpeggio flavour, a different instance. Value equality is not the
    ///     question: two templates with the same frets but different names or fingers are the
    ///     duplicates a charter wants to clean up.
    /// </summary>
    public static bool IsDuplicateOf(ChordTemplate template, ChordTemplate other)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(other);
        return !ReferenceEquals(template, other)
               && ArpeggioFlavour.Of(template).Matches(other)
               && template.HasFrets(other.Frets);
    }

    public static IReadOnlyList<ChordTemplate> DuplicatesOf(IEnumerable<ChordTemplate> templates, ChordTemplate template)
    {
        ArgumentNullException.ThrowIfNull(templates);
        List<ChordTemplate>? duplicates = null;
        foreach (var other in templates)
        {
            if (IsDuplicateOf(template, other))
                (duplicates ??= []).Add(other);
        }

        return duplicates ?? (IReadOnlyList<ChordTemplate>)[];
    }

    /// <summary>
    ///     Missing when a FRETTED string has no finger (an open string legitimately has none);
    ///     Suggested when the fingers are exactly the suggester's; Complete otherwise, which
    ///     includes a shape of open strings only.
    /// </summary>
    public static ChordFingerState FingerStateOf(ChordTemplate template, int stringCount)
    {
        ArgumentNullException.ThrowIfNull(template);
        foreach (var (stringIndex, fret) in template.Frets)
        {
            if (fret > 0 && !template.Fingers.ContainsKey(stringIndex))
                return ChordFingerState.Missing;
        }

        if (!template.Frets.Values.Any(f => f > 0)) return ChordFingerState.Complete;
        return Fingering.SuggestFingeringFor(template, stringCount) is null
            ? ChordFingerState.Suggested
            : ChordFingerState.Complete;
    }
}
