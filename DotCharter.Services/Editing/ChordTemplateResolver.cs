using DotCharter.Services.Editing.Fingering;

namespace DotCharter.Services.Editing;

/// <summary>
///     Find-or-create for chord templates on the EDITING paths, mirroring
///     <c>ChordHandShapeBuilder.FindOrCreateTemplate</c> (the import path) and
///     <c>RefretService.FindOrCreateTemplate</c> (the refret path).
///     <para>
///         A <see cref="ChordTemplate" /> is SHARED: one instance backs every
///         chord AND hand shape with that shape, so editing one silently
///         rewrites all of them. The only correct way to change a chord's shape
///         is therefore to resolve-or-create a template that ALREADY has the new
///         shape and repoint the chord's <c>TemplateId</c>. Editors that changed
///         a chord's notes without doing so left the chord and its template
///         describing different shapes — the SNG/RS-XML export wrote the chord
///         under a chordId whose template disagrees, the 3D hand-shape bar drew
///         the old shape, and <c>RefretPlanner.IsChordPinned</c> pinned the chord
///         forever as "template drift".
///     </para>
///     <para>
///         The match key is the documented one: FRETS alone within the same
///         arpeggio flavour. Newly minted templates arrive charting-ready
///         (suggested fingering + suggested name), exactly like an imported one.
///     </para>
///     <para>
///         The flavour is an <see cref="ArpeggioFlavour" /> — BOTH flags —
///         because two flags spell arpeggio and each import path sets only one.
///         Keying on <c>Arpeggio</c> alone broke it in both directions on every
///         DLC-Builder / EOF chart, where an arpeggio is spelled
///         <c>ForceArpeggioInRs</c>: re-shaping an arpeggio chord resolved with
///         a PLAIN key and silently dropped its <c>-arp</c> export name, and
///         re-shaping a plain chord onto those frets matched the arpeggio
///         template and started exporting it as one.
///     </para>
/// </summary>
public static class ChordTemplateResolver
{
    /// <summary>
    ///     Returns a template of <paramref name="arrangement" /> whose shape is
    ///     <paramref name="frets" />, creating one if none exists. A created
    ///     template is appended to <paramref name="created" /> and NOT added to
    ///     the arrangement — the caller must do that through an
    ///     <c>AddChordTemplatesAction</c> so the mint is undoable, and that
    ///     action must run BEFORE the chords that reference it (the spacing
    ///     enforcer resolves template ids inside the add action's post-execute).
    ///     <para>
    ///         <c>preferredName</c> is kept when a template has to be minted — the
    ///         SOURCE template's name on a cross-arrangement paste, so a
    ///         deliberate "Dmin" is not re-derived. Null/blank ⇒ suggest one.
    ///     </para>
    /// </summary>
    public static ChordTemplate Resolve(
        Arrangement arrangement,
        IReadOnlyDictionary<int, int> frets,
        ArpeggioFlavour arpeggio,
        string? preferredName,
        IList<ChordTemplate> created)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        ArgumentNullException.ThrowIfNull(frets);
        ArgumentNullException.ThrowIfNull(created);

        var existing = FindByShape(arrangement, frets, arpeggio);
        if (existing != null) return existing;

        // Same shape twice in one edit must mint ONE template, not two.
        foreach (var pending in created)
        {
            if (arpeggio.Matches(pending) && pending.HasFrets(frets))
                return pending;
        }

        var template = new ChordTemplate();
        arpeggio.ApplyTo(template);
        foreach (var (stringIndex, fret) in frets) template.Frets[stringIndex] = fret;

        // The arrangement's own 1·3 / 1·4 for a power chord or an octave.
        ArrangementFingeringStyle.SetSuggestedFingers(arrangement, template, arrangement.StringCount);
        template.Name = string.IsNullOrWhiteSpace(preferredName)
            ? ChordNameSuggester.SuggestFirstChordName(arrangement, template.Frets)
            : preferredName;
        template.Id = NextId(arrangement, created);

        created.Add(template);
        return template;
    }

    /// <summary>
    ///     Resolve-or-create for a REDUCED grip of <paramref name="source" />: the strings in
    ///     <paramref name="frets" />, a subset of the source's, fingered exactly as the source
    /// fingers them, in the source's arpeggio flavour (the lower difficulty levels).
    ///     <paramref name="source" /> is never mutated, and a mint goes to
    ///     <paramref name="created" /> like <see cref="Resolve" />'s.
    ///     <para>
    ///         <b>The key is frets + FINGERS within the flavour</b>, the same stricter key
    ///         <c>ChartSpacingEnforcer.FindOrCreateReducedTemplate</c> uses for a partially muted
    ///         chord, and for the same reason: the frets-only key would hand the reduced chord any
    ///         same-fret template's fingering, and the point of a reduced grip is that the hand
    ///         stays where the full chord puts it. Official ladders agree — 97–100 % of their
    ///         reduced templates finger the kept strings as the full chord does.
    ///     </para>
    ///     <para>
    ///         A mint carries NO name: official ladders leave a reduced grip unnamed (46–90 %
    ///         by depth), and a suggested name for a partial grip is a guess. When the source
    ///         does not finger every kept string (no template, or a template that disagrees with
    ///         the chord) the frets-only <see cref="Resolve" /> answers instead.
    ///     </para>
    /// </summary>
    public static ChordTemplate ResolveReduced(
        Arrangement arrangement,
        ChordTemplate? source,
        IReadOnlyDictionary<int, int> frets,
        IList<ChordTemplate> created)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        ArgumentNullException.ThrowIfNull(frets);
        ArgumentNullException.ThrowIfNull(created);

        var flavour = ArpeggioFlavour.Of(source);
        if (source == null || frets.Any(f => source.GetFret(f.Key) != f.Value))
            return Resolve(arrangement, frets, flavour, null, created);

        var fingers = new Dictionary<int, int>(frets.Count);
        foreach (var stringIndex in frets.Keys)
        {
            if (source.Fingers.TryGetValue(stringIndex, out var finger)) fingers[stringIndex] = finger;
        }

        foreach (var candidate in arrangement.ChordTemplates.Concat(created))
        {
            if (flavour.Matches(candidate) && candidate.HasFrets(frets) && MapEquality.SameEntries(candidate.Fingers, fingers))
                return candidate;
        }

        var template = new ChordTemplate { Name = "", Fingers = fingers };
        flavour.ApplyTo(template);
        foreach (var (stringIndex, fret) in frets) template.Frets[stringIndex] = fret;
        template.Id = NextId(arrangement, created);
        created.Add(template);
        return template;
    }

    /// <summary>Resolve-or-create for the shape a chord's notes currently play.</summary>
    public static ChordTemplate ResolveForChord(Arrangement arrangement, Chord chord, IList<ChordTemplate> created)
    {
        ArgumentNullException.ThrowIfNull(chord);
        // Keep the arpeggio flavour of whatever the chord pointed at, so
        // re-shaping an arpeggio chord stays an arpeggio — in BOTH spellings.
        var current = FindById(arrangement, chord.TemplateId);
        // A PINNED template that still describes the chord stays its template: the
        // frets-only key would hand the chord the FIRST value-equal grip instead, and an
        // authored fingering would be replaced by whichever template came earlier.
        var frets = FretsOf(chord);
        if (current is { IsPinned: true } && current.HasFrets(frets)) return current;
        return Resolve(arrangement, frets, ArpeggioFlavour.Of(current), null, created);
    }

    public static Dictionary<int, int> FretsOf(Chord chord)
    {
        ArgumentNullException.ThrowIfNull(chord);
        var frets = new Dictionary<int, int>(chord.ChordNotes.Count);
        foreach (var chordNote in chord.ChordNotes) frets[chordNote.StringIndex] = chordNote.Fret;
        return frets;
    }

    /// <summary>
    ///     The FIND half of find-or-create, without the create: the template this
    ///     arrangement already declares for <paramref name="frets" /> in this
    ///     arpeggio flavour, or null.
    ///     <para>
    ///         Same key as <see cref="Resolve" /> — which calls this — so a
    ///         caller that only wants to know "does the chart already declare
    ///         this shape?" cannot answer it differently from the resolve that
    ///         will run on the next save. The validation rules ask it about a
    ///         chord whose <c>TemplateId</c> is still the <c>-1</c> sentinel:
    ///         the fingering the game will show for that chord is the fingering
    ///         of whatever template this returns.
    ///     </para>
    /// </summary>
    public static ChordTemplate? FindByShape(
        Arrangement arrangement, IReadOnlyDictionary<int, int> frets, ArpeggioFlavour arpeggio)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        ArgumentNullException.ThrowIfNull(frets);

        foreach (var existing in arrangement.ChordTemplates)
        {
            if (arpeggio.Matches(existing) && existing.HasFrets(frets))
                return existing;
        }

        return null;
    }

    /// <summary>Id lookup with NO index fallback — a dangling id is dangling.</summary>
    public static ChordTemplate? FindById(Arrangement arrangement, int templateId)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        if (templateId < 0) return null;
        foreach (var template in arrangement.ChordTemplates)
        {
            if (template.Id == templateId)
                return template;
        }

        return null;
    }

    /// <summary>
    ///     <see cref="FindById" /> for a whole pass, answered in O(1): the FIRST
    ///     template with each id wins, a negative id is never indexed, and there is
    ///     no list-index fallback. A pass that resolves every chord must use this,
    ///     never <c>templates[chord.TemplateId]</c> — ids are not positions.
    /// </summary>
    public static Dictionary<int, ChordTemplate> IndexById(IEnumerable<ChordTemplate> templates)
    {
        ArgumentNullException.ThrowIfNull(templates);
        var byId = new Dictionary<int, ChordTemplate>();
        foreach (var template in templates)
        {
            if (template.Id >= 0) byId.TryAdd(template.Id, template);
        }

        return byId;
    }

    private static int NextId(Arrangement arrangement, IEnumerable<ChordTemplate> created)
    {
        var max = -1;
        foreach (var template in arrangement.ChordTemplates)
        {
            if (template.Id > max)
                max = template.Id;
        }

        foreach (var template in created)
        {
            if (template.Id > max)
                max = template.Id;
        }

        return max + 1;
    }
}
