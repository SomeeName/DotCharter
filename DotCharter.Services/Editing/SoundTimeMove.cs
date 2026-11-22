namespace DotCharter.Services.Editing;

/// <summary>Moves an onset and its absolute bend positions by the same beat delta.</summary>
internal static class SoundTimeMove
{
    public static void MoveTo(GuitarNote note, FractionalPosition position)
    {
        ShiftBends(note, position - note.Position);
        note.Position = position;
    }

    public static void MoveTo(Chord chord, FractionalPosition position)
    {
        var delta = position - chord.Position;
        foreach (var note in chord.ChordNotes) ShiftBends(note, delta);
        chord.Position = position;
    }

    private static void ShiftBends(IGuitarSound sound, FractionalPosition delta)
    {
        if (delta == FractionalPosition.Zero) return;
        foreach (var bend in sound.BendValues) bend.Position += delta;
    }
}
