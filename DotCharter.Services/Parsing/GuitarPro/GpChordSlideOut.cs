namespace DotCharter.Services.Parsing.GuitarPro;

/// <summary>
///     Keeps the shape of a chord whose fretted notes slide out without a target
///     pitch (GP slide flags 4 down / 8 up, GP3-4 values 3 / 4). Each note's
///     landing is first computed alone (<c>fret -/+ 5</c> clamped to the floor or
///     the ceiling), so in a chord the lowest note reaches the floor first and
///     the notes collapse onto one fret (3/5 down landed on 1/1). Here the notes
///     of one chord that slide the same way share ONE distance: the largest move
///     up to <see cref="Travel" /> frets that keeps every note inside the floor
///     or ceiling (3/5 down lands on 1/3). A single note keeps its own result.
///     Pick scrapes, dead-note slides (full mute) and pitched slides are left alone.
/// </summary>
internal static class GpChordSlideOut
{
    /// <summary>The unpitched slide-out travel of one note, in frets.</summary>
    public const int Travel = 5;

    /// <summary>Re-targets the ordinary slide-outs of <paramref name="chord" /> to one shared distance.</summary>
    public static void Align(IReadOnlyList<ChordNote> chord, int capo)
    {
        if (chord.Count < 2) return;
        var floor = GpCapoFrets.MinFrettedFret(capo);
        var ceiling = GpCapoFrets.MaxAbsoluteFret;
        Shift(chord, floor, ceiling, down: true);
        Shift(chord, floor, ceiling, down: false);
    }

    private static void Shift(IReadOnlyList<ChordNote> chord, int floor, int ceiling, bool down)
    {
        var group = chord.Where(n => IsOrdinarySlideOut(n, floor, ceiling, down)).ToList();
        if (group.Count < 2) return;
        var room = down ? group.Min(n => n.Fret - floor) : group.Min(n => ceiling - n.Fret);
        var distance = Math.Clamp(room, 0, Travel);
        foreach (var n in group) n.SlideTo = down ? n.Fret - distance : n.Fret + distance;
    }

    private static bool IsOrdinarySlideOut(ChordNote n, int floor, int ceiling, bool down)
    {
        if (!n.UnpitchedSlide || n.Ignore || n.Mute == MuteType.Full || n.SlideTo is not { } to) return false;
        if (n.Fret < floor || n.Fret > ceiling) return false;
        // Where the per-note rule would have put this note for the direction asked.
        var alone = down ? Math.Max(floor, n.Fret - Travel) : Math.Min(ceiling, n.Fret + Travel);
        return to == alone;
    }
}
