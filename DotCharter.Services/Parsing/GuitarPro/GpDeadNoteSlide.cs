namespace DotCharter.Services.Parsing.GuitarPro;

/// <summary>Resolves dead-note slide-outs after the track's following fretted sounds are available.</summary>
internal static class GpDeadNoteSlide
{
    private const int Down = -4;
    private const int Up = -5;
    private const int DefaultTravel = 7;
    private static readonly FractionalPosition Gap = new(0, new Fraction(1, 32));

    /// <summary>Keeps the authored direction in an import-only slide sentinel.</summary>
    public static bool Mark(IGuitarSound sound, int flags)
    {
        if ((flags & 12) == 0) return false;
        sound.SlideTo = (flags & 8) != 0 ? Up : Down;
        sound.UnpitchedSlide = true;
        return true;
    }

    public static bool IsPending(IGuitarSound sound) => sound.SlideTo is Down or Up;

    /// <summary>Maps the binary reader's discrete GP4 values and GP5 flags to the shared direction markers.</summary>
    public static bool MarkLegacy(IGuitarSound sound, int slideFlag, bool isVersion5)
    {
        var flags = isVersion5 ? slideFlag : slideFlag switch { 3 => 4, 4 => 8, _ => 0 };
        return sound.Mute == MuteType.Full && !GpPickScrape.IsPickScrape(flags) && Mark(sound, flags);
    }

    /// <summary>Places slide starts and landings before templates and hand positions read the frets.</summary>
    public static void Resolve(ChartLevel level, int capo, int lastFret, ISet<IGuitarSound>? placed = null)
    {
        var sounds = level.Notes.Select(n => (n.Position, Sound: (IGuitarSound)n))
            .Concat(level.Chords.SelectMany(c => c.ChordNotes.Select(n => (c.Position, Sound: (IGuitarSound)n))))
            .OrderBy(s => s.Position).ThenBy(s => s.Sound.StringIndex).ToArray();
        IGuitarSound? next = null;
        for (var end = sounds.Length; end > 0;)
        {
            var start = end - 1;
            while (start > 0 && sounds[start - 1].Position == sounds[end - 1].Position) start--;
            for (var i = start; i < end; i++)
            {
                if (Place(sounds[i].Sound, next, capo, lastFret)) placed?.Add(sounds[i].Sound);
            }
            for (var i = start; i < end; i++)
            {
                var sound = sounds[i].Sound;
                if (sound.Fret <= capo || sound.Mute == MuteType.Full || GpPickScrape.IsScrape(sound)) continue;
                next = sound;
                break;
            }
            end = start;
        }
    }

    /// <summary>Trims only placed dead slide-outs near another onset, using a fixed beat gap.</summary>
    public static void TrimSustains(GpImportResult result)
    {
        if (result.DeadSlideSounds.Count == 0) return;
        foreach (var arrangement in result.Arrangements)
        {
            foreach (var level in arrangement.Levels) TrimVoice(level.Notes, level.Chords, result.DeadSlideSounds);
            foreach (var voice in arrangement.NotationVoices) TrimVoice(voice.Notes, voice.Chords, result.DeadSlideSounds);
        }
    }

    private static void TrimVoice(IEnumerable<GuitarNote> notes, IEnumerable<Chord> chords, IReadOnlySet<IGuitarSound> placed)
    {
        var sounds = notes.Select(n => (n.Position, Sound: (IGuitarSound)n))
            .Concat(chords.SelectMany(c => c.ChordNotes.Select(n => (c.Position, Sound: (IGuitarSound)n))))
            .OrderBy(s => s.Position).ToArray();
        foreach (var (position, sound) in sounds)
        {
            if (!placed.Contains(sound)) continue;
            var end = position + sound.Sustain;
            foreach (var other in sounds)
            {
                if (other.Position > end + Gap) break;
                if (other.Position <= position || other.Position < end - Gap) continue;
                var length = other.Position - Gap - position;
                if (length > FractionalPosition.Zero && length < sound.Sustain) sound.Sustain = length;
                break;
            }
        }
    }

    private static bool Place(IGuitarSound sound, IGuitarSound? next, int capo, int lastFret)
    {
        if (sound.SlideTo is not (Down or Up)) return false;
        var direction = sound.SlideTo == Down ? -1 : 1;
        var floor = GpCapoFrets.Sanitize(capo) + 1;
        var ceiling = Math.Min(lastFret, GpCapoFrets.MaxAbsoluteFret);
        var start = sound.Fret;
        if (start == 0 && ceiling > floor)
        {
            start = Math.Clamp(capo + (direction < 0 ? 12 : 5), direction < 0 ? floor + 1 : floor,
                direction < 0 ? ceiling : ceiling - 1);
        }
        if (start < floor || start > ceiling || start + direction < floor || start + direction > ceiling)
        {
            KeepWithoutRoom(sound, direction, capo);
            return false;
        }

        var target = next != null && (next.Fret - start) * direction >= 2
            ? next.Fret
            : start + direction * DefaultTravel;
        sound.Fret = start;
        sound.SlideTo = Math.Clamp(target, floor, ceiling);
        sound.UnpitchedSlide = true;
        sound.Mute = MuteType.Full;
        sound.Hopo = HopoType.None;
        sound.Tremolo = false;
        sound.LinkNext = false;
        switch (sound)
        {
            case GuitarNote note: note.Ignore = false; break;
            case ChordNote note: note.Ignore = false; break;
        }
        return true;
    }

    private static void KeepWithoutRoom(IGuitarSound sound, int direction, int capo)
    {
        sound.Mute = MuteType.Full;
        sound.UnpitchedSlide = sound.Fret > 0;
        sound.SlideTo = sound.Fret > 0
            ? direction < 0
                ? Math.Max(GpCapoFrets.MinFrettedFret(capo), sound.Fret - 5)
                : Math.Min(GpCapoFrets.MaxAbsoluteFret, sound.Fret + 5)
            : null;
    }
}
