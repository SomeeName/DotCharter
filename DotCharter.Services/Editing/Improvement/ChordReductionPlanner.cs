namespace DotCharter.Services.Editing.Improvement;

/// <summary>
/// What a generated difficulty level plays for a chord of the hardest level: the full
///     chord, a REDUCED grip (a subset of its strings on the same frets), or its bass note.
///     <para>
///         <b>The ladder is measured, not guessed.</b> On 688 official dynamic-difficulty
///         arrangements (the base game plus official DLC, <c>LocalChordLadderMeasurementTest</c>)
///         the chance that a lower level keeps each string of a hardest-level chord — given that
///         the level plays the onset at all — rises with depth and falls with the string's place
///         above the bass, so the strings join in order: the bass string first (75–99 % at every
///         depth), then the next string up, and so on. <see cref="JoinDepth" /> is the depth at
///         which each string's measured keep rate crosses one half, which is the subset that
///         minimises the expected number of strings a charter has to add or remove to reach the
///         official level. The thinning of ONSETS is <see cref="LevelGenerationService" />'s; this
///         only decides what a kept chord onset plays.
///     </para>
///     <para>
///         <b>Templates.</b> A reduced grip resolves through
///         <see cref="ChordTemplateResolver.ResolveReduced" />: the source template's fingering on
///         the kept strings and its arpeggio flavour, never a mutation of the source (templates are
///         SHARED). Mints are collected in <see cref="Created" /> for the caller to add.
///     </para>
/// </summary>
internal sealed class ChordReductionPlanner(Arrangement arrangement)
{
    /// <summary>
    ///     <c>JoinDepth[size - 2][position - 1]</c>: the depth (0 = easiest level, 1 = hardest) from
    ///     which the string <c>position</c> places above the bass joins a <c>size</c>-string chord.
    ///     Linear crossings of one half between decile centres of the measured keep rates;
    ///     chords of more than six strings use the six-string row, and strings past
    ///     its end its last entry.
    /// </summary>
    private static readonly double[][] JoinDepth =
    [
        [0.73],
        [0.60, 0.69],
        [0.50, 0.61, 0.68],
        [0.31, 0.47, 0.54, 0.59],
        [0.41, 0.55, 0.64, 0.66, 0.68]
    ];

    private readonly Dictionary<int, ChordTemplate> _byId = ChordTemplateResolver.IndexById(arrangement.ChordTemplates);
    private readonly Dictionary<(int TemplateId, string Shape), ChordTemplate> _resolved = [];

    /// <summary>Templates minted for reduced grips, in mint order; none is in the arrangement yet.</summary>
    public List<ChordTemplate> Created { get; } = [];

    /// <summary>The string indices of <paramref name="chord" /> a level at <paramref name="depth" />
    /// keeps, ascending. The bass string always; all of them at depth 1.</summary>
    public static List<int> KeptStrings(Chord chord, double depth)
    {
        ArgumentNullException.ThrowIfNull(chord);
        var strings = chord.ChordNotes.Select(n => n.StringIndex).Distinct().Order().ToList();
        if (strings.Count <= 1 || depth >= 1) return strings;

        var row = JoinDepth[Math.Min(strings.Count, 6) - 2];
        var kept = new List<int>(strings.Count) { strings[0] };
        for (var position = 1; position < strings.Count; position++)
        {
            if (depth >= row[Math.Min(position, row.Length) - 1])
                kept.Add(strings[position]);
        }

        return kept;
    }

    /// <summary>
    ///     A reduced copy of <paramref name="chord" /> holding only <paramref name="keptStrings" />
    ///     (two or more, a proper subset), pointed at a template for exactly that grip. Every chord
    ///     flag and every kept chord note's own techniques are copied; <paramref name="chord" /> and
    ///     its template are not touched.
    /// </summary>
    public Chord Reduce(Chord chord, IReadOnlyCollection<int> keptStrings)
    {
        ArgumentNullException.ThrowIfNull(chord);
        ArgumentNullException.ThrowIfNull(keptStrings);
        var reduced = chord.Clone();
        for (var i = reduced.ChordNotes.Count - 1; i >= 0; i--)
        {
            if (!keptStrings.Contains(reduced.ChordNotes[i].StringIndex))
                reduced.ChordNotes.RemoveAt(i);
        }

        var frets = ChordTemplateResolver.FretsOf(reduced);
        var key = (chord.TemplateId, string.Join(",", frets.OrderBy(f => f.Key).Select(f => $"{f.Key}:{f.Value}")));
        if (!_resolved.TryGetValue(key, out var template))
        {
            _byId.TryGetValue(chord.TemplateId, out var source);
            template = ChordTemplateResolver.ResolveReduced(arrangement, source, frets, Created);
            _resolved[key] = template;
        }

        reduced.TemplateId = template.Id;
        return reduced;
    }
}
