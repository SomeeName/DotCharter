namespace DotCharter.Services.Editing.HandShapes;

/// <summary>
///     Whether a note FITS a grip: it plays a string the grip does not hold, or the fret the grip
///     already holds there, and the grown grip still spans at most <see cref="MaxFretSpan" /> frets
///     between its lowest and highest fretted string (open strings ask nothing of the fret hand).
///     The tab importer's let-ring runs (<c>GpHandShapeBuilder</c>) and the hand-shape resize
///     (<c>TimelineWheelAdjuster.RebuildGrips</c>) both ask it, so a bar only ever grows its grip
///     over a note the same hand position plays.
/// </summary>
public static class HandShapeGripFit
{
    /// <summary>Frets between the lowest and highest FRETTED string of one grip, above which it is
    /// not one hand position.</summary>
    public const int MaxFretSpan = 5;

    /// <summary>
    ///     <paramref name="grip" /> grown by every (string, fret) in <paramref name="sounds" />, or
    ///     null when one of them re-frets a string the grip (or an earlier sound) holds, or the
    ///     result stretches past <see cref="MaxFretSpan" />.
    /// </summary>
    public static Dictionary<int, int>? Grow(IReadOnlyDictionary<int, int> grip,
        IEnumerable<(int StringIndex, int Fret)> sounds)
    {
        ArgumentNullException.ThrowIfNull(grip);
        ArgumentNullException.ThrowIfNull(sounds);
        var grown = new Dictionary<int, int>(grip);
        foreach (var (stringIndex, fret) in sounds)
        {
            if (!grown.TryAdd(stringIndex, fret) && grown[stringIndex] != fret) return null;
        }

        var fretted = grown.Values.Where(f => f > 0).ToList();
        return fretted.Count > 0 && fretted.Max() - fretted.Min() > MaxFretSpan ? null : grown;
    }

    /// <summary>Every (string, fret) a note or chord starting in [<paramref name="start" />,
    /// <paramref name="end" />) plays.</summary>
    public static IEnumerable<(int StringIndex, int Fret)> SoundsIn(IEnumerable<GuitarNote> notes,
        IEnumerable<Chord> chords, FractionalPosition start, FractionalPosition end)
    {
        ArgumentNullException.ThrowIfNull(notes);
        ArgumentNullException.ThrowIfNull(chords);
        foreach (var note in notes)
        {
            if (note.Position >= start && note.Position < end) yield return (note.StringIndex, note.Fret);
        }

        foreach (var chord in chords)
        {
            if (chord.Position < start || chord.Position >= end) continue;
            foreach (var cn in chord.ChordNotes) yield return (cn.StringIndex, cn.Fret);
        }
    }
}
