using DotCharter.Services.State;

namespace DotCharter.Services.Editing.Catalog;

/// <summary>
///     Converts a captured phrase to and from the on-disk representation.
///     <para>
///         <b>A catalog entry IS a tiny <c>.rscp</c>:</b> a one-arrangement,
///         one-level project holding just the phrase's sounds, hand shapes,
///         anchors and chord templates. That is deliberate. The <c>.rscp</c>
///         writer/parser pair already carries the complete field knowledge for
///         every one of those types, and the repo's own history says a
///         second, hand-written serializer for the same model is how fields
///         silently stop being persisted (see the <c>DotCharter.Services</c>
///         guide: fourteen rscp fields shipped one-sided). Reusing the project
///         serializer means a phrase file can never lag the model.
///     </para>
///     <para>
///         The synthetic beat grid the fragment carries exists only so the file
///         is a legal project. Positions are stored as
///         <see cref="FractionalPosition" /> text — (beat, fraction) — so they
///         are independent of it, and of any tempo. A phrase therefore keeps its
///         RHYTHM, not its milliseconds, and lands correctly in a song at a
///         different tempo.
///     </para>
///     <para>
///         What a phrase captures: notes, chords, the chord/hand-shape SHAPES
///         (never template ids — those are positional and renumbered on every
///         save), hand shapes and fret anchors. What it deliberately does NOT
///         capture: event points, phrases, sections and tone changes. Those are
///         song STRUCTURE, not a reusable riff, and pasting them would rewrite
///         the destination's structure behind the user's back (the rscp writer
///         even appends unknown phrase names to <c>Arrangement.Phrases</c> as it
///         serializes).
///     </para>
/// </summary>
public static class PhraseCatalogFragment
{
    /// <summary>Beat length of the fragment's synthetic grid. Any value works —
    /// nothing reads it back — but a round one keeps the file readable.</summary>
    private const double SyntheticBeatMs = 500.0; // 120 BPM

    private const int MinSyntheticBeats = 4;

    /// <summary>
    ///     Builds the project fragment for a phrase captured out of
    ///     <paramref name="source" />. The inputs are NOT mutated: everything is
    ///     cloned and re-anchored so the earliest element sits on beat 0.
    /// </summary>
    /// <returns><c>null</c> when there is nothing worth storing.</returns>
    public static ChartProject? Build(
        string name,
        Arrangement source,
        IEnumerable<GuitarNote> notes,
        IEnumerable<Chord> chords,
        IEnumerable<HandShape> handShapes,
        IEnumerable<FretAnchor> anchors)
    {
        ArgumentNullException.ThrowIfNull(source);

        var noteList = notes.Select(n => n.Clone()).ToList();
        var chordList = chords.Select(c => c.Clone()).ToList();
        var handShapeList = handShapes.Select(h => h.Clone()).ToList();
        var anchorList = anchors.Select(a => a.Clone()).ToList();

        if (noteList.Count == 0 && chordList.Count == 0 && handShapeList.Count == 0 && anchorList.Count == 0)
            return null;

        var origin = Origin(noteList, chordList, handShapeList, anchorList);
        var shift = FractionalPosition.Zero - origin;
        foreach (var n in noteList) n.Position += shift;
        foreach (var c in chordList) c.Position += shift;
        foreach (var h in handShapeList)
        {
            h.Position += shift;
            h.EndPosition += shift;
        }

        foreach (var a in anchorList) a.Position += shift;

        var fragmentArrangement = BuildArrangement(source);
        var level = new ChartLevel { DifficultyIndex = 0 };
        fragmentArrangement.Levels.Add(level);

        // Template ids are POSITIONAL — renumbered on every save — so an entry
        // must carry the SHAPE, never the id. Resolve every chord and hand shape
        // into the fragment's own (initially empty) template list through the
        // same find-or-create the paste path uses, so the file is self-consistent
        // no matter what the source arrangement does afterwards.
        var created = new List<ChordTemplate>();
        foreach (var chord in chordList)
        {
            chord.TemplateId = ResolveFrom(fragmentArrangement, source, chord.TemplateId, created)?.Id
                               ?? ResolveMinting(fragmentArrangement, ChordTemplateResolver.FretsOf(chord),
                                   ArpeggioFlavour.Plain, null, null, created).Id;
        }

        foreach (var handShape in handShapeList.ToList())
        {
            var resolved = ResolveFrom(fragmentArrangement, source, handShape.TemplateId, created);
            if (resolved == null)
            {
                // No shape of its own: adopt the first chord it covers. A hand
                // shape with neither carries no information, so it is dropped
                // rather than stored empty. (Persistence is not the reason: the rscp writer does not coerce
                // TemplateId -1 to 0, which would point it at somebody else's shape —
                // the sentinel is an ABSENT attribute, see
                // RscpWriter.BuildHandShapesNode. The drop stands on the
                // information argument alone.)
                var inner = chordList.FirstOrDefault(c => handShape.Covers(c.Position));
                if (inner == null)
                {
                    handShapeList.Remove(handShape);
                    continue;
                }

                handShape.TemplateId = inner.TemplateId;
                continue;
            }

            handShape.TemplateId = resolved.Id;
        }

        foreach (var template in created) fragmentArrangement.ChordTemplates.Add(template);

        foreach (var n in noteList) level.Notes.Add(n);
        foreach (var c in chordList) level.Chords.Add(c);
        foreach (var h in handShapeList) level.HandShapes.Add(h);
        foreach (var a in anchorList.OrderBy(a => a.Position)) level.Anchors.Add(a);

        var project = new ChartProject();
        project.Chart.SongName = name;
        project.Arrangements.Add(fragmentArrangement);
        AddSyntheticBeats(project, Span(level));
        return project;
    }

    /// <summary>
    ///     Reads a parsed fragment back into a catalog entry. Returns <c>null</c>
    ///     when the file is not a phrase fragment (no arrangement, no level, or
    ///     nothing in it) — a corrupt or foreign file is skipped, never thrown on.
    /// </summary>
    public static PhraseCatalogEntry? Read(string id, ChartProject project, DateTime createdUtc)
    {
        ArgumentNullException.ThrowIfNull(project);

        var arrangement = project.Arrangements.FirstOrDefault();
        var level = ChartLevels.Hardest(arrangement);
        if (arrangement == null || level == null) return null;
        if (level.Notes.Count == 0 && level.Chords.Count == 0 &&
            level.HandShapes.Count == 0 && level.Anchors.Count == 0)
        {
            return null;
        }

        var payload = new ClipboardPayload
        {
            Notes = level.Notes.ToList(),
            Chords = level.Chords.ToList(),
            HandShapes = level.HandShapes.ToList(),
            Anchors = level.Anchors.ToList(),
            ChordTemplates = arrangement.ChordTemplates
                .Where(t => t.Id >= 0)
                .GroupBy(t => t.Id)
                .ToDictionary(g => g.Key, g => g.First())
        };
        payload.BasePosition = Origin(payload.Notes, payload.Chords, payload.HandShapes, payload.Anchors);

        return new PhraseCatalogEntry
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(project.Chart.SongName) ? id : project.Chart.SongName,
            CreatedUtc = createdUtc,
            Payload = payload,
            Source = PhraseSourceContext.From(arrangement)
        };
    }

    private static Arrangement BuildArrangement(Arrangement source)
    {
        var arrangement = new Arrangement
        {
            Name = source.Name,
            ArrangementType = source.ArrangementType,
            Capo = source.Capo,
            StringCount = source.StringCount,
            TuningType = source.TuningType,
            Tuning = source.Tuning.ToArray()
        };
        foreach (var pitch in source.TuningPitches) arrangement.TuningPitches.Add(new TuningOffset(pitch.Step));

        // The rscp writer only serializes explicit tuning offsets for CUSTOM;
        // every other type is re-derived from its NAME on read. That is lossless
        // for a consistent arrangement and lossy for an inconsistent one, so an
        // arrangement whose offsets disagree with its declared type is written as
        // CUSTOM — the phrase's pitches matter more than its tuning label.
        // A name the table does not know has no canonical vector to match, so it
        // is CUSTOM by definition — resolving it to zeros would quietly
        // certify an unknown name as "standard" and drop the real offsets on
        // the next read.
        var known = TuningUtilities.TryGetOffsetsForTuningType(
            source.TuningType, source.StringCount, out var canonical);
        var actual = arrangement.TuningPitches.Select(t => t.Step).ToArray();
        if (!known || !CanonicalMatches(canonical, actual)) arrangement.TuningType = "CUSTOM";

        return arrangement;
    }

    private static bool CanonicalMatches(IReadOnlyList<int> canonical, IReadOnlyList<int> actual)
    {
        for (var i = 0; i < actual.Count; i++)
        {
            var expected = i < canonical.Count ? canonical[i] : 0;
            if (actual[i] != expected) return false;
        }

        return true;
    }

    /// <summary>Resolves the shape <paramref name="sourceTemplateId" /> names in
    /// <paramref name="source" /> into <paramref name="fragment" />. Null when the
    /// id is the -1 sentinel or dangles.</summary>
    private static ChordTemplate? ResolveFrom(
        Arrangement fragment, Arrangement source, int sourceTemplateId, IList<ChordTemplate> created)
    {
        var sourceTemplate = ChordTemplateResolver.FindById(source, sourceTemplateId);
        return sourceTemplate == null
            ? null
            : ResolveMinting(fragment, sourceTemplate.Frets, ArpeggioFlavour.Of(sourceTemplate), sourceTemplate.Name,
                sourceTemplate.Fingers, created);
    }

    /// <summary>
    ///     Find-or-create in the fragment, preserving the source template's
    ///     AUTHORED fingering when a template actually has to be minted.
    ///     <c>ChordTemplateResolver</c> re-suggests fingers for every new template
    ///     (it is a pure function of the shape), which would throw away a
    ///     hand-authored or GP-imported fingering the phrase was captured with.
    ///     Overwriting is safe here and ONLY here: a just-minted template is still
    ///     private to <paramref name="created" /> and backs nothing. When the shape
    ///     was already resolved, the existing template wins — the same
    ///     "first fingering a shape is seen with wins" rule the import path uses.
    /// </summary>
    private static ChordTemplate ResolveMinting(
        Arrangement fragment,
        IReadOnlyDictionary<int, int> frets,
        ArpeggioFlavour arpeggio,
        string? preferredName,
        IReadOnlyDictionary<int, int>? preferredFingers,
        IList<ChordTemplate> created)
    {
        var before = created.Count;
        var template = ChordTemplateResolver.Resolve(fragment, frets, arpeggio, preferredName, created);
        if (created.Count > before && preferredFingers is { Count: > 0 })
        {
            template.Fingers.Clear();
            foreach (var (stringIndex, finger) in preferredFingers) template.Fingers[stringIndex] = finger;
        }

        return template;
    }

    private static FractionalPosition Origin(
        IReadOnlyList<GuitarNote> notes,
        IReadOnlyList<Chord> chords,
        IReadOnlyList<HandShape> handShapes,
        IReadOnlyList<FretAnchor> anchors)
    {
        FractionalPosition? min = null;
        foreach (var n in notes) min = min == null ? n.Position : FractionalPosition.Min(min.Value, n.Position);
        foreach (var c in chords) min = min == null ? c.Position : FractionalPosition.Min(min.Value, c.Position);
        foreach (var h in handShapes) min = min == null ? h.Position : FractionalPosition.Min(min.Value, h.Position);
        foreach (var a in anchors) min = min == null ? a.Position : FractionalPosition.Min(min.Value, a.Position);
        return min ?? FractionalPosition.Zero;
    }

    private static double Span(ChartLevel level)
    {
        var end = FractionalPosition.Zero;
        foreach (var n in level.Notes) end = FractionalPosition.Max(end, n.EndPosition);
        foreach (var c in level.Chords) end = FractionalPosition.Max(end, c.EndPosition);
        foreach (var h in level.HandShapes) end = FractionalPosition.Max(end, h.EndPosition);
        foreach (var a in level.Anchors) end = FractionalPosition.Max(end, a.Position);
        return end.DoubleValue;
    }

    private static void AddSyntheticBeats(ChartProject project, double spanInBeats)
    {
        var count = Math.Max(MinSyntheticBeats, (int)Math.Ceiling(spanInBeats) + 2);
        for (var i = 0; i < count; i++)
        {
            project.Beats.Add(new BeatMarker
            {
                Time = i * SyntheticBeatMs,
                IsMeasure = i % 4 == 0,
                IsAnchor = i == 0
            });
        }
    }
}
