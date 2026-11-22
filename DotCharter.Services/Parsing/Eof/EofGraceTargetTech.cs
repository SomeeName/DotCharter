using System.Numerics;

namespace DotCharter.Services.Parsing.Eof;

/// <summary>Ask the real note exporter which records belong to a reconstructed grace target.</summary>
internal static class EofGraceTargetTech
{
    public static IReadOnlyList<EofRawProGuitarNote> Owners(IReadOnlyList<EofRawProGuitarNote> notes, Dictionary<int, int> pairs)
    {
        var owners = notes.Select(Copy).ToList();
        foreach (var (ornament, target) in pairs)
        {
            owners[target].PositionMs = notes[ornament].PositionMs;
            owners[target].LengthMs = EofGraceTiming.FoldedLength(owners[target].LengthMs, notes[ornament].LengthMs);
        }
        return owners;
    }

    public static IReadOnlyList<EofRawProGuitarNote> Build(EofRawProGuitarNote target,
        IReadOnlyList<EofRawProGuitarNote> techNotes, int capo, IList<BeatMarker> beats)
    {
        var raw = Copy(target);
        var arrangement = new Arrangement { Capo = capo, StringCount = 8 };
        var level = new ChartLevel { DifficultyIndex = raw.Difficulty };
        arrangement.Levels.Add(level);
        var live = (byte)(raw.NoteBitmask & ~raw.GhostBitmask);
        var ownTech = techNotes.Where(t => OwnData(raw, live, t)).ToList();
        if (BitOperations.IsPow2((uint)live))
        {
            level.Notes.Add(EofProGuitarTrackParser.BuildSingleNote(raw, live, capo, beats, ownTech));
        }
        else
        {
            var chord = EofProGuitarTrackParser.BuildChord(raw, live, null, capo, beats, arrangement, [], ownTech);
            // Source chord-note order chooses the global slide. Preserve that
            // encoded choice when the reader has rebuilt notes in string order.
            var lowest = EofProGuitarTrackParser.GetLowestAdjustedChordFret(raw, live, capo);
            var baseline = chord.ChordNotes.FirstOrDefault(n => n.SlideTo.HasValue &&
                EofProGuitarTrackParser.ResolveEffectiveSlide(raw, null, n.Fret, lowest, capo).SlideTo == n.SlideTo);
            if (baseline != null)
            {
                chord.ChordNotes.Remove(baseline);
                chord.ChordNotes.Insert(0, baseline);
            }
            level.Chords.Add(chord);
        }
        // The exporter calls this SAME function before displacing a grace's
        // target. No flags, fret, length or override-emission rules live here.
        return EofNoteBuilder.Build(arrangement, beats).SelectMany(n => n.BendTechNotes).ToList();
    }

    // Select the candidate's data before reconstructing it. This is not the
    // generation test: only the actual exporter's resulting record can pass.
    private static bool OwnData(EofRawProGuitarNote target, byte live, EofRawProGuitarNote tech)
    {
        if (tech.Difficulty != target.Difficulty || (tech.NoteBitmask & live) != tech.NoteBitmask) return false;
        for (var s = 0; s < 8; s++)
        {
            if ((tech.NoteBitmask & (1 << s)) != 0 && tech.Frets[s] != target.Frets[s]) return false;
        }
        return (tech.Flags & EofConstants.PgFlagBend) != 0
            ? (target.Flags & EofConstants.PgFlagBend) != 0 && tech.NoteBitmask == live
            : tech.PositionMs == target.PositionMs;
    }

    private static EofRawProGuitarNote Copy(EofRawProGuitarNote note)
    {
        var wire = new EofPackWriter();
        EofProGuitarNoteWriter.Write(wire, note);
        return EofProGuitarNoteReader.Read(new EofPackReader(wire.ToArray()));
    }

    public static bool SameRecord(EofRawProGuitarNote expected, EofRawProGuitarNote actual)
    {
        var first = new EofPackWriter();
        var second = new EofPackWriter();
        EofProGuitarNoteWriter.Write(first, expected);
        EofProGuitarNoteWriter.Write(second, actual);
        return first.ToArray().AsSpan().SequenceEqual(second.ToArray());
    }
}
