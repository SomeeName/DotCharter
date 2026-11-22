namespace DotCharter.Services.Export.DynamicDifficulty;

/// <summary>
///     A chord or hand shape that needs a template with fewer notes (Rocksmith2014.DD's
///     <c>TemplateRequest</c>). Exactly one of <see cref="Chord" /> and
///     <see cref="HandShape" /> is set.
/// </summary>
internal sealed record DdTemplateRequest(int OriginalId, int NoteCount, bool FromHighestNote, DdChord? Chord, DdHandShape? HandShape);

/// <summary>
///     Ported from Rocksmith2014.NET (src/Rocksmith2014.DD/Utils.fs and
///     TemplateRequestApplier.fs; MIT, Copyright (c) 2020 Tapio Malmberg — see
///     THIRD-PARTY-NOTICES.md § 2.1).
///     <para>
///         <b>Strings past the sixth.</b> The F# is six strings wide. Here a template is
///         <see cref="DdArrangement.StringSlots" /> wide and string 0 is the LOWEST, so a
///         seven-string guitar is read lowest first (the chart's order, not the package's):
///         "the highest strings" are the top three slots and notes are removed from the
///         low or the high end of the same order. For six slots every index is the F#'s.
///     </para>
/// </summary>
internal static class DdTemplates
{
    public static int NoteCount(DdChordTemplate template) => template.Frets.Count(f => f >= 0);

    /// <summary><c>getAllowedChordNotes</c>.</summary>
    public static int AllowedChordNotes(double diffPercent, int maxChordNotesInPhrase) =>
        maxChordNotesInPhrase == 0 ? 0 : (int)Math.Ceiling(diffPercent * maxChordNotesInPhrase);

    /// <summary><c>shouldStartFromHighestNote</c>: for double and triple stops on the
    /// highest strings, start adding notes from the highest string.</summary>
    public static bool StartFromHighestNote(int totalNotes, DdChordTemplate template)
    {
        var fr = template.Frets;
        var top = fr.Length - 1;
        return (totalNotes == 3 && fr[top] > -1 && fr[top - 1] > -1 && fr[top - 2] > -1)
               || (totalNotes == 2 && fr[top] > -1 && fr[top - 1] > -1);
    }

    public static DdTemplateRequest Request(int originalId, int noteCount, int totalNotes, DdChordTemplate template,
        DdChord? chord, DdHandShape? handShape) =>
        new(originalId, noteCount, StartFromHighestNote(totalNotes, template), chord, handShape);
}

/// <summary><c>TemplateRequestApplier.applyChordId</c>: one per phrase, sharing the
/// arrangement's template list.</summary>
internal sealed class DdTemplateRequestApplier
{
    private readonly List<DdChordTemplate> _templates;
    private readonly Dictionary<(int OriginalId, int NoteCount), int> _templateMap = [];

    public DdTemplateRequestApplier(List<DdChordTemplate> templates) => _templates = templates;

    /// <summary>
    ///     One level's requests, the hand shapes' first (<c>templateRequests1 |> Seq.append
    ///     templateRequests2</c>). The one decision that is NOT DLC Builder's: the F#
    ///     narrows a hand shape and the chords under it independently, each by a count from
    ///     one end of its own template. That agreed for a chord with muted strings only
    ///     because the cleanup had cut the template both shared; now such a chord sits on
    ///     its own reduced grip (<see cref="DdChordTemplate.ReducedFrom" />). A shape whose
    ///     narrowed grip would lack a string that chord sounds at this level takes its full
    ///     grip cut to exactly the strings sounded under it instead. Every other shape gets
    ///     the F# request unchanged.
    /// </summary>
    public void ApplyLevel(IReadOnlyList<(DdHandShape Shape, DdTemplateRequest? Request)> handShapes,
        IReadOnlyList<(DdEntity Entity, DdTemplateRequest? Request)> chosen)
    {
        foreach (var (shape, request) in handShapes)
        {
            if (request == null) continue;
            var full = _templates[request.OriginalId];
            var narrowed = RemoveNotes(DdTemplates.NoteCount(full) - request.NoteCount, request.FromHighestNote, full).Frets;
            var sounded = new HashSet<int>();
            var reducedChordLosesAString = false;
            foreach (var (entity, chordRequest) in chosen)
            {
                if (entity.Time < shape.StartTime || entity.Time >= shape.EndTime) continue;
                var strings = SoundedStrings(entity, chordRequest).Where(s => full.Frets[s] >= 0).ToList();
                sounded.UnionWith(strings);
                reducedChordLosesAString |= entity.Chord is { } chord
                                            && _templates[chordRequest?.OriginalId ?? chord.ChordId].ReducedFrom == full
                                            && strings.Exists(s => narrowed[s] < 0);
            }

            if (reducedChordLosesAString)
                shape.ChordId = GripOn(full, sounded);
            else
                Apply(request);
        }

        foreach (var (_, request) in chosen)
        {
            if (request != null) Apply(request);
        }
    }

    /// <summary>The strings a chosen note or chord sounds, the chord's read before its
    /// own request is applied.</summary>
    private IEnumerable<int> SoundedStrings(DdEntity entity, DdTemplateRequest? request)
    {
        if (entity.Note is { } note) return [note.String];
        var chord = entity.Chord!;
        if (chord.HasChordNotes) return chord.ChordNotes!.Select(chordNote => chordNote.String);
        var template = _templates[request?.OriginalId ?? chord.ChordId];
        var frets = request == null
            ? template.Frets
            : RemoveNotes(DdTemplates.NoteCount(template) - request.NoteCount, request.FromHighestNote, template).Frets;
        return Enumerable.Range(0, frets.Length).Where(s => frets[s] >= 0);
    }

    /// <summary>The template holding <paramref name="full" /> on exactly
    /// <paramref name="strings" />: the muted-string cleanup's own reduced grip when it
    /// minted that one, else a new one.</summary>
    private int GripOn(DdChordTemplate full, HashSet<int> strings)
    {
        var fingers = (int[])full.Fingers.Clone();
        var frets = (int[])full.Frets.Clone();
        for (var s = 0; s < frets.Length; s++)
        {
            if (strings.Contains(s)) continue;
            fingers[s] = -1;
            frets[s] = -1;
        }

        var existing = _templates.FindIndex(x =>
            x.ReducedFrom == full
            && x.Frets.AsSpan().SequenceEqual(frets)
            && x.Fingers.AsSpan().SequenceEqual(fingers));
        if (existing != -1) return existing;
        _templates.Add(new DdChordTemplate(full.Name, full.DisplayName, fingers, frets) { Source = full.Source, ReducedFrom = full });
        return _templates.Count - 1;
    }

    public void Apply(DdTemplateRequest request)
    {
        if (!_templateMap.TryGetValue((request.OriginalId, request.NoteCount), out var chordId))
        {
            var template = _templates[request.OriginalId];
            var notesToRemove = DdTemplates.NoteCount(template) - request.NoteCount;
            var (fingers, frets) = RemoveNotes(notesToRemove, request.FromHighestNote, template);
            chordId = GetChordId(template, fingers, frets);
            _templateMap.Add((request.OriginalId, request.NoteCount), chordId);
        }

        request.Chord?.ChordId = chordId;
        request.HandShape?.ChordId = chordId;
    }

    /// <summary>Removes notes from the lowest string up (keeping the highest) or from the
    /// highest string down.</summary>
    private static (int[] Fingers, int[] Frets) RemoveNotes(int notesToRemove, bool fromHighest, DdChordTemplate template)
    {
        var fingers = (int[])template.Fingers.Clone();
        var frets = (int[])template.Frets.Clone();
        var (i, change) = fromHighest ? (0, 1) : (frets.Length - 1, -1);
        var leftToRemove = notesToRemove;
        while (leftToRemove > 0 && i >= 0 && i < frets.Length)
        {
            if (frets[i] != -1)
            {
                fingers[i] = -1;
                frets[i] = -1;
                leftToRemove--;
            }

            i += change;
        }

        return (fingers, frets);
    }

    /// <summary>An identical existing template's ID, or a new template's. The F# matches
    /// the existing template's DISPLAY name against the source's NAME and vice versa;
    /// kept as it is so the same templates are reused.</summary>
    private int GetChordId(DdChordTemplate template, int[] fingers, int[] frets)
    {
        var existing = _templates.FindIndex(x =>
            x.DisplayName == template.Name
            && x.Name == template.DisplayName
            && x.Frets.AsSpan().SequenceEqual(frets)
            && x.Fingers.AsSpan().SequenceEqual(fingers));
        if (existing != -1) return existing;
        _templates.Add(new DdChordTemplate(template.Name, template.DisplayName, fingers, frets) { ReducedFrom = template.ReducedFrom });
        return _templates.Count - 1;
    }
}
