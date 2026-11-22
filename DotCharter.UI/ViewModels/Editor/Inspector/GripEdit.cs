using DotCharter.Services.Editing.ChordShapes;

namespace DotCharter.UI.ViewModels.Editor.Inspector;

internal static class GripEdit
{
    internal static int Ghosts(HandShape shape, ChordTemplate grip, Func<HandShape, ChordTemplate, int> inferGhosts) =>
        shape.GhostMask ?? (grip.GhostMask != 0 ? grip.GhostMask : inferGhosts(shape, grip));

    internal static Chord? Refret(Chord chord, IReadOnlyDictionary<int, int> frets)
    {
        var changed = chord.ChordNotes.Any(note => note.Mute != MuteType.Full &&
            frets.TryGetValue(note.StringIndex, out var fret) && fret != note.Fret);
        if (!changed) return null;

        var result = chord.Clone();
        foreach (var note in result.ChordNotes)
            if (note.Mute != MuteType.Full && frets.TryGetValue(note.StringIndex, out var fret)) note.Fret = fret;

        return result;
    }

    internal static bool MissingFingering(ChordTemplate grip, int capo) =>
        !grip.Fingerless && grip.Frets.Any(f => f.Value > capo && grip.GetFinger(f.Key) is < 0 or > 4);

    internal static ChordTemplate? AutoFill(Arrangement arrangement, ChordTemplate grip, int ghostMask)
    {
        if (grip.IsPinned || ghostMask != 0) return null;
        var next = grip.Clone();
        if (MissingFingering(grip, arrangement.Capo))
        {
            ArrangementFingeringStyle.SetSuggestedFingers(arrangement, next, arrangement.StringCount);
            foreach (var (index, finger) in grip.Fingers)
                if (finger is >= 0 and <= 4) next.Fingers[index] = finger;
        }
        if (next.Name.Length == 0 && !next.Unnamed) next.Name = NewChordNames.Suggest(arrangement, next.Frets);
        return next.Equals(grip) ? null : next;
    }

    internal static string FretText(ChordTemplate template, int strings)
    {
        var slots = Enumerable.Range(0, strings).Select(s => template.GetFret(s) is >= 0 and var fret
            ? fret.ToString(System.Globalization.CultureInfo.InvariantCulture) : "x").ToArray();
        return string.Join(slots.Any(s => s.Length > 1) ? " " : "", slots);
    }
}
