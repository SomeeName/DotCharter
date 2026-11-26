namespace DotCharter.Services.Parsing.GuitarPro;

/// <summary>
///     Marks what a Guitar Pro import guessed as <see cref="Arrangement.AudioCheckSpots" /> (D110): a dead-note
///     slide-out's landing (<see cref="GpDeadNoteSlide" />; the tab gives the direction, the landing fret is chosen
///     from the next fretted sound or a fixed travel) and a grace ornament's placement when the tab does not state
///     before or on the beat (GP3/GP4 store no flavour; GP5 and gpif do). Swing is stated by the tab (triplet feel),
///     so it is not marked. One spot per position of the playable levels; run after the beat grid is final, so the
///     positions are the chart's.
/// </summary>
internal static class GpAudioCheck
{
    /// <summary>Records a grace target in <paramref name="sink" /> when the tab left the placement open.</summary>
    public static void NoteGraceTiming(ISet<IGuitarSound>? sink, bool placementStated, IGuitarSound target)
    {
        if (!placementStated) sink?.Add(target);
    }

    public static void Mark(GpImportResult result)
    {
        foreach (var arrangement in result.Arrangements)
        {
            MarkSounds(arrangement, result.DeadSlideSounds, AudioCheckReason.DeadNoteSlideLanding);
            MarkSounds(arrangement, result.GraceTimingGuesses, AudioCheckReason.GraceTiming);
        }
    }

    private static void MarkSounds(Arrangement arrangement, HashSet<IGuitarSound> sounds, AudioCheckReason reason)
    {
        if (sounds.Count == 0) return;
        var positions = arrangement.Levels
            .SelectMany(level => level.Notes.Where(sounds.Contains).Select(n => n.Position)
                .Concat(level.Chords.Where(c => c.ChordNotes.Any(sounds.Contains)).Select(c => c.Position)))
            .Distinct().Order();
        foreach (var position in positions)
            arrangement.AudioCheckSpots.Add(new AudioCheckSpot(position, reason));
    }
}
