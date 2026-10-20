namespace DotCharter.Services.Parsing.Xml.Song;

/// <summary>
///     Export-time projection: a chord that a <c>LinkNext</c> sound lands on is
///     emitted as loose single NOTES instead of a <c>&lt;chord&gt;</c>.
///     <para>
///         <b>Linked chords are LEGAL to author and must stay in the chart.</b>
///         A chord tied or slid into the next chord is ordinary guitar, and the
///         editor lets you write it. What cannot survive is the SPELLING: in
///         RS2014 a <c>&lt;chord&gt;</c> means a STRUM, and a shape you slid
///         into was never re-picked — so the game mis-displays it and DLC
///         Builder rejects it as I03 "note is linked to a chord"
///         (Rocksmith2014.NET <c>InstrumentalChecker.isLinkedToChord</c>, which
///         inspects CHORD NOTES too).
///     </para>
///     <para>
///         <b>This is EOF's answer, in EOF's own words.</b>
///         <c>EOF_SRC/rs.c:2010</c>: <i>"Identify chords that follow a note with
///         linkNext status, which will need to be broken into single notes so
///         that they display correctly in-game"</i> — EOF keeps the linked chord
///         in the project and splits it only while writing the XML
///         (<c>EOF_NOTE_TFLAG_TEMP</c> / <c>EOF_NOTE_TFLAG_SPLIT_CHORD</c>). The
///         evidence in its output is unambiguous: across the 98 hand-corrected
///         exports in <c>TestData/EOF</c> there are <b>493</b> chord notes
///         carrying <c>linkNext</c> and <b>not one</b> lands on a chord. All 493
///         land on loose notes at gap 0, 492 of those landings carry 2-4
///         SIMULTANEOUS notes (so the landing really is a shape, deliberately
///         written as notes), and a hand shape covers the landing in 493 of 493
///         — which is what keeps the shape visible without claiming a strum.
///     </para>
///     <para>
///         The WHOLE landing chord is split, matching EOF: a partly-linked chord
///         is still a chord and would still be rejected, and RS offers no third
///         spelling. We trigger on the PRECISE landing (a link whose sustain ends
///         exactly at this chord, on a string this chord plays) rather than EOF's
///         looser "the previous sound had linkNext anywhere"; ours is a strict
///         subset, so it splits every chord that would otherwise be illegal and
///         no others.
///     </para>
///     <para>
///         <b>A chord marked <see cref="Chord.SplitIntoNotes" /> is split the same
///         way</b> — EOF's "split" status (<c>EOF_PRO_GUITAR_NOTE_FLAG_SPLIT</c>,
///         <c>song.h:64</c>: "a chord that exports to Rocksmith as single notes
///         instead of a chord", <c>rs.c:2087</c>). The flag is the Inspector's
///         Playback ▸ "Split notes", and a Guitar Pro pick scrape on several
/// strings is imported as one: one <c>&lt;note&gt;</c> per string,
///         no <c>&lt;chord&gt;</c>, no repeat box — and, as in EOF, it counts as
/// those notes everywhere else too (<see cref="IsSplit" />).
///     </para>
///     <para>
///         Pure: the domain objects are read, never mutated. The caller writes
///         the returned projection instead of <c>level.Notes</c>/
///         <c>level.Chords</c>. Hand shapes, anchors and templates are untouched
///         — the shape keeps displaying, exactly as in EOF's output. (A template
///         that ONLY split chords play is left out of the emitted list by
///         <c>RsChordTemplateExport</c>, not here.)
///     </para>
/// </summary>
internal static class LinkLandingBreakup
{
    /// <summary>The notes and chords to WRITE for this level.</summary>
    /// <param name="level">The level to project. Not modified.</param>
    internal static (IReadOnlyList<GuitarNote> Notes, IReadOnlyList<Chord> Chords) Project(ChartLevel level)
    {
        if (level.Chords.Count == 0)
            return (level.Notes.ToList(), Array.Empty<Chord>());

        var landings = CollectLinkLandings(level.Notes, level.Chords);
        if (landings.Count == 0 && !AnySplit(level.Chords))
            return (level.Notes.ToList(), level.Chords.ToList());

        var notes = level.Notes.ToList();
        var chords = new List<Chord>(level.Chords.Count);
        var split = false;

        foreach (var chord in level.Chords)
        {
            if (chord.ChordNotes.Count == 0 || !ShipsAsNotes(chord, landings))
            {
                chords.Add(chord);
                continue;
            }

            foreach (var chordNote in chord.ChordNotes)
                notes.Add(ToLooseNote(chord, chordNote));
            split = true;
        }

        if (split)
        {
            notes.Sort((a, b) =>
            {
                var byPosition = a.Position.CompareTo(b.Position);
                return byPosition != 0 ? byPosition : a.StringIndex.CompareTo(b.StringIndex);
            });
        }

        return (notes, chords);
    }

    /// <summary>The chords <see cref="Project" /> splits into loose notes — the
    /// ones the export does NOT ship as a <c>&lt;chord&gt;</c> at all, so no repeat
    /// box either (<c>Export/Rocksmith/RepeatChords</c>). Reference identity.</summary>
    internal static HashSet<Chord> LandedChords(IList<GuitarNote> notes, IList<Chord> chords)
    {
        var landed = new HashSet<Chord>();
        if (chords.Count == 0) return landed;

        var landings = CollectLinkLandings(notes, chords);
        if (landings.Count == 0 && !AnySplit(chords)) return landed;

        foreach (var chord in chords)
        {
            if (chord.ChordNotes.Count > 0 && ShipsAsNotes(chord, landings))
                landed.Add(chord);
        }

        return landed;
    }

    /// <summary>(position, string) for every place an authored link lands —
    /// from single notes AND from chord notes, since a chord links the same
    /// way.</summary>
    private static HashSet<(FractionalPosition Position, int StringIndex)> CollectLinkLandings(
        IList<GuitarNote> notes, IList<Chord> chords)
    {
        var landings = new HashSet<(FractionalPosition, int)>();

        foreach (var note in notes)
        {
            if (note.LinkNext)
                landings.Add((note.Position + note.Sustain, note.StringIndex));
        }

        foreach (var chord in chords)
        {
            foreach (var chordNote in chord.ChordNotes)
            {
                if (chordNote.LinkNext)
                    landings.Add((chord.Position + chordNote.Sustain, chordNote.StringIndex));
            }
        }

        return landings;
    }

    /// <summary>
    ///     A chord the export ALWAYS writes as single notes because the charter
    ///     marked it "Split notes" — whatever else the level holds. EOF breaks
    ///     such a chord up before every decision that counts chords (the chord
    ///     template list, <c>rs.c:219</c>/<c>:2124</c>; the repeat decision,
    ///     <c>rs.c:2082-2124</c> runs before the chord writer), so the
    ///     export-side decisions ask this one question and treat the chord as the
    /// notes the game will see: <c>RsChordTemplateExport</c>,
    ///     <c>RsChordDensity</c> / <c>RepeatChords</c> and, in Core,
    ///     <c>ArrangementPropertiesDeriver</c>. A split chord with no chord notes
    ///     has nothing to split and still ships as a chord.
    /// </summary>
    internal static bool IsSplit(Chord chord) => chord.SplitIntoNotes && chord.ChordNotes.Count > 0;

    private static bool AnySplit(IList<Chord> chords)
    {
        for (var i = 0; i < chords.Count; i++)
        {
            if (IsSplit(chords[i])) return true;
        }

        return false;
    }

    private static bool ShipsAsNotes(Chord chord, HashSet<(FractionalPosition, int)> landings)
        => IsSplit(chord) || IsLandedOn(chord, landings);

    private static bool IsLandedOn(Chord chord, HashSet<(FractionalPosition, int)> landings)
    {
        foreach (var chordNote in chord.ChordNotes)
        {
            if (landings.Contains((chord.Position, chordNote.StringIndex)))
                return true;
        }

        return false;
    }

    /// <summary>One string of a split chord as a standalone note. Every playable
    /// field travels; the chord's own <see cref="Chord.Accent" /> and
    /// <see cref="Chord.Ignore" /> fold in because they describe the strum as a
    /// whole (EOF clones the chord's flags onto each temporary note the same
    /// way).</summary>
    internal static GuitarNote ToLooseNote(Chord chord, ChordNote chordNote)
    {
        var note = new GuitarNote
        {
            Position = chord.Position,
            StringIndex = chordNote.StringIndex,
            Fret = chordNote.Fret,
            Sustain = chordNote.Sustain,
            LinkNext = chordNote.LinkNext,
            SlideTo = chordNote.SlideTo,
            UnpitchedSlide = chordNote.UnpitchedSlide,
            Mute = chordNote.Mute,
            Hopo = chordNote.Hopo,
            Harmonic = chordNote.Harmonic,
            Vibrato = chordNote.Vibrato,
            VibratoStrength = chordNote.VibratoStrength,
            Tremolo = chordNote.Tremolo,
            BassPicking = chordNote.BassPicking,
            Accent = chordNote.Accent || chord.Accent,
            Ignore = chordNote.Ignore || chord.Ignore,
            GraceFret = chordNote.GraceFret
        };

        foreach (var bend in chordNote.BendValues)
            note.BendValues.Add(bend.Clone());

        return note;
    }
}
