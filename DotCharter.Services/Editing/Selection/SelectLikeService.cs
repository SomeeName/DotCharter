using GuitarNote = DotCharter.Core.Domain.Guitar.GuitarNote;

namespace DotCharter.Services.Editing.Selection;

/// <summary>
/// Finds notes elsewhere in a track that match the shape of a given selection.
/// A "shape" is the set of (relative position, string, relative fret) signatures
/// of the template notes; matches preserve string layout and fret intervals while
/// optionally allowing the whole pattern to be transposed up or down the neck.
/// </summary>
public class SelectLikeService : ISelectLikeService
{
    private readonly IAppLogger _logger;

    public SelectLikeService(IAppLogger logger) => _logger = logger;

    public IReadOnlyList<GuitarNote> FindMatches(
        IReadOnlyList<GuitarNote> templateNotes,
        IEnumerable<GuitarNote> source,
        bool allowTransposition = true)
    {
        if (templateNotes.Count == 0) return Array.Empty<GuitarNote>();

        var sourceList = source as IReadOnlyList<GuitarNote> ?? source.ToList();
        if (sourceList.Count == 0) return Array.Empty<GuitarNote>();

        // Try strict (techniques must match) first; if that finds nothing, fall
        // back to a loose match keyed only on string + relative fret + relative
        // position. The strict rule was rejecting common cases (e.g. repeated
        // power chords whose computed PassOtherNotes flag flips with neighbours).
        var strict = FindMatchesInternal(templateNotes, sourceList, allowTransposition, requireTechniques: true);
        if (strict.Count > 0)
        {
            _logger.Info(AppLogCategory.Input,
                $"Select-like (strict) found {strict.Count} matching note(s) for template of {templateNotes.Count} (transposition={allowTransposition}).");
            return strict;
        }

        var loose = FindMatchesInternal(templateNotes, sourceList, allowTransposition, requireTechniques: false);
        _logger.Info(AppLogCategory.Input,
            $"Select-like (loose) found {loose.Count} matching note(s) for template of {templateNotes.Count} (transposition={allowTransposition}).");
        return loose;
    }

    /// <inheritdoc />
    public LikeShapeMatches FindLikeShapes(
        Arrangement? arrangement,
        IReadOnlyList<Chord> seedChords,
        IReadOnlyList<HandShape> seedHandShapes,
        IEnumerable<Chord> chords,
        IEnumerable<HandShape> handShapes)
    {
        ArgumentNullException.ThrowIfNull(seedChords);
        ArgumentNullException.ThrowIfNull(seedHandShapes);
        ArgumentNullException.ThrowIfNull(chords);
        ArgumentNullException.ThrowIfNull(handShapes);

        var seeds = new List<ChordShapeSignature>();
        foreach (var chord in seedChords) AddSeed(seeds, ChordShapeSignature.ForChord(chord, arrangement));
        foreach (var handShape in seedHandShapes)
            AddSeed(seeds, ChordShapeSignature.ForHandShape(handShape, arrangement));

        if (seeds.Count == 0)
        {
            _logger.Info(AppLogCategory.Input,
                "Select-like (shapes): no seed shape resolved from the selection.");
            return new LikeShapeMatches(Array.Empty<Chord>(), Array.Empty<HandShape>());
        }

        var seedChordSet = new HashSet<Chord>(seedChords);
        var seedHandShapeSet = new HashSet<HandShape>(seedHandShapes);

        var matchedChords = new List<Chord>();
        foreach (var chord in chords)
        {
            if (seedChordSet.Contains(chord)) continue;
            if (MatchesAnySeed(seeds, ChordShapeSignature.ForChord(chord, arrangement)))
                matchedChords.Add(chord);
        }

        var matchedHandShapes = new List<HandShape>();
        foreach (var handShape in handShapes)
        {
            if (seedHandShapeSet.Contains(handShape)) continue;
            if (MatchesAnySeed(seeds, ChordShapeSignature.ForHandShape(handShape, arrangement)))
                matchedHandShapes.Add(handShape);
        }

        _logger.Info(AppLogCategory.Input,
            $"Select-like (shapes) matched {matchedChords.Count} chord(s) and {matchedHandShapes.Count} hand shape(s) against {seeds.Count} seed shape(s).");
        return new LikeShapeMatches(matchedChords, matchedHandShapes);
    }

    /// <inheritdoc />
    public IReadOnlyList<FretAnchor> FindLikeAnchors(ChartLevel? level, IReadOnlyList<FretAnchor> seedAnchors)
    {
        ArgumentNullException.ThrowIfNull(seedAnchors);
        if (level == null || seedAnchors.Count == 0) return Array.Empty<FretAnchor>();

        var seeds = new List<AnchorSpanSignature>(seedAnchors.Count);
        foreach (var anchor in seedAnchors)
            seeds.Add(AnchorSpanSignature.Build(anchor, level));

        var seedSet = new HashSet<FretAnchor>(seedAnchors);
        var matches = new List<FretAnchor>();

        foreach (var anchor in level.Anchors)
        {
            if (seedSet.Contains(anchor)) continue;
            var candidate = AnchorSpanSignature.Build(anchor, level);
            foreach (var seed in seeds)
            {
                if (!seed.Matches(candidate)) continue;
                matches.Add(anchor);
                break;
            }
        }

        _logger.Info(AppLogCategory.Input,
            $"Select-like (anchors) matched {matches.Count} anchor(s) against {seeds.Count} seed position(s) " +
            $"(fret+width+span; the fret alone is deliberately not enough).");
        return matches;
    }

    private static void AddSeed(List<ChordShapeSignature> seeds, ChordShapeSignature? candidate)
    {
        if (candidate == null || candidate.IsEmpty) return;
        foreach (var seed in seeds)
        {
            if (seed.SameAs(candidate))
                return;
        }

        seeds.Add(candidate);
    }

    private static bool MatchesAnySeed(List<ChordShapeSignature> seeds, ChordShapeSignature? candidate)
    {
        if (candidate == null || candidate.IsEmpty) return false;
        foreach (var seed in seeds)
        {
            if (seed.Matches(candidate))
                return true;
        }

        return false;
    }

    private static IReadOnlyList<GuitarNote> FindMatchesInternal(
        IReadOnlyList<GuitarNote> templateNotes,
        IReadOnlyList<GuitarNote> sourceList,
        bool allowTransposition,
        bool requireTechniques)
    {
        var templateSorted = templateNotes.OrderBy(n => n.Position).ToList();
        var anchorTemplate = templateSorted[0];
        var signatures = templateSorted.Select(n => new NoteSignature(
            n.Position - anchorTemplate.Position,
            n.StringIndex,
            n.Fret - anchorTemplate.Fret,
            n)).ToList();

        var templateSet = new HashSet<GuitarNote>(templateNotes);
        var positionIndex = BuildPositionIndex(sourceList);
        var matches = new List<GuitarNote>();
        var visited = new HashSet<GuitarNote>();

        foreach (var candidate in sourceList)
        {
            if (candidate.StringIndex != anchorTemplate.StringIndex) continue;
            if (!allowTransposition && candidate.Fret != anchorTemplate.Fret) continue;

            var candidateMatches = TryMatchAt(candidate, signatures, positionIndex, requireTechniques);
            if (candidateMatches == null) continue;

            foreach (var note in candidateMatches)
            {
                if (templateSet.Contains(note)) continue;
                if (visited.Add(note)) matches.Add(note);
            }
        }

        return matches;
    }

    private static List<GuitarNote>? TryMatchAt(
        GuitarNote anchor,
        IReadOnlyList<NoteSignature> signatures,
        Dictionary<PositionKey, List<GuitarNote>> positionIndex,
        bool requireTechniques)
    {
        var matched = new List<GuitarNote>(signatures.Count);
        foreach (var sig in signatures)
        {
            var expectedPos = anchor.Position + sig.RelativePosition;
            if (!positionIndex.TryGetValue(new PositionKey(expectedPos), out var bucket))
                return null;

            var hit = bucket.FirstOrDefault(n =>
                n.StringIndex == sig.StringIndex &&
                n.Fret == anchor.Fret + sig.FretOffset &&
                (!requireTechniques || HasSameTechniques(n, sig.Template)));
            if (hit == null) return null;

            matched.Add(hit);
        }
        return matched;
    }

    // Two notes are considered "alike" only when their techniques match exactly.
    // Bend values, slide targets and sustain length are part of the shape too,
    // so a clean note never gets lumped in with a bent or sustained twin.
    private static bool HasSameTechniques(GuitarNote candidate, GuitarNote template)
    {
        if (candidate.Sustain != template.Sustain) return false;
        if (candidate.Mute != template.Mute) return false;
        if (candidate.Hopo != template.Hopo) return false;
        if (candidate.Harmonic != template.Harmonic) return false;
        if (candidate.BassPicking != template.BassPicking) return false;
        if (candidate.Vibrato != template.Vibrato) return false;
        if (candidate.VibratoStrength != template.VibratoStrength) return false;
        if (candidate.Tremolo != template.Tremolo) return false;
        if (candidate.Accent != template.Accent) return false;
        if (candidate.LinkNext != template.LinkNext) return false;
        if (candidate.PassOtherNotes != template.PassOtherNotes) return false;
        if (candidate.Ignore != template.Ignore) return false;
        if (candidate.UnpitchedSlide != template.UnpitchedSlide) return false;
        if (candidate.SlideTo.HasValue != template.SlideTo.HasValue) return false;
        if (candidate.BendValues.Count != template.BendValues.Count) return false;
        for (var i = 0; i < template.BendValues.Count; i++)
        {
            var t = template.BendValues[i];
            var c = candidate.BendValues[i];
            if (t.Position != c.Position) return false;
            if (Math.Abs(t.Step - c.Step) > 0.001f) return false;
        }
        return true;
    }

    private static Dictionary<PositionKey, List<GuitarNote>> BuildPositionIndex(IReadOnlyList<GuitarNote> notes)
    {
        var dict = new Dictionary<PositionKey, List<GuitarNote>>();
        foreach (var note in notes)
        {
            var key = new PositionKey(note.Position);
            if (!dict.TryGetValue(key, out var bucket))
            {
                bucket = [];
                dict[key] = bucket;
            }
            bucket.Add(note);
        }
        return dict;
    }

    private readonly record struct NoteSignature(
        FractionalPosition RelativePosition,
        int StringIndex,
        int FretOffset,
        GuitarNote Template);

    private readonly record struct PositionKey(FractionalPosition Position);
}
