namespace DotCharter.Services.Parsing.GuitarPro;

/// <summary>
/// Merges Guitar Pro re-imports into existing arrangements while preserving
/// user-placed FHPs / handshapes / note timing. Two strategies:
/// <list type="bullet">
///   <item><b>Technique-only</b>: copy only technique fields (mute, hopo,
///   harmonic, vibrato, tremolo, slides, accent, bends…) onto matching
///   existing notes. Position, sustain and fret stay untouched.</item>
///   <item><b>Diff-compare</b>: same idea but a previous GP version is also
///   provided. Only fields whose value changed between old and new GP files
///   are written, so an accidental tweak in the chart isn't undone by a
///   re-import that didn't actually touch that field.</item>
/// </list>
/// Sounds are matched between source and target by (StringIndex, Position) —
/// single notes against single notes, and a CHORD's per-string
/// <see cref="ChordNote"/>s against the target chord's, keyed on the chord's
/// own position. Sounds in the source that have no match in the target are
/// ignored (and counted) — the goal is to update existing charting, not
/// rewrite it.
/// </summary>
public static class GpTechniqueMerger
{
    public sealed class MergeStats
    {
        public int NotesScanned { get; set; }
        public int NotesUpdated { get; set; }
        public int NotesUnmatched { get; set; }
        public int FieldsChanged { get; set; }

        /// <summary>Chords (strums) seen in the source.</summary>
        public int ChordsScanned { get; set; }

        /// <summary>Chords whose target counterpart took at least one field.</summary>
        public int ChordsUpdated { get; set; }

        /// <summary>Chords with no chord at the same position in the target.</summary>
        public int ChordsUnmatched { get; set; }
    }

    public static MergeStats ApplyTechniquesOnly(Arrangement target, Arrangement source)
    {
        var stats = new MergeStats();
        var index = BuildNoteIndex(target);
        var chordIndex = BuildChordIndex(target);

        foreach (var srcLevel in source.Levels)
        {
            foreach (var srcNote in srcLevel.Notes)
            {
                stats.NotesScanned++;
                var key = (srcNote.StringIndex, srcNote.Position);
                if (!index.TryGetValue(key, out var dstNote))
                {
                    stats.NotesUnmatched++;
                    continue;
                }

                var changed = CopyTechniqueFields(srcNote, dstNote);
                if (changed > 0)
                {
                    stats.NotesUpdated++;
                    stats.FieldsChanged += changed;
                }
            }

            foreach (var srcChord in srcLevel.Chords)
            {
                stats.ChordsScanned++;
                if (!chordIndex.TryGetValue(srcChord.Position, out var dstChord))
                {
                    stats.ChordsUnmatched++;
                    continue;
                }

                var changed = 0;
                if (dstChord.Accent != srcChord.Accent)
                {
                    dstChord.Accent = srcChord.Accent;
                    changed++;
                }

                foreach (var srcChordNote in srcChord.ChordNotes)
                {
                    var dstChordNote = FindChordNote(dstChord, srcChordNote.StringIndex);
                    if (dstChordNote == null) continue;
                    changed += CopyTechniqueFields(srcChordNote, dstChordNote);
                }

                if (changed > 0)
                {
                    stats.ChordsUpdated++;
                    stats.FieldsChanged += changed;
                }
            }
        }

        return stats;
    }

    public static MergeStats ApplyDiff(Arrangement target, Arrangement oldSource, Arrangement newSource)
    {
        var stats = new MergeStats();
        var dstIndex = BuildNoteIndex(target);
        var oldIndex = BuildNoteIndex(oldSource);
        var dstChords = BuildChordIndex(target);
        var oldChords = BuildChordIndex(oldSource);

        foreach (var newLevel in newSource.Levels)
        {
            foreach (var newNote in newLevel.Notes)
            {
                stats.NotesScanned++;
                var key = (newNote.StringIndex, newNote.Position);
                if (!dstIndex.TryGetValue(key, out var dstNote))
                {
                    stats.NotesUnmatched++;
                    continue;
                }

                oldIndex.TryGetValue(key, out var oldNote);
                var changed = CopyChangedTechniqueFields(newNote, oldNote, dstNote);
                if (changed > 0)
                {
                    stats.NotesUpdated++;
                    stats.FieldsChanged += changed;
                }
            }

            foreach (var newChord in newLevel.Chords)
            {
                stats.ChordsScanned++;
                if (!dstChords.TryGetValue(newChord.Position, out var dstChord))
                {
                    stats.ChordsUnmatched++;
                    continue;
                }

                oldChords.TryGetValue(newChord.Position, out var oldChord);

                var changed = 0;
                if ((oldChord == null || newChord.Accent != oldChord.Accent) &&
                    dstChord.Accent != newChord.Accent)
                {
                    dstChord.Accent = newChord.Accent;
                    changed++;
                }

                foreach (var newChordNote in newChord.ChordNotes)
                {
                    var dstChordNote = FindChordNote(dstChord, newChordNote.StringIndex);
                    if (dstChordNote == null) continue;
                    var oldChordNote = oldChord == null
                        ? null
                        : FindChordNote(oldChord, newChordNote.StringIndex);
                    changed += CopyChangedTechniqueFields(newChordNote, oldChordNote, dstChordNote);
                }

                if (changed > 0)
                {
                    stats.ChordsUpdated++;
                    stats.FieldsChanged += changed;
                }
            }
        }

        return stats;
    }

    private static Dictionary<(int String, FractionalPosition Pos), GuitarNote> BuildNoteIndex(Arrangement arr)
    {
        var dict = new Dictionary<(int, FractionalPosition), GuitarNote>();
        foreach (var lvl in arr.Levels)
        {
            foreach (var n in lvl.Notes)
                dict[(n.StringIndex, n.Position)] = n;
        }

        return dict;
    }

    private static Dictionary<FractionalPosition, Chord> BuildChordIndex(Arrangement arr)
    {
        var dict = new Dictionary<FractionalPosition, Chord>();
        foreach (var lvl in arr.Levels)
        {
            foreach (var c in lvl.Chords)
                dict[c.Position] = c;
        }

        return dict;
    }

    private static ChordNote? FindChordNote(Chord chord, int stringIndex)
    {
        for (var i = 0; i < chord.ChordNotes.Count; i++)
        {
            if (chord.ChordNotes[i].StringIndex == stringIndex)
                return chord.ChordNotes[i];
        }

        return null;
    }

    private static int CopyTechniqueFields(IGuitarSound src, IGuitarSound dst)
    {
        var changes = 0;
        if (dst.Mute != src.Mute) { dst.Mute = src.Mute; changes++; }
        if (dst.Hopo != src.Hopo) { dst.Hopo = src.Hopo; changes++; }
        if (dst.Harmonic != src.Harmonic) { dst.Harmonic = src.Harmonic; changes++; }
        if (dst.Vibrato != src.Vibrato) { dst.Vibrato = src.Vibrato; changes++; }
        if (dst.VibratoStrength != src.VibratoStrength) { dst.VibratoStrength = src.VibratoStrength; changes++; }
        if (dst.Tremolo != src.Tremolo) { dst.Tremolo = src.Tremolo; changes++; }
        if (dst.LinkNext != src.LinkNext) { dst.LinkNext = src.LinkNext; changes++; }
        if (dst.SlideTo != src.SlideTo) { dst.SlideTo = src.SlideTo; changes++; }
        if (dst.UnpitchedSlide != src.UnpitchedSlide) { dst.UnpitchedSlide = src.UnpitchedSlide; changes++; }
        if (dst.Accent != src.Accent) { dst.Accent = src.Accent; changes++; }
        if (dst.BassPicking != src.BassPicking) { dst.BassPicking = src.BassPicking; changes++; }
        if (!BendsEqual(dst.BendValues, src.BendValues))
        {
            dst.BendValues.Clear();
            foreach (var b in src.BendValues) dst.BendValues.Add(b.Clone());
            changes++;
        }
        return changes;
    }

    private static int CopyChangedTechniqueFields(IGuitarSound newNote, IGuitarSound? oldNote, IGuitarSound dst)
    {
        if (oldNote == null) return CopyTechniqueFields(newNote, dst);
        var changes = 0;
        if (newNote.Mute != oldNote.Mute && dst.Mute != newNote.Mute) { dst.Mute = newNote.Mute; changes++; }
        if (newNote.Hopo != oldNote.Hopo && dst.Hopo != newNote.Hopo) { dst.Hopo = newNote.Hopo; changes++; }
        if (newNote.Harmonic != oldNote.Harmonic && dst.Harmonic != newNote.Harmonic) { dst.Harmonic = newNote.Harmonic; changes++; }
        if (newNote.Vibrato != oldNote.Vibrato && dst.Vibrato != newNote.Vibrato) { dst.Vibrato = newNote.Vibrato; changes++; }
        if (newNote.VibratoStrength != oldNote.VibratoStrength && dst.VibratoStrength != newNote.VibratoStrength) { dst.VibratoStrength = newNote.VibratoStrength; changes++; }
        if (newNote.Tremolo != oldNote.Tremolo && dst.Tremolo != newNote.Tremolo) { dst.Tremolo = newNote.Tremolo; changes++; }
        if (newNote.LinkNext != oldNote.LinkNext && dst.LinkNext != newNote.LinkNext) { dst.LinkNext = newNote.LinkNext; changes++; }
        if (newNote.SlideTo != oldNote.SlideTo && dst.SlideTo != newNote.SlideTo) { dst.SlideTo = newNote.SlideTo; changes++; }
        if (newNote.UnpitchedSlide != oldNote.UnpitchedSlide && dst.UnpitchedSlide != newNote.UnpitchedSlide) { dst.UnpitchedSlide = newNote.UnpitchedSlide; changes++; }
        if (newNote.Accent != oldNote.Accent && dst.Accent != newNote.Accent) { dst.Accent = newNote.Accent; changes++; }
        if (newNote.BassPicking != oldNote.BassPicking && dst.BassPicking != newNote.BassPicking) { dst.BassPicking = newNote.BassPicking; changes++; }
        if (!BendsEqual(newNote.BendValues, oldNote.BendValues) && !BendsEqual(dst.BendValues, newNote.BendValues))
        {
            dst.BendValues.Clear();
            foreach (var b in newNote.BendValues) dst.BendValues.Add(b.Clone());
            changes++;
        }
        return changes;
    }

    private static bool BendsEqual(IList<BendValue> a, IList<BendValue> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].Position != b[i].Position) return false;
            if (Math.Abs(a[i].Step - b[i].Step) > 0.001f) return false;
        }
        return true;
    }
}
