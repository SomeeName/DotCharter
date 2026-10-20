using System.Xml.Linq;

namespace DotCharter.Services.Parsing.Xml.Song;

/// <summary>
///     RS-XML import: the single notes EOF wrote for a chord marked "Split notes"
///     come back as that ONE chord, marked <see cref="Chord.SplitIntoNotes" />.
/// RS XML has no spelling of its own for a split chord — EOF
///     writes one <c>&lt;note&gt;</c> per string (<c>EOF_SRC/rs.c:2082-2124</c>) —
///     so this gives the editor back the unit the charter selected. A Guitar Pro
/// pick scrape on two or more strings (tap + tremolo + fret-hand mute +
///     unpitched slide + ignore) ships the same way and comes back the same way.
///     <para>
///         <b>What marks a split chord.</b> EOF's own RS import merges EVERY pair of
///         notes at one timestamp (<c>rs_import.c:1650-1655</c>), and so does
///         RsCharter's (<c>RSXMLLevelTransformer.addNotes</c>). That is too broad:
///         EOF writes simultaneous single notes for four reasons, and only one of
///         them is a split chord —
///     </para>
///     <list type="bullet">
///         <item>a chord a <c>linkNext</c> lands on is broken into notes
///         (<c>rs.c:2010</c>), and keeps its hand shape;</item>
///         <item>a chord inside an ARPEGGIO phrase is broken into notes
///         (<c>rs.c:2300-2345</c>), inside the arpeggio's hand shape;</item>
///         <item>a "chordify" chord writes its notes 1 ms after the chord tag
///         (<c>rs.c:2135-2210</c>), inside that chord's hand shape;</item>
///         <item>a SPLIT chord is broken into notes and marked ignored for the chord
///         count (<c>rs.c:2124</c>), so it gets no chord template and no hand
///         shape.</item>
///     </list>
///     <para>
///         So a group is a split chord when it is two or more notes at one time on
///         different strings, with no chord of the level at that time, <b>inside no
///         hand shape</b>, and <b>no note of it is a link target</b> (the sound
///         before it on that string has <c>linkNext</c>, EOF's own test at
///         <c>rs.c:2010</c>). Measured on the owner's 35 EOF projects (each
///         <c>notes.eof</c> against the <c>*_RS2.xml</c> EOF wrote next to it): of
///         the 240 simultaneous-note groups, 237 are link landings (175 of them
///         inside a hand shape), and the 3 that are neither are exactly the 3 split
///         chords the <c>.eof</c> files hold at those times. A DotCharter split
///         chord exports the same way (<c>LinkLandingBreakup</c>), so it round-trips.
///     </para>
///     <para>
///         The chord plays a template the file declares with exactly those frets
///         (not an arpeggio) when there is one, else a nameless, fingerless mint —
///         the same mint the pick scrape and RsCharter's <c>splitChord</c> make. A
///         template only split chords play is not exported (<c>RsChordTemplateExport</c>),
///         and a split chord counts as its notes in the repeat decision and the
///         arrangement properties, so the merge changes nothing the file says.
///         Two gates keep it that way through the next SAVE, too:
///     </para>
///     <list type="bullet">
///         <item>the strings are all fret-hand muted or none are — the save-time
///         pass <c>ChartSpacingEnforcer.RemoveMutedNotesFromPartiallyMutedChords</c>
///         would otherwise strip the muted ones from the chord;</item>
///         <item>a note whose fret makes the save-time derivation ignore it
///         (<see cref="ChartSpacingEnforcer.RequiresIgnore" />) sits in a group whose
///         every note is ignored already — the chord carries ONE derived ignore, and
///         its notes ship with it.</item>
///     </list>
///     <para>
///         <b>And the write-back stays the safety net.</b> <see cref="RsXmlSongWriter" />
///         must write the arrangement back as the same document. A level whose
///         <c>&lt;level&gt;</c> comes out different keeps its loose notes; if anything
///         else differs (outside the levels — say a declared template that nothing
///         else plays would drop out of the list), nothing is merged.
///     </para>
///     <para>
///         Not merged, and so still loose after a round trip: a split chord inside a
///         hand shape someone drew over it, and a split chord a link lands on — both
///         look exactly like EOF's broken-up ordinary chords.
///     </para>
/// </summary>
internal static class RsXmlSplitChordMerge
{
    /// <summary>Merges what can be merged in <c>project.Arrangements[0]</c>; returns
    /// the number of split chords kept.</summary>
    internal static int Apply(ChartProject project)
    {
        if (project.Arrangements.Count == 0) return 0;
        var arrangement = project.Arrangements[0];

        var candidates = new Candidates(arrangement);
        var plan = new Dictionary<ChartLevel, List<(List<GuitarNote> Notes, ChordTemplate Template)>>();
        foreach (var level in arrangement.Levels)
        {
            var groups = candidates.For(level).ToList();
            if (groups.Count > 0) plan[level] = groups;
        }

        if (plan.Count == 0) return 0;

        var before = Write(project);
        var undo = plan.Keys.ToDictionary(l => l, l => (Notes: l.Notes.ToList(), Chords: l.Chords.ToList()));
        foreach (var template in candidates.Minted) arrangement.ChordTemplates.Add(template);
        foreach (var (level, groups) in plan)
            Merge(level, groups);

        while (plan.Count > 0)
        {
            var after = Write(project);
            var differing = DifferingDifficulties(before, after);
            if (differing.Count == 0 && Outside(after) == Outside(before)) break;

            // A level that merged nothing can only differ through a neighbour on the
            // level ladder, and anything outside <levels> through all of them: give
            // the whole arrangement its notes back. Otherwise just the levels that moved.
            var culprits = plan.Keys.Where(l => differing.Contains(l.DifficultyIndex)).ToList();
            if (culprits.Count == 0 || culprits.Count != differing.Count || Outside(after) != Outside(before))
                culprits = plan.Keys.ToList();
            foreach (var level in culprits) Revert(level, undo[level], plan);
            DropUnplayedMints(arrangement, candidates.Minted);
        }

        return plan.Values.Sum(g => g.Count);
    }

    private static void Merge(ChartLevel level, List<(List<GuitarNote> Notes, ChordTemplate Template)> groups)
    {
        foreach (var (notes, template) in groups)
        {
            foreach (var note in notes) level.Notes.Remove(note);
            level.Chords.Add(ToSplitChord(notes, template));
        }

        var sorted = level.Chords.OrderBy(c => c.Position).ToList();
        for (var i = 0; i < sorted.Count; i++) level.Chords[i] = sorted[i];
    }

    private static void Revert(ChartLevel level, (List<GuitarNote> Notes, List<Chord> Chords) original,
        Dictionary<ChartLevel, List<(List<GuitarNote> Notes, ChordTemplate Template)>> plan)
    {
        level.Notes.Clear();
        foreach (var note in original.Notes) level.Notes.Add(note);
        level.Chords.Clear();
        foreach (var chord in original.Chords) level.Chords.Add(chord);
        plan.Remove(level);
    }

    /// <summary>A mint no chord plays any more (its level gave its notes back) leaves
    /// the arrangement: an unplayed template is exported, a split-only one is not.</summary>
    private static void DropUnplayedMints(Arrangement arrangement, List<ChordTemplate> minted)
    {
        var played = arrangement.Levels.SelectMany(l => l.Chords).Select(c => c.TemplateId).ToHashSet();
        foreach (var template in minted)
        {
            if (!played.Contains(template.Id))
                arrangement.ChordTemplates.Remove(template);
        }
    }

    /// <summary>The write-back, minus the one line that is a clock reading.</summary>
    private static XElement Write(ChartProject project)
    {
        var root = new RsXmlSongWriter().Write(project).Root!;
        root.Element(E.LastConversionDateTime)?.Remove();
        return root;
    }

    private static string Outside(XElement song)
    {
        var copy = new XElement(song);
        copy.Element(E.Levels)?.Remove();
        return copy.ToString(SaveOptions.DisableFormatting);
    }

    private static HashSet<int> DifferingDifficulties(XElement before, XElement after)
    {
        var was = Levels(before);
        var now = Levels(after);
        var differing = new HashSet<int>();
        foreach (var difficulty in was.Keys.Union(now.Keys))
        {
            if (was.GetValueOrDefault(difficulty) != now.GetValueOrDefault(difficulty))
                differing.Add(difficulty);
        }

        return differing;
    }

    private static Dictionary<int, string> Levels(XElement song)
    {
        var byDifficulty = new Dictionary<int, string>();
        foreach (var level in song.Element(E.Levels)?.Elements(E.Level) ?? [])
        {
            var difficulty = (int?)level.Attribute(E.Difficulty) ?? -1;
            var xml = level.ToString(SaveOptions.DisableFormatting);
            // A repeated difficulty keeps every copy in the comparison.
            byDifficulty[difficulty] = byDifficulty.TryGetValue(difficulty, out var earlier) ? earlier + xml : xml;
        }

        return byDifficulty;
    }

    /// <summary>One chord note per note, every technique on its own string. The
    /// chord's own Accent / Ignore are set when EVERY string has it: the export
    /// folds them back into each note (<c>LinkLandingBreakup.ToLooseNote</c>), so
    /// that changes nothing it writes, and a pick scrape comes back as the ignored
    /// chord it was.</summary>
    private static Chord ToSplitChord(List<GuitarNote> notes, ChordTemplate template)
    {
        var chord = new Chord
        {
            Position = notes[0].Position,
            SplitIntoNotes = true,
            TemplateId = template.Id,
            Accent = notes.All(n => n.Accent),
            Ignore = notes.All(n => n.Ignore)
        };
        foreach (var note in notes)
        {
            var chordNote = new ChordNote
            {
                StringIndex = note.StringIndex,
                Fret = note.Fret,
                Sustain = note.Sustain,
                LinkNext = note.LinkNext,
                SlideTo = note.SlideTo,
                UnpitchedSlide = note.UnpitchedSlide,
                Mute = note.Mute,
                Hopo = note.Hopo,
                Harmonic = note.Harmonic,
                Vibrato = note.Vibrato,
                VibratoStrength = note.VibratoStrength,
                Tremolo = note.Tremolo,
                BassPicking = note.BassPicking,
                Accent = note.Accent,
                Ignore = note.Ignore,
                GraceFret = note.GraceFret
            };
            foreach (var bend in note.BendValues) chordNote.BendValues.Add(bend.Clone());
            chord.ChordNotes.Add(chordNote);
        }

        return chord;
    }

    /// <summary>The per-group rule, with the templates it plays.</summary>
    private sealed class Candidates
    {
        private readonly Dictionary<string, ChordTemplate> _byFrets = [];
        private int _nextId;

        public Candidates(Arrangement arrangement)
        {
            foreach (var template in arrangement.ChordTemplates)
            {
                _nextId = Math.Max(_nextId, template.Id + 1);
                if (template.IsArpeggio || template.Frets.Count < 2) continue;
                _byFrets.TryAdd(FretsKey(template.Frets.Select(f => (f.Key, f.Value))), template);
            }
        }

        /// <summary>Templates made for groups no declared template matched — added to
        /// the arrangement by <see cref="Apply" />, one per shape.</summary>
        public List<ChordTemplate> Minted { get; } = [];

        public IEnumerable<(List<GuitarNote> Notes, ChordTemplate Template)> For(ChartLevel level)
        {
            if (level.Notes.Count < 2) yield break;

            var chordTimes = new HashSet<FractionalPosition>();
            foreach (var chord in level.Chords) chordTimes.Add(chord.Position);

            Dictionary<int, List<(FractionalPosition Position, IGuitarSound Sound)>>? byString = null;

            foreach (var group in level.Notes.GroupBy(n => n.Position))
            {
                var notes = group.OrderBy(n => n.StringIndex).ToList();
                if (notes.Count < 2 || chordTimes.Contains(group.Key)) continue;
                if (notes.Select(n => n.StringIndex).Distinct().Count() != notes.Count) continue;

                // EOF gives a split chord no hand shape; every other chord it breaks
                // into notes keeps one (see the class summary).
                if (InsideAHandShape(level, group.Key)) continue;

                var fullyMuted = notes.Count(n => n.Mute == MuteType.Full);
                if (fullyMuted != 0 && fullyMuted != notes.Count) continue;

                if (notes.Any(RequiresIgnore) && !notes.All(n => n.Ignore || RequiresIgnore(n))) continue;

                byString ??= SoundsByString(level);
                if (notes.Any(n => IsLinkTarget(byString, n))) continue;

                yield return (notes, TemplateFor(notes));
            }
        }

        private ChordTemplate TemplateFor(List<GuitarNote> notes)
        {
            var key = FretsKey(notes.Select(n => (n.StringIndex, n.Fret)));
            if (_byFrets.TryGetValue(key, out var template)) return template;

            template = new ChordTemplate { Id = _nextId++, Name = string.Empty };
            foreach (var note in notes) template.Frets[note.StringIndex] = note.Fret;
            _byFrets[key] = template;
            Minted.Add(template);
            return template;
        }

        private static bool InsideAHandShape(ChartLevel level, FractionalPosition position)
        {
            foreach (var shape in level.HandShapes)
            {
                if (position >= shape.Position && position < shape.EndPosition)
                    return true;
            }

            return false;
        }

        private static string FretsKey(IEnumerable<(int StringIndex, int Fret)> frets) =>
            string.Join("|", frets.OrderBy(f => f.StringIndex).Select(f => $"{f.StringIndex}:{f.Fret}"));

        private static bool RequiresIgnore(GuitarNote note) =>
            ChartSpacingEnforcer.RequiresIgnore(note.Fret, note.Harmonic, note.Sustain);

        private static Dictionary<int, List<(FractionalPosition Position, IGuitarSound Sound)>> SoundsByString(
            ChartLevel level)
        {
            var byString = new Dictionary<int, List<(FractionalPosition, IGuitarSound)>>();
            foreach (var note in level.Notes)
                Add(note.StringIndex, note.Position, note);
            foreach (var chord in level.Chords)
            {
                foreach (var chordNote in chord.ChordNotes)
                    Add(chordNote.StringIndex, chord.Position, chordNote);
            }

            return byString;

            void Add(int stringIndex, FractionalPosition position, IGuitarSound sound)
            {
                if (!byString.TryGetValue(stringIndex, out var list))
                    byString[stringIndex] = list = [];
                list.Add((position, sound));
            }
        }

        /// <summary>
        ///     EOF's own test (<c>rs.c:2010</c>): the sound right before this note on
        ///     its string has <c>linkNext</c>. Its sustain need not end exactly here —
        ///     a millisecond of rounding must not turn a landing into a split chord.
        /// </summary>
        private static bool IsLinkTarget(
            Dictionary<int, List<(FractionalPosition Position, IGuitarSound Sound)>> byString, GuitarNote note)
        {
            if (!byString.TryGetValue(note.StringIndex, out var sounds)) return false;

            FractionalPosition? latest = null;
            foreach (var (position, _) in sounds)
            {
                if (position < note.Position && (latest == null || position > latest.Value))
                    latest = position;
            }

            if (latest == null) return false;

            foreach (var (position, sound) in sounds)
            {
                if (position == latest.Value && sound.LinkNext)
                    return true;
            }

            return false;
        }
    }
}
