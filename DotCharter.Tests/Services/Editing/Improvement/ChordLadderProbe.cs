namespace DotCharter.Tests.Services.Editing.Improvement;

/// <summary>
///     What a lower difficulty level plays where the hardest level has a chord (#490): the full
///     chord, a REDUCED chord (a proper subset of its strings on the same frets), a single note
///     on one of its strings, nothing, or something else (a different shape). Used by
///     <c>LocalChordLadderMeasurementTest</c> on official ladders and on the generator's.
/// </summary>
internal static class ChordLadderProbe
{
    public enum Outcome { Full, Reduced, Single, Nothing, Other }

    public sealed record Observation(Outcome Outcome, IReadOnlyList<int> KeptStrings, int? TemplateId);

    /// <summary>The outcome at <paramref name="full" />'s position on <paramref name="level" />.</summary>
    public static Observation Classify(ChartLevel level, Chord full)
    {
        var fullFrets = ChordTemplateResolverFrets(full);
        var chord = level.Chords.FirstOrDefault(c => c.Position == full.Position);
        if (chord != null)
        {
            var frets = ChordTemplateResolverFrets(chord);
            var kept = frets.Keys.OrderBy(s => s).ToList();
            var subset = frets.All(f => fullFrets.TryGetValue(f.Key, out var fret) && fret == f.Value);
            if (!subset) return new Observation(Outcome.Other, kept, chord.TemplateId);
            if (frets.Count == fullFrets.Count) return new Observation(Outcome.Full, kept, chord.TemplateId);
            return new Observation(frets.Count == 1 ? Outcome.Single : Outcome.Reduced, kept, chord.TemplateId);
        }

        var notes = level.Notes.Where(n => n.Position == full.Position).ToList();
        if (notes.Count == 0) return new Observation(Outcome.Nothing, [], null);
        var strings = notes.Select(n => n.StringIndex).OrderBy(s => s).ToList();
        var onShape = notes.All(n => fullFrets.TryGetValue(n.StringIndex, out var fret) && fret == n.Fret);
        if (!onShape) return new Observation(Outcome.Other, strings, null);
        return new Observation(notes.Count == 1 ? Outcome.Single : Outcome.Reduced, strings, null);
    }

    /// <summary>Interval label of each string of <paramref name="full" /> against its lowest
    /// string: R (the bass string), 8 (an octave of it), 5 (a fifth), 3 (a third), 7 (a seventh),
    /// x (anything else).</summary>
    public static Dictionary<int, char> IntervalLabels(Arrangement arrangement, Chord full)
    {
        var labels = new Dictionary<int, char>();
        var ordered = full.ChordNotes.OrderBy(n => n.StringIndex).ToList();
        if (ordered.Count == 0) return labels;
        var bass = SoundingPitch.MidiFor(arrangement, ordered[0].StringIndex, ordered[0].Fret) ?? 0;
        foreach (var note in ordered)
        {
            if (note.StringIndex == ordered[0].StringIndex)
            {
                labels[note.StringIndex] = 'R';
                continue;
            }

            var interval = (((SoundingPitch.MidiFor(arrangement, note.StringIndex, note.Fret) ?? 0) - bass) % 12 + 12) % 12;
            labels[note.StringIndex] = interval switch
            {
                0 => '8',
                7 => '5',
                3 or 4 => '3',
                10 or 11 => '7',
                _ => 'x'
            };
        }

        return labels;
    }

    /// <summary>The hand shapes on <paramref name="level" /> that cover <paramref name="position" />.</summary>
    public static List<HandShape> HandShapesAt(ChartLevel level, FractionalPosition position) =>
        level.HandShapes.Where(h => h.Position <= position && position < h.EndPosition).ToList();

    private static Dictionary<int, int> ChordTemplateResolverFrets(Chord chord)
    {
        var frets = new Dictionary<int, int>();
        foreach (var note in chord.ChordNotes) frets.TryAdd(note.StringIndex, note.Fret);
        return frets;
    }
}
