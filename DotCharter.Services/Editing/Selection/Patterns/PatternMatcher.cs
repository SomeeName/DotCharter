using System.Globalization;
using System.Text;

namespace DotCharter.Services.Editing.Selection.Patterns;

/// <summary>Everything a pattern is matched AGAINST — one arrangement's level,
///     passed in explicitly so the matcher stays pure and testable.</summary>
public readonly record struct PatternSource(
    Arrangement? Arrangement,
    IReadOnlyList<GuitarNote> Notes,
    IReadOnlyList<Chord> Chords,
    IReadOnlyList<HandShape> HandShapes,
    IReadOnlyList<FretAnchor> Anchors,
    IReadOnlyList<EventPoint> EventPoints)
{
    public static PatternSource Empty => new(null, [], [], [], [], []);

    /// <summary>
    ///     Event points come from the ARRANGEMENT, not the level — sections and
    ///     phrases are song structure and exist once, while notes and anchors are
    ///     per difficulty level. <see cref="PatternRange" /> is the only thing
    ///     that reads them.
    /// </summary>
    public static PatternSource From(Arrangement? arrangement, ChartLevel? level) =>
        level == null
            ? Empty
            : new PatternSource(arrangement, level.Notes, level.Chords, level.HandShapes, level.Anchors,
                arrangement?.EventPoints ?? (IReadOnlyList<EventPoint>)[]);
}

/// <summary>The already-selected elements a pattern is derived FROM.</summary>
public readonly record struct PatternSeed(
    IReadOnlyList<GuitarNote> Notes,
    IReadOnlyList<Chord> Chords,
    IReadOnlyList<HandShape> HandShapes,
    IReadOnlyList<FretAnchor> Anchors)
{
    public static PatternSeed Empty => new([], [], [], []);

    public int Count => Notes.Count + Chords.Count + HandShapes.Count + Anchors.Count;
}

/// <summary>
///     ONE occurrence of a pattern. For an individually-matched pattern that is a
///     single element; for a run it is the whole re-occurrence of the lick; and
///     with companions on it also carries the hand shape and the fret-hand anchor
///     that belong to it.
///     <para>
///         The grouping is what makes the NAVIGATE verb work. Flat element lists
///         are all "select every match" needs, but "go to the next one" has to
///         know where one match ends and the next begins, or a four-note lick
///         takes four keypresses to step over and each one selects a fragment.
///     </para>
/// </summary>
public sealed class PatternMatchGroup
{
    public PatternMatchGroup(
        FractionalPosition position,
        IReadOnlyList<GuitarNote> notes,
        IReadOnlyList<Chord> chords,
        IReadOnlyList<HandShape> handShapes,
        IReadOnlyList<FretAnchor> anchors)
    {
        Position = position;
        Notes = notes;
        Chords = chords;
        HandShapes = handShapes;
        Anchors = anchors;
    }

    /// <summary>Where the occurrence starts — what the playhead lands on.</summary>
    public FractionalPosition Position { get; }

    public IReadOnlyList<GuitarNote> Notes { get; }
    public IReadOnlyList<Chord> Chords { get; }
    public IReadOnlyList<HandShape> HandShapes { get; }
    public IReadOnlyList<FretAnchor> Anchors { get; }

    public int Count => Notes.Count + Chords.Count + HandShapes.Count + Anchors.Count;

    internal PatternMatchGroup With(IReadOnlyList<HandShape> handShapes, IReadOnlyList<FretAnchor> anchors,
        IReadOnlyList<GuitarNote> notes, IReadOnlyList<Chord> chords) =>
        new(Position, notes, chords, handShapes, anchors);
}

/// <summary>What a pattern found, in chart order, never including the seeds.</summary>
public readonly record struct PatternMatches(
    IReadOnlyList<GuitarNote> Notes,
    IReadOnlyList<Chord> Chords,
    IReadOnlyList<HandShape> HandShapes,
    IReadOnlyList<FretAnchor> Anchors,
    IReadOnlyList<PatternMatchGroup> Groups)
{
    public static PatternMatches Empty => new([], [], [], [], []);

    /// <summary>How many ELEMENTS matched. <see cref="Groups" />.Count is how many
    ///     OCCURRENCES — the two differ for a run and whenever companions are on.</summary>
    public int Count => Notes.Count + Chords.Count + HandShapes.Count + Anchors.Count;
}

/// <summary>
///     Runs a <see cref="MatchPattern" /> over an arrangement: pure, allocation-
///     modest, no services and no clock, so every facet is provable in the
///     default test gate.
///     <para>
///         The engine is signature-based. Each element is reduced to a canonical
///         string KEY built from exactly the facets the pattern enables, and two
///         elements are alike when their keys are equal. The one thing that
///         cannot live in the key is FINGERING, because an unauthored finger is a
///         hole rather than a value (see <see cref="ChordShapeSignature" />) and
///         hole-tolerant agreement is not transitive — so fingers ride alongside
///         and are compared pairwise.
///     </para>
///     <para>
///         This does NOT replace <see cref="SelectLikeService" />. That service is
///         the shipped default behaviour of the one built-in select-like keybind
///         and encodes three specific, argued-for answers; this is the seam for
///         asking a DIFFERENT question, and the two are deliberately allowed to
///         disagree.
///     </para>
/// </summary>
public static class PatternMatcher
{
    /// <summary>
    ///     Every element in <paramref name="source" /> that is alike one of the
    ///     seeds under <paramref name="pattern" />, excluding the seeds
    ///     themselves. An empty seed for a scope contributes nothing — a pattern
    ///     is a question ABOUT a selection, never a standing filter.
    /// </summary>
    public static PatternMatches Find(MatchPattern pattern, PatternSeed seed, PatternSource source)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        var gate = RangeGate.Build(pattern, seed, source);

        var groups = new List<PatternMatchGroup>();
        MatchSounds(pattern, seed, source, gate, groups);
        MatchHandShapes(pattern, seed, source, gate, groups);
        MatchAnchors(pattern, seed, source, gate, groups);

        groups.Sort(static (a, b) => a.Position.CompareTo(b.Position));

        if (pattern.Companions != PatternCompanions.None)
            groups = AddCompanions(pattern, seed, source, groups);

        var notes = new List<GuitarNote>();
        var chords = new List<Chord>();
        var handShapes = new List<HandShape>();
        var anchors = new List<FretAnchor>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);

        foreach (var group in groups)
        {
            foreach (var note in group.Notes)
            {
                if (seen.Add(note))
                    notes.Add(note);
            }

            foreach (var chord in group.Chords)
            {
                if (seen.Add(chord))
                    chords.Add(chord);
            }

            foreach (var handShape in group.HandShapes)
            {
                if (seen.Add(handShape))
                    handShapes.Add(handShape);
            }

            foreach (var anchor in group.Anchors)
            {
                if (seen.Add(anchor))
                    anchors.Add(anchor);
            }
        }

        return new PatternMatches(notes, chords, handShapes, anchors, groups);
    }

    // ---------------------------------------------------------------- sounds

    private static void MatchSounds(
        MatchPattern pattern, PatternSeed seed, PatternSource source, RangeGate gate,
        List<PatternMatchGroup> output)
    {
        var wantsNotes = pattern.Scope.HasFlag(PatternScope.Notes);
        var wantsChords = pattern.Scope.HasFlag(PatternScope.Chords);
        if (!wantsNotes && !wantsChords) return;

        var seeds = new List<SoundRef>();
        if (wantsNotes)
        {
            foreach (var note in seed.Notes)
                seeds.Add(SoundRef.Of(note));
        }

        if (wantsChords)
        {
            foreach (var chord in seed.Chords)
                seeds.Add(SoundRef.Of(chord));
        }

        if (seeds.Count == 0) return;

        var candidates = new List<SoundRef>();
        if (wantsNotes)
        {
            foreach (var note in source.Notes)
                candidates.Add(SoundRef.Of(note));
        }

        if (wantsChords)
        {
            foreach (var chord in source.Chords)
                candidates.Add(SoundRef.Of(chord));
        }

        if (candidates.Count == 0) return;

        candidates.Sort(CompareSounds);

        var seedSet = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var s in seeds) seedSet.Add(s.Element);

        if (pattern.Grouping == PatternGrouping.AsRun)
            MatchRun(pattern, seeds, candidates, source.Arrangement, seedSet, gate, output);
        else
            MatchIndividually(pattern, seeds, candidates, source.Arrangement, seedSet, gate, output);
    }

    private static void MatchIndividually(
        MatchPattern pattern, List<SoundRef> seeds, List<SoundRef> candidates,
        Arrangement? arrangement, HashSet<object> seedSet, RangeGate gate, List<PatternMatchGroup> output)
    {
        var seedEntries = new List<SeedEntry>(seeds.Count);
        foreach (var s in seeds)
        {
            var origin = Origin.Of(pattern, [s]);
            var signature = BuildSoundSignature(pattern, s, arrangement, origin);
            var entry = new SeedEntry(signature, LowestFrettedFret(s));
            var duplicate = false;
            foreach (var existing in seedEntries)
            {
                if (existing.Signature.SameAs(signature) && existing.LowestFret == entry.LowestFret)
                {
                    duplicate = true;
                    break;
                }
            }

            if (!duplicate) seedEntries.Add(entry);
        }

        var claimed = new HashSet<object>(ReferenceEqualityComparer.Instance);

        foreach (var candidate in candidates)
        {
            if (seedSet.Contains(candidate.Element)) continue;
            if (!gate.Allows(candidate.Position)) continue;
            if (!claimed.Add(candidate.Element)) continue;

            var signature = BuildSoundSignature(pattern, candidate, arrangement, Origin.Of(pattern, [candidate]));
            var candidateFret = LowestFrettedFret(candidate);

            foreach (var entry in seedEntries)
            {
                if (!entry.Signature.Matches(signature)) continue;
                if (!WithinFretDistance(pattern, entry.LowestFret, candidateFret)) continue;
                output.Add(GroupOf([candidate]));
                break;
            }
        }
    }

    /// <summary>
    ///     The whole selection as one contiguous run. The template is the seeds in
    ///     chart order; a match is any window of that many CONSECUTIVE sounds in
    ///     the source whose elements are alike one for one — and, depending on
    ///     <see cref="MatchPattern.Rhythm" />, whose internal spacing is identical
    ///     or merely proportional. Contiguity is the point: "this lick appears
    ///     again" is a statement about a passage, not about a scattered set.
    /// </summary>
    private static void MatchRun(
        MatchPattern pattern, List<SoundRef> seeds, List<SoundRef> candidates,
        Arrangement? arrangement, HashSet<object> seedSet, RangeGate gate, List<PatternMatchGroup> output)
    {
        var template = new List<SoundRef>(seeds);
        template.Sort(CompareSounds);

        var templateOrigin = Origin.Of(pattern, template);
        var templateFret = LowestFrettedFret(template);
        var templateSignatures = new List<Signature>(template.Count);
        foreach (var element in template)
            templateSignatures.Add(BuildSoundSignature(pattern, element, arrangement, templateOrigin));

        var claimed = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var window = new List<SoundRef>(template.Count);

        for (var start = 0; start + template.Count <= candidates.Count; start++)
        {
            window.Clear();
            for (var i = 0; i < template.Count; i++) window.Add(candidates[start + i]);

            // A window overlapping the seeds is the seed run itself (or a shifted
            // copy of it) — never a find. A window overlapping an already-claimed
            // window is a sliding duplicate of a match we have: a run of four
            // identical notes must not report three overlapping "occurrences".
            var skip = false;
            foreach (var element in window)
            {
                if (seedSet.Contains(element.Element) || claimed.Contains(element.Element) ||
                    !gate.Allows(element.Position))
                {
                    skip = true;
                    break;
                }
            }

            if (skip) continue;
            if (!WithinFretDistance(pattern, templateFret, LowestFrettedFret(window))) continue;
            if (!RunMatches(pattern, template, templateSignatures, window, arrangement)) continue;

            foreach (var element in window) claimed.Add(element.Element);
            output.Add(GroupOf(window));
        }
    }

    private static bool RunMatches(
        MatchPattern pattern, List<SoundRef> template, List<Signature> templateSignatures,
        List<SoundRef> window, Arrangement? arrangement)
    {
        var windowOrigin = Origin.Of(pattern, window);

        for (var i = 0; i < template.Count; i++)
        {
            var signature = BuildSoundSignature(pattern, window[i], arrangement, windowOrigin);
            if (!templateSignatures[i].Matches(signature)) return false;
        }

        return RhythmMatches(pattern.Rhythm, template, window);
    }

    /// <summary>
    ///     <see cref="RhythmRule.Proportional" /> is the interesting one: a lick
    ///     and its half- or double-time restatement are the same lick. The ratio
    ///     is fixed by the first non-zero gap and every later gap must honour it,
    ///     so an arbitrary re-spacing is still refused. Compared by cross-
    ///     multiplication on the beat values rather than by dividing, so it stays
    ///     exact.
    /// </summary>
    private static bool RhythmMatches(RhythmRule rule, List<SoundRef> template, List<SoundRef> window)
    {
        if (rule == RhythmRule.Ignore) return true;

        if (rule == RhythmRule.Exact)
        {
            for (var i = 1; i < template.Count; i++)
            {
                if (!(template[i].Position - template[0].Position)
                        .Equals(window[i].Position - window[0].Position))
                {
                    return false;
                }
            }

            return true;
        }

        double? ratio = null;
        for (var i = 1; i < template.Count; i++)
        {
            var templateGap = (template[i].Position - template[0].Position).DoubleValue;
            var windowGap = (window[i].Position - window[0].Position).DoubleValue;

            if (Math.Abs(templateGap) < 1e-9)
            {
                // Simultaneous in the template must stay simultaneous.
                if (Math.Abs(windowGap) >= 1e-9) return false;
                continue;
            }

            if (Math.Abs(windowGap) < 1e-9) return false;

            var thisRatio = windowGap / templateGap;
            if (thisRatio <= 0) return false;
            if (ratio == null) ratio = thisRatio;
            else if (Math.Abs(thisRatio - ratio.Value) > 1e-6) return false;
        }

        return true;
    }

    /// <summary>One occurrence, split back into the typed lists the selection
    ///     model speaks.</summary>
    private static PatternMatchGroup GroupOf(IReadOnlyList<SoundRef> sounds)
    {
        var notes = new List<GuitarNote>();
        var chords = new List<Chord>();
        var position = sounds[0].Position;

        foreach (var sound in sounds)
        {
            if (sound.Position < position) position = sound.Position;
            switch (sound.Element)
            {
                case GuitarNote note:
                    notes.Add(note);
                    break;
                case Chord chord:
                    chords.Add(chord);
                    break;
            }
        }

        return new PatternMatchGroup(position, notes, chords, [], []);
    }

    // ----------------------------------------------------------- hand shapes

    private static void MatchHandShapes(
        MatchPattern pattern, PatternSeed seed, PatternSource source, RangeGate gate,
        List<PatternMatchGroup> output)
    {
        if (!pattern.Scope.HasFlag(PatternScope.HandShapes)) return;
        if (seed.HandShapes.Count == 0) return;

        var seedSignatures = new List<Signature>();
        foreach (var handShape in seed.HandShapes)
        {
            var signature = BuildHandShapeSignature(pattern, handShape, source.Arrangement);
            if (signature != null) AddDistinct(seedSignatures, signature);
        }

        if (seedSignatures.Count == 0) return;

        var seedSet = new HashSet<HandShape>(seed.HandShapes);
        foreach (var handShape in source.HandShapes)
        {
            if (seedSet.Contains(handShape)) continue;
            if (!gate.Allows(handShape.Position)) continue;
            var signature = BuildHandShapeSignature(pattern, handShape, source.Arrangement);
            if (signature == null) continue;

            foreach (var seedSignature in seedSignatures)
            {
                if (!seedSignature.Matches(signature)) continue;
                output.Add(new PatternMatchGroup(handShape.Position, [], [], [handShape], []));
                break;
            }
        }
    }

    // --------------------------------------------------------------- anchors

    private static void MatchAnchors(
        MatchPattern pattern, PatternSeed seed, PatternSource source, RangeGate gate,
        List<PatternMatchGroup> output)
    {
        if (!pattern.Scope.HasFlag(PatternScope.Anchors)) return;
        if (seed.Anchors.Count == 0) return;

        var seedSignatures = new List<Signature>();
        foreach (var anchor in seed.Anchors)
            AddDistinct(seedSignatures, BuildAnchorSignature(pattern, anchor, source));

        var seedSet = new HashSet<FretAnchor>(seed.Anchors);
        foreach (var anchor in source.Anchors)
        {
            if (seedSet.Contains(anchor)) continue;
            if (!gate.Allows(anchor.Position)) continue;
            var signature = BuildAnchorSignature(pattern, anchor, source);

            foreach (var seedSignature in seedSignatures)
            {
                if (!seedSignature.Matches(signature)) continue;
                output.Add(new PatternMatchGroup(anchor.Position, [], [], [], [anchor]));
                break;
            }
        }
    }

    // ------------------------------------------------------------ companions

    /// <summary>
    ///     Drags the related elements along with each occurrence. A chord, the
    ///     hand shape drawn over it and the FHP anchor holding it are ONE authored
    ///     decision — re-fingering the chord alone leaves the other two describing
    ///     a shape that is gone — so a pattern has to be able to select
    ///     the set, not just the sound.
    /// </summary>
    private static List<PatternMatchGroup> AddCompanions(
        MatchPattern pattern, PatternSeed seed, PatternSource source, List<PatternMatchGroup> groups)
    {
        var seedSet = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var note in seed.Notes) seedSet.Add(note);
        foreach (var chord in seed.Chords) seedSet.Add(chord);
        foreach (var handShape in seed.HandShapes) seedSet.Add(handShape);
        foreach (var anchor in seed.Anchors) seedSet.Add(anchor);

        var expanded = new List<PatternMatchGroup>(groups.Count);

        foreach (var group in groups)
        {
            var handShapes = new List<HandShape>(group.HandShapes);
            var anchors = new List<FretAnchor>(group.Anchors);
            var notes = new List<GuitarNote>(group.Notes);
            var chords = new List<Chord>(group.Chords);

            var soundPositions = new List<FractionalPosition>(group.Notes.Count + group.Chords.Count);
            foreach (var note in group.Notes) soundPositions.Add(note.Position);
            foreach (var chord in group.Chords) soundPositions.Add(chord.Position);

            if (pattern.Companions.HasFlag(PatternCompanions.CoveringHandShape))
            {
                foreach (var position in soundPositions)
                {
                    foreach (var handShape in source.HandShapes)
                    {
                        if (!handShape.Covers(position)) continue;
                        if (seedSet.Contains(handShape) || handShapes.Contains(handShape)) continue;
                        handShapes.Add(handShape);
                    }
                }
            }

            if (pattern.Companions.HasFlag(PatternCompanions.GoverningAnchor))
            {
                foreach (var position in soundPositions)
                {
                    var anchor = GoverningAnchor(position, source.Anchors);
                    if (anchor == null) continue;
                    if (seedSet.Contains(anchor) || anchors.Contains(anchor)) continue;
                    anchors.Add(anchor);
                }
            }

            if (pattern.Companions.HasFlag(PatternCompanions.SoundsInside))
            {
                foreach (var handShape in group.HandShapes)
                    CollectSoundsIn(source, handShape.Position, handShape.EndPosition, seedSet, notes, chords);

                foreach (var anchor in group.Anchors)
                {
                    CollectSoundsIn(source, anchor.Position, NextAnchorPosition(anchor, source.Anchors),
                        seedSet, notes, chords);
                }
            }

            expanded.Add(group.With(handShapes, anchors, notes, chords));
        }

        return expanded;
    }

    private static void CollectSoundsIn(
        PatternSource source, FractionalPosition start, FractionalPosition? end,
        HashSet<object> seedSet, List<GuitarNote> notes, List<Chord> chords)
    {
        foreach (var note in source.Notes)
        {
            if (!note.Position.IsWithin(start, end)) continue;
            if (seedSet.Contains(note) || notes.Contains(note)) continue;
            notes.Add(note);
        }

        foreach (var chord in source.Chords)
        {
            if (!chord.Position.IsWithin(start, end)) continue;
            if (seedSet.Contains(chord) || chords.Contains(chord)) continue;
            chords.Add(chord);
        }
    }

    /// <summary>The last anchor at or before <paramref name="position" /> — the
    ///     hand position a sound is actually played under.</summary>
    private static FretAnchor? GoverningAnchor(FractionalPosition position, IReadOnlyList<FretAnchor> anchors)
    {
        FretAnchor? best = null;
        foreach (var anchor in anchors)
        {
            if (anchor.Position > position) continue;
            if (best == null || anchor.Position > best.Position) best = anchor;
        }

        return best;
    }

    private static FractionalPosition? NextAnchorPosition(FretAnchor anchor, IReadOnlyList<FretAnchor> anchors)
    {
        FractionalPosition? next = null;
        foreach (var other in anchors)
        {
            if (other.Position <= anchor.Position) continue;
            if (next == null || other.Position < next.Value) next = other.Position;
        }

        return next;
    }

    // ------------------------------------------------------------- the range

    /// <summary>
    ///     Which stretches of the song a pattern may look in. "Every chorus and
    ///     nothing else" is a charting request, not a search refinement: it is the
    ///     difference between fixing one passage and rewriting the whole chart.
    /// </summary>
    private sealed class RangeGate
    {
        private readonly List<(FractionalPosition Start, FractionalPosition? End)>? _spans;

        private RangeGate(List<(FractionalPosition Start, FractionalPosition? End)>? spans) => _spans = spans;

        public static RangeGate Unrestricted => new(null);

        public bool Allows(FractionalPosition position)
        {
            if (_spans == null) return true;
            foreach (var (start, end) in _spans)
            {
                if (position.IsWithin(start, end))
                    return true;
            }

            return false;
        }

        public static RangeGate Build(MatchPattern pattern, PatternSeed seed, PatternSource source)
        {
            if (pattern.Range == PatternRange.Anywhere) return Unrestricted;

            var bySection = pattern.Range == PatternRange.SameSection;
            var regions = Regions(source.EventPoints, bySection);
            // No structure authored at all: refusing everything would look like a
            // broken pattern, so an absent vocabulary means no restriction.
            if (regions.Count == 0) return Unrestricted;

            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var position in SeedPositions(seed))
            {
                var name = NameAt(regions, position);
                if (name != null) wanted.Add(name);
            }

            if (wanted.Count == 0) return Unrestricted;

            var spans = new List<(FractionalPosition, FractionalPosition?)>();
            foreach (var region in regions)
            {
                if (wanted.Contains(region.Name))
                    spans.Add((region.Start, region.End));
            }

            return new RangeGate(spans);
        }

        private static IEnumerable<FractionalPosition> SeedPositions(PatternSeed seed)
        {
            foreach (var note in seed.Notes) yield return note.Position;
            foreach (var chord in seed.Chords) yield return chord.Position;
            foreach (var handShape in seed.HandShapes) yield return handShape.Position;
            foreach (var anchor in seed.Anchors) yield return anchor.Position;
        }

        private static string? NameAt(List<Region> regions, FractionalPosition position)
        {
            foreach (var region in regions)
            {
                if (position.IsWithin(region.Start, region.End))
                    return region.Name;
            }

            return null;
        }

        /// <summary>A named region runs from its marker to the NEXT marker that
        ///     names the same thing — the last one runs to the end of the song.</summary>
        private static List<Region> Regions(IReadOnlyList<EventPoint> eventPoints, bool bySection)
        {
            var markers = new List<(FractionalPosition Position, string Name)>();
            foreach (var point in eventPoints)
            {
                var name = bySection ? point.Section : point.Phrase;
                if (!string.IsNullOrWhiteSpace(name)) markers.Add((point.Position, name.Trim()));
            }

            markers.Sort(static (a, b) => a.Position.CompareTo(b.Position));

            var regions = new List<Region>(markers.Count);
            for (var i = 0; i < markers.Count; i++)
            {
                regions.Add(new Region(markers[i].Name, markers[i].Position,
                    i + 1 < markers.Count ? markers[i + 1].Position : null));
            }

            return regions;
        }

        private readonly record struct Region(string Name, FractionalPosition Start, FractionalPosition? End);
    }

    // ------------------------------------------------------------ signatures

    private static Signature BuildSoundSignature(
        MatchPattern pattern, SoundRef sound, Arrangement? arrangement, Origin origin)
    {
        var key = new StringBuilder(64);

        if (pattern.MatchSoundKind) key.Append(sound.IsChord ? "c|" : "n|");

        var members = sound.Members;
        AppendMembers(key, pattern, members.Count,
            i => members[i].StringIndex, i => members[i].Fret, origin);

        if (pattern.Sustain != SustainRule.Ignore) key.Append(SustainKey(pattern.Sustain, sound.Sustain));

        if (pattern.MatchBeatOffset) key.Append(BeatOffsetKey(sound.Position));

        var template = ResolveTemplate(arrangement, sound.TemplateId);
        if (pattern.MatchChordName) key.Append(template?.Name ?? string.Empty).Append('|');
        // IsArpeggio, not one flag: two flags spell arpeggio and each import
        // path sets only one, so a single-flag key made every RS-XML arpeggio
        // sort with the plain shapes.
        if (pattern.MatchArpeggio) key.Append(template is { IsArpeggio: true } ? "arp|" : "-|");

        key.Append(TechniqueKey(pattern.Techniques, sound));

        return new Signature(key.ToString(), pattern.MatchFingering
            ? FingersOf(template, sound.FretsByString())
            : null);
    }

    private static Signature? BuildHandShapeSignature(
        MatchPattern pattern, HandShape handShape, Arrangement? arrangement)
    {
        var template = ResolveTemplate(arrangement, handShape.TemplateId);
        // A hand shape has no notes of its own — its template IS its shape, so a
        // shape whose template cannot be resolved has no signature at all rather
        // than an empty one that would match every other unresolvable shape.
        if (template == null || template.Frets.Count == 0) return null;

        var frets = new Dictionary<int, int>(template.Frets);
        var strings = new List<int>(frets.Keys);
        strings.Sort();

        var origin = new Origin(
            pattern.Fret == FretRule.Transposable ? LowestFretted(frets.Values) : 0,
            pattern.Strings == StringRule.Relative && strings.Count > 0 ? strings[0] : 0);

        var key = new StringBuilder(48);
        key.Append("h|");
        AppendMembers(key, pattern, strings.Count, i => strings[i], i => frets[strings[i]], origin);

        if (pattern.MatchBeatOffset) key.Append(BeatOffsetKey(handShape.Position));
        if (pattern.MatchChordName) key.Append(template.Name).Append('|');
        if (pattern.MatchArpeggio) key.Append(template.IsArpeggio ? "arp|" : "-|");

        return new Signature(key.ToString(), pattern.MatchFingering ? FingersOf(template, frets) : null);
    }

    private static Signature BuildAnchorSignature(MatchPattern pattern, FretAnchor anchor, PatternSource source)
    {
        var key = new StringBuilder(48);
        key.Append("a|");

        // A transposable (or fret-ignoring) anchor pattern is asking "the same
        // hand JOB anywhere on the neck", so the anchor's own fret drops out —
        // the governed span below is already expressed relative to it.
        if (pattern.Fret == FretRule.Exact)
            key.Append(anchor.Fret.ToString(CultureInfo.InvariantCulture)).Append('|');

        if (pattern.MatchAnchorWidth) key.Append(anchor.Width.ToString(CultureInfo.InvariantCulture)).Append('|');
        if (pattern.MatchBeatOffset) key.Append(BeatOffsetKey(anchor.Position));

        if (pattern.MatchAnchorSpan)
        {
            var governed = GovernedSpan(anchor, source);
            key.Append(string.Join(',', governed)).Append('|');
        }

        return new Signature(key.ToString(), null);
    }

    /// <summary>The (string, fret) members of an element, under whichever string
    ///     and fret rules the pattern asks for.</summary>
    private static void AppendMembers(
        StringBuilder key, MatchPattern pattern, int count,
        Func<int, int> stringAt, Func<int, int> fretAt, Origin origin)
    {
        if (pattern.Strings != StringRule.Ignore || pattern.Fret != FretRule.Ignore)
        {
            var parts = new List<string>(count);
            for (var i = 0; i < count; i++)
            {
                var stringPart = pattern.Strings switch
                {
                    StringRule.Exact => stringAt(i).ToString(CultureInfo.InvariantCulture),
                    StringRule.Relative => "r" + (stringAt(i) - origin.String).ToString(CultureInfo.InvariantCulture),
                    _ => string.Empty
                };
                parts.Add(stringPart + ":" + FretKey(pattern.Fret, fretAt(i), origin.Fret));
            }

            parts.Sort(StringComparer.Ordinal);
            key.Append(string.Join(',', parts)).Append('|');
        }

        if (pattern.Strings != StringRule.Ignore || pattern.MatchSize)
            key.Append(count.ToString(CultureInfo.InvariantCulture)).Append('|');
    }

    /// <summary>Every sound from this anchor up to the NEXT anchor, expressed
    ///     relative to it — the run the hand is actually holding this position
    ///     for.</summary>
    private static List<string> GovernedSpan(FretAnchor anchor, PatternSource source)
    {
        var spanEnd = NextAnchorPosition(anchor, source.Anchors);
        var governed = new List<string>();

        foreach (var note in source.Notes)
        {
            if (!note.Position.IsWithin(anchor.Position, spanEnd)) continue;
            governed.Add(SpanEntry(note.Position - anchor.Position, note.StringIndex, note.Fret - anchor.Fret));
        }

        foreach (var chord in source.Chords)
        {
            if (!chord.Position.IsWithin(anchor.Position, spanEnd)) continue;
            foreach (var chordNote in chord.ChordNotes)
            {
                governed.Add(SpanEntry(chord.Position - anchor.Position, chordNote.StringIndex,
                    chordNote.Fret - anchor.Fret));
            }
        }

        governed.Sort(StringComparer.Ordinal);
        return governed;
    }

    private static string SpanEntry(FractionalPosition offset, int stringIndex, int relativeFret) =>
        $"{Frac(offset)}/{stringIndex.ToString(CultureInfo.InvariantCulture)}/" +
        relativeFret.ToString(CultureInfo.InvariantCulture);

    // ----------------------------------------------------------- facet pieces

    /// <summary>What a transposable/relative comparison is measured FROM.</summary>
    private readonly record struct Origin(int Fret, int String)
    {
        public static Origin Of(MatchPattern pattern, IReadOnlyList<SoundRef> group) => new(
            pattern.Fret == FretRule.Transposable ? LowestFrettedFret(group) : 0,
            pattern.Strings == StringRule.Relative ? LowestString(group) : 0);
    }

    private readonly record struct SeedEntry(Signature Signature, int LowestFret);

    /// <summary>
    ///     The lowest FRETTED fret of a group — the reference a transposable
    ///     pattern measures from, and the neck position a fret-distance limit is
    ///     measured against. Open strings are excluded because an open string does
    ///     not move with the hand; a shape that contains one is only ever alike
    ///     another shape with the same string open.
    /// </summary>
    private static int LowestFrettedFret(IReadOnlyList<SoundRef> group)
    {
        var lowest = 0;
        foreach (var sound in group)
        {
            foreach (var member in sound.Members)
            {
                if (member.Fret <= 0) continue;
                if (lowest == 0 || member.Fret < lowest) lowest = member.Fret;
            }
        }

        return lowest;
    }

    private static int LowestFrettedFret(SoundRef sound) => LowestFrettedFret([sound]);

    private static int LowestString(IReadOnlyList<SoundRef> group)
    {
        var lowest = int.MaxValue;
        foreach (var sound in group)
        {
            foreach (var member in sound.Members)
            {
                if (member.StringIndex < lowest)
                    lowest = member.StringIndex;
            }
        }

        return lowest == int.MaxValue ? 0 : lowest;
    }

    private static int LowestFretted(IEnumerable<int> frets)
    {
        var lowest = 0;
        foreach (var fret in frets)
        {
            if (fret <= 0) continue;
            if (lowest == 0 || fret < lowest) lowest = fret;
        }

        return lowest;
    }

    /// <summary>
    ///     "The same grip, but only around here." Two all-open elements (both
    ///     lowest frets 0) are always within range — there is no neck position to
    ///     compare.
    /// </summary>
    private static bool WithinFretDistance(MatchPattern pattern, int seedFret, int candidateFret)
    {
        if (pattern.MaxFretDistance <= 0) return true;
        if (seedFret == 0 || candidateFret == 0) return true;
        return Math.Abs(seedFret - candidateFret) <= pattern.MaxFretDistance;
    }

    private static string FretKey(FretRule rule, int fret, int transposeBase) => rule switch
    {
        FretRule.Ignore => string.Empty,
        FretRule.Exact => fret.ToString(CultureInfo.InvariantCulture),
        // "o" and "f" keep an open string from ever colliding with the fretted
        // note that happens to sit on the transposition base.
        _ => fret <= 0 ? "o" : "f" + (fret - transposeBase).ToString(CultureInfo.InvariantCulture)
    };

    private static string SustainKey(SustainRule rule, FractionalPosition sustain) => rule switch
    {
        SustainRule.SameLength => Frac(sustain) + "|",
        SustainRule.SameKind => (sustain.BeatId == 0 && sustain.Fraction.Numerator == 0 ? "short" : "held") + "|",
        _ => string.Empty
    };

    /// <summary>Where in its beat the sound sits — the fractional part only, so it
    ///     needs neither a beat map nor a time signature.</summary>
    private static string BeatOffsetKey(FractionalPosition position) =>
        $"{position.Fraction.Numerator.ToString(CultureInfo.InvariantCulture)}/" +
        $"{position.Fraction.Denominator.ToString(CultureInfo.InvariantCulture)}|";

    private static string TechniqueKey(TechniqueFacet facets, SoundRef sound)
    {
        if (facets == TechniqueFacet.None) return string.Empty;

        var key = new StringBuilder(32);
        var members = sound.Members;

        void Fold(TechniqueFacet facet, Func<Member, string> project)
        {
            if (!facets.HasFlag(facet)) return;
            var values = new List<string>(members.Count);
            foreach (var member in members)
            {
                var value = project(member);
                if (!values.Contains(value)) values.Add(value);
            }

            values.Sort(StringComparer.Ordinal);
            key.Append(string.Join('+', values)).Append('|');
        }

        Fold(TechniqueFacet.Mute, static m => m.Sound.Mute.ToString());
        Fold(TechniqueFacet.Hopo, static m => m.Sound.Hopo.ToString());
        Fold(TechniqueFacet.Harmonic, static m => m.Sound.Harmonic.ToString());

        // The bend curve is compared RELATIVE to the note's own onset.
        // BendValue.Position is absolute (it is clamped against the note's end
        // position), so comparing it raw would make one bend alike only to a bend
        // at the same instant in the song — i.e. never.
        var onset = sound.Position;
        Fold(TechniqueFacet.Bend, m => BendKey(m.Sound, onset));
        Fold(TechniqueFacet.Slide, static m => SlideKey(m));
        Fold(TechniqueFacet.Vibrato, static m => m.Sound.Vibrato ? m.Sound.VibratoStrength.ToString() : "-");
        Fold(TechniqueFacet.Tremolo, static m => m.Sound.Tremolo ? "1" : "0");
        Fold(TechniqueFacet.BassPicking, static m => m.Sound.BassPicking.ToString());
        Fold(TechniqueFacet.LinkNext, static m => m.Sound.LinkNext ? "1" : "0");

        // Accent, Ignore and PassOtherNotes also live on the CHORD itself, so they
        // are folded at the sound level rather than per string.
        if (facets.HasFlag(TechniqueFacet.Accent)) key.Append(sound.Accent ? "1|" : "0|");
        if (facets.HasFlag(TechniqueFacet.Ignored)) key.Append(sound.Ignored ? "1|" : "0|");
        if (facets.HasFlag(TechniqueFacet.PassOtherNotes)) key.Append(sound.PassOtherNotes ? "1|" : "0|");

        return key.ToString();
    }

    /// <summary>A bend as its whole curve, measured from the note's own onset —
    ///     the same shape at the same points, not merely "has a bend".</summary>
    private static string BendKey(IGuitarSound sound, FractionalPosition onset)
    {
        if (sound.BendValues.Count == 0) return "-";
        var parts = new List<string>(sound.BendValues.Count);
        foreach (var bend in sound.BendValues)
            parts.Add($"{Frac(bend.Position - onset)}@{bend.Step.ToString("0.###", CultureInfo.InvariantCulture)}");
        return string.Join(';', parts);
    }

    /// <summary>A slide as its DISTANCE and direction, never its destination fret —
    ///     a slide is a movement, so it survives transposition by construction.</summary>
    private static string SlideKey(Member member)
    {
        if (member.Sound.SlideTo is not { } target) return "-";
        return (target - member.Fret).ToString(CultureInfo.InvariantCulture)
               + (member.Sound.UnpitchedSlide ? "u" : "p");
    }

    private static ChordTemplate? ResolveTemplate(Arrangement? arrangement, int templateId)
        => arrangement == null || templateId < 0 ? null : ChordTemplateResolver.FindById(arrangement, templateId);

    /// <summary>
    ///     The template's fingering restricted to the strings actually played, and
    ///     only when the template still describes them — a drifted template's
    ///     fingering belongs to some other shape.
    /// </summary>
    private static Dictionary<int, int> FingersOf(ChordTemplate? template, Dictionary<int, int> frets)
    {
        var fingers = new Dictionary<int, int>();
        if (template == null) return fingers;
        if (template.Frets.Count != frets.Count) return fingers;
        foreach (var (stringIndex, fret) in frets)
        {
            if (!template.Frets.TryGetValue(stringIndex, out var other) || other != fret)
                return fingers;
        }

        foreach (var (stringIndex, finger) in template.Fingers)
        {
            if (frets.ContainsKey(stringIndex))
                fingers[stringIndex] = finger;
        }

        return fingers;
    }

    private static string Frac(FractionalPosition position) =>
        $"{position.BeatId.ToString(CultureInfo.InvariantCulture)}:" +
        $"{position.Fraction.Numerator.ToString(CultureInfo.InvariantCulture)}/" +
        position.Fraction.Denominator.ToString(CultureInfo.InvariantCulture);

    private static void AddDistinct(List<Signature> signatures, Signature? candidate)
    {
        if (candidate == null) return;
        foreach (var existing in signatures)
        {
            if (existing.SameAs(candidate))
                return;
        }

        signatures.Add(candidate);
    }

    private static int CompareSounds(SoundRef a, SoundRef b)
    {
        var byPosition = a.Position.CompareTo(b.Position);
        if (byPosition != 0) return byPosition;
        var byKind = a.IsChord.CompareTo(b.IsChord);
        return byKind != 0 ? byKind : a.LowestStringIndex.CompareTo(b.LowestStringIndex);
    }

    // ----------------------------------------------------------------- types

    /// <summary>One string of one sound: where the hand is, and the sound whose
    ///     technique flags apply to it.</summary>
    private readonly record struct Member(int StringIndex, int Fret, IGuitarSound Sound);

    /// <summary>
    ///     A note or a chord behind one shape, so the matcher never branches on
    ///     which it has. A note is a one-member sound; a chord is one member per
    ///     chord note.
    /// </summary>
    private sealed class SoundRef
    {
        private SoundRef(object element, bool isChord, FractionalPosition position,
            FractionalPosition sustain, int templateId, bool accent, bool ignored, bool passOtherNotes,
            List<Member> members)
        {
            Element = element;
            IsChord = isChord;
            Position = position;
            Sustain = sustain;
            TemplateId = templateId;
            Accent = accent;
            Ignored = ignored;
            PassOtherNotes = passOtherNotes;
            Members = members;
        }

        public object Element { get; }
        public bool IsChord { get; }
        public FractionalPosition Position { get; }
        public FractionalPosition Sustain { get; }
        public int TemplateId { get; }
        public bool Accent { get; }
        public bool Ignored { get; }
        public bool PassOtherNotes { get; }
        public IReadOnlyList<Member> Members { get; }

        public int LowestStringIndex
        {
            get
            {
                var lowest = int.MaxValue;
                foreach (var member in Members)
                {
                    if (member.StringIndex < lowest)
                        lowest = member.StringIndex;
                }

                return lowest == int.MaxValue ? 0 : lowest;
            }
        }

        public static SoundRef Of(GuitarNote note) => new(
            note, false, note.Position, note.Sustain, -1, note.Accent, note.Ignore, note.PassOtherNotes,
            [new Member(note.StringIndex, note.Fret, note)]);

        public static SoundRef Of(Chord chord)
        {
            var members = new List<Member>(chord.ChordNotes.Count);
            var accent = chord.Accent;
            var ignored = chord.Ignore;
            foreach (var chordNote in chord.ChordNotes)
            {
                members.Add(new Member(chordNote.StringIndex, chordNote.Fret, chordNote));
                accent |= chordNote.Accent;
                ignored |= chordNote.Ignore;
            }

            return new SoundRef(chord, true, chord.Position, chord.GetMaxSustain(), chord.TemplateId,
                accent, ignored, chord.PassOtherNotes, members);
        }

        public Dictionary<int, int> FretsByString()
        {
            var frets = new Dictionary<int, int>(Members.Count);
            foreach (var member in Members) frets[member.StringIndex] = member.Fret;
            return frets;
        }
    }

    /// <summary>
    ///     A canonical key plus the fingering that cannot go in it. Fingers are
    ///     hole-tolerant — they only have to agree where BOTH sides authored one —
    ///     which makes agreement non-transitive and therefore un-hashable.
    /// </summary>
    private sealed class Signature
    {
        private readonly Dictionary<int, int>? _fingers;
        private readonly string _key;

        public Signature(string key, Dictionary<int, int>? fingers)
        {
            _key = key;
            _fingers = fingers;
        }

        public bool Matches(Signature? other)
        {
            if (other == null) return false;
            if (!string.Equals(_key, other._key, StringComparison.Ordinal)) return false;
            if (_fingers == null || other._fingers == null) return true;

            foreach (var (stringIndex, finger) in _fingers)
            {
                if (other._fingers.TryGetValue(stringIndex, out var otherFinger) && otherFinger != finger)
                    return false;
            }

            return true;
        }

        /// <summary>Exact equality — used only to dedupe the seed list, never to
        ///     match.</summary>
        public bool SameAs(Signature other)
        {
            if (!string.Equals(_key, other._key, StringComparison.Ordinal)) return false;
            if (_fingers == null || other._fingers == null) return _fingers == null && other._fingers == null;
            if (_fingers.Count != other._fingers.Count) return false;
            foreach (var (stringIndex, finger) in _fingers)
            {
                if (!other._fingers.TryGetValue(stringIndex, out var otherFinger) || otherFinger != finger)
                    return false;
            }

            return true;
        }
    }
}
