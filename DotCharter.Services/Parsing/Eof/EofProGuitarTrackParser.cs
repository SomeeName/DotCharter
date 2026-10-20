using System.Collections.ObjectModel;
using Arrangement = DotCharter.Core.Domain.Project.Arrangement;

namespace DotCharter.Services.Parsing.Eof;

/// <summary>
///     Parses a single Pro Guitar EOF track (format 4) into a DotCharter
///     <see cref="Arrangement"/>. Handles notes, sections (anchors, arpeggios,
///     trills, tremolos, handshapes, tone changes) and the relevant track-level
///     custom data blocks (fingers, arrangement type, capo).
/// </summary>
internal static class EofProGuitarTrackParser
{
    /// <summary>EOF stores finger 5 = thumb; DotCharter uses 0 = T. Other values 1..4 line up.</summary>
    private static int RemapFinger(int eofFinger) => eofFinger == 5 ? 0 : eofFinger;

    /// <summary>
    ///     True when <paramref name="current" /> does not already name exactly
    ///     the tuning <paramref name="steps" /> describes — a different string
    ///     COUNT, letters whose base is unreadable, or a different pitch on any
    ///     string. False means the spelling already agrees and must be left
    ///     alone: an <c>.eof</c> carries no letters, so re-spelling one that is
    ///     already right would only trade the user's (or the app default's)
    ///     spelling for the canonical one.
    /// </summary>
    private static bool NeedsRespelling(string[] current, int[] steps)
    {
        if (current.Length != steps.Length) return true;
        var currentSteps = TuningOffsetResolution.DeriveFromNoteNames(current);
        return currentSteps is null || !currentSteps.SequenceEqual(steps);
    }

    public static Arrangement Parse(EofPackReader r, EofTrackInfo info, IList<BeatMarker> beats)
    {
        var arrangement = new Arrangement
        {
            Name = string.IsNullOrWhiteSpace(info.Name) ? $"Track {info.Id}" : info.Name,
            ArrangementType = InferArrangementType(info.Name)
        };

        var _ = r.ReadU8(); // numFrets (unused; preserved for protocol position)
        var numStrings = r.ReadU8();
        if (numStrings > 8)
            throw new InvalidDataException($"Unsupported EOF string count: {numStrings} (max 8).");
        arrangement.StringCount = numStrings;

        var tuningSteps = new int[numStrings];
        for (var i = 0; i < numStrings; i++) tuningSteps[i] = (sbyte)r.ReadU8();
        arrangement.TuningPitches.Clear();
        foreach (var step in tuningSteps) arrangement.TuningPitches.Add(new TuningOffset(step));

        // A chart states its tuning TWICE — offsets AND note letters — and this
        // reader filled only the offsets, so an imported EOF arrangement kept
        // Arrangement's DEFAULT six-name {E,A,D,G,B,e} whatever the file said.
        // The letters drive the lane gutter labels, the tuner, the tuning chip,
        // ChordNameSuggester and the sheet export, and on a 4-string bass the
        // stale six-entry default also named two strings that do not exist.
        // Every other importer syncs both (GpArrangementParser, GpLegacyImporter,
        // SongsterrConverter, RscpArrangementParser, the package reader's SNG mapper,
        // RsXmlSongReader); this one was the last outlier — and it is the format
        // the hand-corrected TestData/EOF corpus is authored in.
        //
        // Re-spelled only when the letters would say something DIFFERENT. An
        // .eof stores OFFSETS and no letters at all, so a spelling the file
        // cannot contradict is left exactly as it stands — which is what keeps
        // the round trip lossless for the app's own default {E,A,D,G,B,e},
        // whose lowercase top string GetTuningNames would otherwise normalise
        // to "E" and report as an ArrangementHeader change.
        if (NeedsRespelling(arrangement.Tuning, tuningSteps))
            arrangement.Tuning = TuningUtilities.GetTuningNames(numStrings, tuningSteps);

        var rawNotes = new List<EofRawProGuitarNote>();
        var noteCount = r.ReadU32();
        for (var i = 0; i < noteCount; i++)
            rawNotes.Add(EofProGuitarNoteReader.Read(r));

        // Sections: capture anchors (16), arpeggios (10) / handshapes (19),
        // trills (9), tremolos (14), and tone changes (18).
        var rawAnchors = new List<(int diff, uint posMs, uint fret)>();
        var arpeggios = new List<(int diff, uint startMs, uint endMs, bool isArpeggio)>();
        // ReSharper disable once CollectionNeverQueried.Local -- captured for future use; currently informational only.
        var handshapes = new List<(int diff, uint startMs, uint endMs)>();
        var trills = new List<(int diff, uint startMs, uint endMs)>();
        var tremolos = new List<(int diff, uint startMs, uint endMs)>();
        var toneChanges = new List<(string name, uint posMs, bool isDefault)>();
        var typeCount = r.ReadU16();
        for (var t = 0; t < typeCount; t++)
        {
            var sectionType = r.ReadU16();
            var instances = r.ReadU32();
            for (var i = 0; i < instances; i++)
            {
                var name = r.ReadString();
                var diff = r.ReadU8();
                var startMs = r.ReadU32();
                var endOrFret = r.ReadU32(); // For FHP: fret. For tone change: 1 = default tone marker.
                var sectionFlags = r.ReadU32();

                switch (sectionType)
                {
                    case EofConstants.SectionFretHandPos:
                        rawAnchors.Add((diff, startMs, endOrFret));
                        break;
                    case EofConstants.SectionArpeggio:
                    case EofConstants.SectionHandshape:
                        // EOF stores handshapes IN the arpeggio array; the
                        // RS_ARP_HANDSHAPE flag (or the legacy dedicated
                        // handshape type) marks a plain handshape — only a
                        // bare type-10 instance is a true arpeggio.
                        var isArpeggio = sectionType == EofConstants.SectionArpeggio &&
                                         (sectionFlags & EofConstants.SectionFlagRsArpHandshape) == 0;
                        arpeggios.Add((diff, startMs, endOrFret, isArpeggio));
                        if (sectionType == EofConstants.SectionHandshape)
                            handshapes.Add((diff, startMs, endOrFret));
                        break;
                    case EofConstants.SectionTrill:
                        trills.Add((diff, startMs, endOrFret));
                        break;
                    case EofConstants.SectionTremolo:
                        tremolos.Add((diff, startMs, endOrFret));
                        break;
                    case EofConstants.SectionRsToneChange:
                        toneChanges.Add((name, startMs, endOrFret == 1));
                        break;
                }
            }
        }

        // Custom data blocks — fingers (2), arrangement type (3), capo (6) are honored.
        var fingerByNote = new Dictionary<int, int[]>();
        var rawTechNotes = new List<EofRawProGuitarNote>();
        var blockCount = r.ReadU32();
        for (var b = 0; b < blockCount; b++)
        {
            var size = r.ReadU32();
            var id = r.ReadU32();
            if (size == 0) continue;
            var payloadSize = (int)Math.Max(0L, (long)size - 4);
            var consumed = ConsumeCustomBlock(r, id, payloadSize, rawNotes, rawTechNotes, fingerByNote, arrangement,
                numStrings);
            if (consumed < payloadSize) r.Skip(payloadSize - consumed);
        }

        var graceFretByNote = FoldGraceNotePairs(rawNotes, arrangement.Capo);
        BuildLevels(arrangement, rawNotes, rawTechNotes, fingerByNote, beats, trills, tremolos, graceFretByNote,
            out var ghostShapes);
        ApplyAnchors(arrangement, rawAnchors, beats);
        ApplyArpeggioHandShapes(arrangement, arpeggios, beats, ghostShapes);
        ApplyToneChanges(arrangement, toneChanges, beats);
        return arrangement;
    }

    /// <summary>Delegates to the shared tab-import heuristic — one keyword table
    /// for EOF, gpif, GP3-5 binary and Songsterr (see
    /// <see cref="ArrangementTypeHeuristics"/>).</summary>
    internal static ArrangementType InferArrangementType(string? trackName,
        ArrangementType fallback = ArrangementType.Lead) => ArrangementTypeHeuristics.FromTrackName(trackName, fallback);

    private static void ApplyToneChanges(Arrangement arrangement,
        List<(string name, uint posMs, bool isDefault)> toneChanges, IList<BeatMarker> beats)
    {
        if (toneChanges.Count == 0) return;
        // The default tone — used as the StartingTone — is the one explicitly flagged
        // (end_pos == 1 in the EOF section), falling back to the earliest tone change.
        var ordered = toneChanges.OrderBy(c => c.posMs).ToList();
        var defaultTone = ordered.FirstOrDefault(c => c.isDefault).name ?? ordered[0].name;
        if (!string.IsNullOrWhiteSpace(defaultTone))
            arrangement.StartingTone = defaultTone;

        foreach (var (name, posMs, _) in ordered)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            arrangement.ToneChanges.Add(new ToneChange
            {
                Position = TimelineMath.PositionFromImportedMs(posMs, beats),
                ToneName = name
            });
            if (!arrangement.ToneNames.Contains(name))
                arrangement.ToneNames.Add(name);
        }
    }

    private static void ApplyArpeggioHandShapes(Arrangement arrangement,
        List<(int diff, uint startMs, uint endMs, bool isArpeggio)> arpeggios, IList<BeatMarker> beats,
        Dictionary<(int diff, uint posMs), int> ghostShapeTemplates)
    {
        if (arpeggios.Count == 0) return;
        foreach (var (diff, startMs, endMs, isArpeggio) in arpeggios)
        {
            var level = arrangement.Levels.FirstOrDefault(l => l.DifficultyIndex == diff);
            if (level == null) continue;
            var start = TimelineMath.PositionFromImportedMs(startMs, beats);
            var end = TimelineMath.PositionFromImportedMs(endMs, beats);

            // EOF stores the handshape's chord *shape* on a ghost-gem note that
            // sits inside the handshape window: the played note is e.g. just
            // fret 10 on string 5, but the same EOF note carries ghost frets
            // on the other strings to record the full chord shape + fingering
            // for the handshape. We register one ChordTemplate per such note
            // (in BuildLevels) and prefer those here so the handshape renders
            // with the *intended* fingers/frets instead of inheriting from a
            // possibly unrelated nearby chord.
            var templateId = -1;
            foreach (var ((d, posMs), tid) in ghostShapeTemplates)
            {
                if (d != diff) continue;
                if (posMs < startMs || posMs > endMs) continue;
                templateId = tid;
                break;
            }
            if (templateId < 0)
                templateId = ResolveHandShapeTemplate(level, start, end);

            // A true arpeggio must reference an Arpeggio template — the chord
            // it resolved to keeps its own plain template, so find-or-create
            // an arpeggio variant of the shape instead of mutating it.
            if (isArpeggio)
                templateId = EnsureArpeggioTemplate(arrangement, templateId);

            level.HandShapes.Add(new HandShape
            {
                Position = start,
                EndPosition = end,
                Name = "Arpeggio",
                TemplateId = templateId
            });
        }
    }

    private static int EnsureArpeggioTemplate(Arrangement arrangement, int templateId)
    {
        if (templateId < 0) return templateId;
        var source = arrangement.ChordTemplates.FirstOrDefault(t => t.Id == templateId);
        if (source == null || source.Arpeggio) return templateId;

        var existing = arrangement.ChordTemplates.FirstOrDefault(t => t.Arpeggio && t.HasSameShapeAs(source));
        if (existing != null) return existing.Id;

        var clone = source.Clone();
        clone.Id = arrangement.ChordTemplates.Count;
        clone.Arpeggio = true;
        arrangement.ChordTemplates.Add(clone);
        return clone.Id;
    }

    private static int ResolveHandShapeTemplate(ChartLevel level,
        FractionalPosition start,
        FractionalPosition end)
    {
        Chord? best = null;
        foreach (var c in level.Chords)
        {
            if (c.Position < start) continue;
            if (c.Position > end) break;
            best = c;
            break;
        }
        if (best == null)
        {
            // No chord strictly inside the window — fall back to the closest
            // chord at or after the start so single-note arpeggios still render.
            foreach (var c in level.Chords)
            {
                if (c.Position >= start)
                {
                    best = c;
                    break;
                }
            }
        }
        return best?.TemplateId ?? -1;
    }

    private static void ApplyAnchors(Arrangement arrangement,
        List<(int diff, uint posMs, uint fret)> rawAnchors, IList<BeatMarker> beats)
    {
        if (rawAnchors.Count == 0) return;

        foreach (var (diff, posMs, fret) in rawAnchors)
        {
            if (fret == 0) continue;
            var level = arrangement.Levels.FirstOrDefault(l => l.DifficultyIndex == diff);
            if (level == null)
            {
                level = new ChartLevel { DifficultyIndex = diff };
                arrangement.Levels.Add(level);
            }

            var pos = TimelineMath.PositionFromImportedMs(posMs, beats);
            // Anchor (Fret Hand Position) frets are also stored relative to the
            // un-capoed fretboard — add the arrangement capo so the FHP lands
            // on the in-game fret EOF would render (rs.c: end_pos + tp->capo).
            var capoFret = (int)fret + arrangement.Capo;
            level.Anchors.Add(new FretAnchor(pos, capoFret));
        }
    }

    private static int ConsumeCustomBlock(EofPackReader r, uint id, int _,
        List<EofRawProGuitarNote> rawNotes, List<EofRawProGuitarNote> rawTechNotes, Dictionary<int, int[]> fingerByNote,
        Arrangement arrangement, int numStrings)
    {
        var startPos = r.Position;
        switch (id)
        {
            case EofConstants.PgCustomFingerArray:
                // EOF writes one finger byte per *used* string (per its bitmask) over
                // the actual string count of the track, NOT a fixed 8 strings.
                for (var i = 0; i < rawNotes.Count; i++)
                {
                    var note = rawNotes[i];
                    var fingers = new int[Math.Max(numStrings, 1)];
                    for (var s = 0; s < numStrings; s++)
                    {
                        if ((note.NoteBitmask & (1 << s)) != 0)
                            fingers[s] = r.ReadU8();
                    }

                    fingerByNote[i] = fingers;
                }
                break;
            case EofConstants.PgCustomArrangementType:
                var arrType = r.ReadU8();
                arrangement.ArrangementType = arrType switch
                {
                    EofConstants.ArrTypeCombo => ArrangementType.Combo,
                    EofConstants.ArrTypeRhythm => ArrangementType.Rhythm,
                    EofConstants.ArrTypeLead => ArrangementType.Lead,
                    EofConstants.ArrTypeBass => ArrangementType.Bass,
                    _ => arrangement.ArrangementType
                };
                break;
            case EofConstants.PgCustomCapoPosition:
                arrangement.Capo = r.ReadU8();
                break;
            case EofConstants.PgCustomTechNotes:
            {
                var techNoteCount = (int)r.ReadU32();
                for (var i = 0; i < techNoteCount; i++)
                    rawTechNotes.Add(EofProGuitarNoteReader.Read(r));
                break;
            }
            default:
                // Unhandled block — caller will skip remainder.
                return 0;
        }

        return r.Position - startPos;
    }

    /// <summary>
    ///     Detects EOF-style grace pairs — a short (≤130 ms) note carrying
    ///     LINKNEXT plus a pitched RS_NOTATION slide, immediately followed by
    ///     its slide target on the same string(s) and difficulty — and folds
    ///     them back into DotCharter's grace-fret hints. The result maps each
    ///     surviving TARGET note to its per-string grace frets:
    ///     <see cref="GuitarNote.GraceFret"/> for a single-string ornament and
    ///     <see cref="ChordNote.GraceFret"/> for a chord grace (Guitar Pro
    ///     graces a strum with one grace note per string, so EOF's grace note
    ///     carries the whole graced sub-shape). This is the exact inverse of
    ///     <c>EofNoteBuilder</c>'s grace materialization; EOF's own GP import
    ///     writes the same shape (34–68 ms grace + displaced target).
    /// </summary>
    private static Dictionary<EofRawProGuitarNote, Dictionary<int, int>> FoldGraceNotePairs(
        List<EofRawProGuitarNote> rawNotes, int capo)
    {
        var graceFrets = new Dictionary<EofRawProGuitarNote, Dictionary<int, int>>();
        var consumed = new HashSet<EofRawProGuitarNote>();
        for (var i = 0; i < rawNotes.Count; i++)
        {
            var g = rawNotes[i];
            if (consumed.Contains(g)) continue;
            if (g.LengthMs is 0 or > 130) continue;
            if ((g.Flags & EofConstants.PgFlagLinkNext) == 0) continue;
            if ((g.Flags & EofConstants.PgFlagRsNotation) == 0) continue;
            if ((g.Flags & (EofConstants.PgFlagSlideUp | EofConstants.PgFlagSlideDown)) == 0) continue;

            var live = (byte)(g.NoteBitmask & ~g.GhostBitmask);
            var liveCount = PopCount(live);
            if (liveCount == 0) continue;
            var slideEnd = g.SlideEndFret & EofConstants.FretValueMask;
            if (slideEnd <= 0) continue;

            var graceEnd = (ulong)g.PositionMs + g.LengthMs;
            for (var j = i + 1; j < rawNotes.Count; j++)
            {
                var m = rawNotes[j];
                if (m.PositionMs > graceEnd + 2) break; // notes are position-sorted
                if (m.PositionMs + 2 < graceEnd) continue;
                if (m.Difficulty != g.Difficulty) continue;
                var mLive = (byte)(m.NoteBitmask & ~m.GhostBitmask);

                var hints = liveCount == 1
                    ? MatchSingleStringGrace(g, m, live, mLive, slideEnd, capo)
                    : MatchChordGrace(g, m, live, mLive, slideEnd, capo);
                if (hints == null) continue;

                m.PositionMs = g.PositionMs;
                m.LengthMs += g.LengthMs;
                graceFrets[m] = hints;
                consumed.Add(g);
                break;
            }
        }

        if (consumed.Count > 0) rawNotes.RemoveAll(consumed.Contains);
        return graceFrets;
    }

    /// <summary>
    ///     The single-string half of <see cref="FoldGraceNotePairs"/>, unchanged
    ///     since it shipped: the target must sound the grace's one string at
    ///     exactly the stored slide end fret. A fret-0 (open) grace is a real
    ///     hint value, not "absent". Null = not a grace pair.
    /// </summary>
    private static Dictionary<int, int>? MatchSingleStringGrace(EofRawProGuitarNote g, EofRawProGuitarNote m,
        byte live, byte mLive, int slideEnd, int capo)
    {
        var s = FirstSetBit(live);
        if ((mLive & (1 << s)) == 0) return null;
        if ((m.Frets[s] & EofConstants.FretMutedBit) != 0) return null;
        if ((m.Frets[s] & EofConstants.FretValueMask) != slideEnd) return null;

        var gFret = g.Frets[s] & EofConstants.FretValueMask;
        return new Dictionary<int, int> { [s] = gFret > 0 ? gFret + capo : 0 };
    }

    /// <summary>
    ///     The multi-string half of <see cref="FoldGraceNotePairs"/>. EOF stores
    ///     ONE slide end fret — that of the note's LOWEST fretted string — and
    ///     projects the resulting distance onto every string, so a chord grace
    ///     only matches when the target sounds EVERY graced string exactly one
    ///     uniform distance away. Every graced string needs a fretted (non-open,
    ///     unmuted) origin, because an open string has no slide origin. Null =
    ///     not a grace pair, and the two notes stay independent chords.
    /// </summary>
    private static Dictionary<int, int>? MatchChordGrace(EofRawProGuitarNote g, EofRawProGuitarNote m,
        byte live, byte mLive, int slideEnd, int capo)
    {
        int? lowest = null;
        for (var s = 0; s < 8; s++)
        {
            if ((live & (1 << s)) == 0) continue;
            if ((g.Frets[s] & EofConstants.FretMutedBit) != 0) return null;
            var fret = g.Frets[s] & EofConstants.FretValueMask;
            if (fret <= 0) return null;
            if (lowest == null || fret < lowest.Value) lowest = fret;
        }

        if (lowest is null) return null;
        var distance = slideEnd - lowest.Value;
        if (distance == 0) return null;

        var hints = new Dictionary<int, int>(8);
        for (var s = 0; s < 8; s++)
        {
            if ((live & (1 << s)) == 0) continue;
            if ((mLive & (1 << s)) == 0) return null;
            if ((m.Frets[s] & EofConstants.FretMutedBit) != 0) return null;
            var gFret = g.Frets[s] & EofConstants.FretValueMask;
            if ((m.Frets[s] & EofConstants.FretValueMask) != gFret + distance) return null;
            hints[s] = gFret + capo;
        }

        return hints;
    }

    private static void BuildLevels(Arrangement arrangement, List<EofRawProGuitarNote> rawNotes,
        List<EofRawProGuitarNote> rawTechNotes, Dictionary<int, int[]> fingerByNote, IList<BeatMarker> beats,
        List<(int diff, uint startMs, uint endMs)> trills,
        List<(int diff, uint startMs, uint endMs)> tremolos,
        Dictionary<EofRawProGuitarNote, Dictionary<int, int>> graceFretByNote,
        out Dictionary<(int diff, uint posMs), int> ghostShapeTemplates)
    {
        var capo = arrangement.Capo;
        ghostShapeTemplates = [];
        var techNotesByDifficulty = rawTechNotes
            .GroupBy(n => (int)n.Difficulty)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<EofRawProGuitarNote>)g.OrderBy(n => n.PositionMs).ToList());

        // Group raw notes by EOF "type" (difficulty index).
        var byDifficulty = new Dictionary<int, List<(int idx, EofRawProGuitarNote note)>>();
        for (var i = 0; i < rawNotes.Count; i++)
        {
            var note = rawNotes[i];
            if (!byDifficulty.TryGetValue(note.Difficulty, out var list))
                byDifficulty[note.Difficulty] = list = [];
            list.Add((i, note));
        }

        // ChordTemplate dedupe across all chords on the arrangement so identical
        // shapes share a single template id (matches Rocksmith XML behaviour).
        var templateCache = new Dictionary<string, ChordTemplate>();

        foreach (var kv in byDifficulty.OrderBy(k => k.Key))
        {
            var level = new ChartLevel { DifficultyIndex = kv.Key };
            techNotesByDifficulty.TryGetValue(kv.Key, out var techNotes);
            foreach (var (idx, note) in kv.Value)
            {
                // EOF stores ghost gems as a bitmask alongside the note bitmask
                // (see song.c eof_read_pro_guitar_note: ghost is read right after
                // note). Ghost gems serve two purposes in EOF projects:
                //   1) Pure visual guide notes the editor draws transparently.
                //   2) A fingering hint: the played note may be a single gem
                //      (e.g. fret 10 on string 5) but the same EOF note carries
                //      ghost frets+fingers on additional strings to record the
                //      *handshape* shape that should be displayed for the
                //      surrounding arpeggio/handshape window.
                // We strip ghost gems from the played note (so it stays a clean
                // single note / chord without the ghost frets) but, when the
                // ghost bitmask combined with the live bits forms a multi-string
                // shape, we register a ChordTemplate from the FULL bitmask so
                // ApplyArpeggioHandShapes can use the intended shape+fingers.
                var liveBitmask = (byte)(note.NoteBitmask & ~note.GhostBitmask);
                var setBits = PopCount(liveBitmask);

                var fingers = fingerByNote.GetValueOrDefault(idx);

                // Register the ghost-derived handshape template if this note
                // actually has ghost strings. We use the full NoteBitmask
                // (live + ghost) to capture the complete shape EOF intended
                // for the handshape, including its fingers. A PURE-ghost note
                // registers too: EOF exports a fully ghosted base chord as
                // the handshape's shape (rs.c:2313) — a tapped harmonic's fretting
                // finger is one such note.
                if (note.GhostBitmask != 0 && (PopCount(note.NoteBitmask) > 1 || setBits == 0))
                {
                    var ghostTid = BuildShapeTemplate(note, note.NoteBitmask, fingers, capo,
                        arrangement, templateCache);
                    ghostShapeTemplates[(note.Difficulty, note.PositionMs)] = ghostTid;
                }

                if (setBits == 0) continue; // Pure-ghost note: nothing is played.

                var inTrill = trills.Any(s => s.diff == note.Difficulty &&
                                              note.PositionMs >= s.startMs &&
                                              note.PositionMs <= s.endMs);
                var inTremolo = tremolos.Any(s => s.diff == note.Difficulty &&
                                                  note.PositionMs >= s.startMs &&
                                                  note.PositionMs <= s.endMs);

                graceFretByNote.TryGetValue(note, out var graceFrets);

                if (setBits == 1)
                {
                    var n = BuildSingleNote(note, liveBitmask, capo, beats, techNotes);
                    if (inTremolo || inTrill) n.Tremolo = true;
                    if (graceFrets != null && graceFrets.TryGetValue(n.StringIndex, out var graceFret))
                        n.GraceFret = graceFret;
                    level.Notes.Add(n);
                }
                else
                {
                    // A chord grace carries one hint per graced STRING; a
                    // single-string grace whose target happens to be a chord
                    // lands here too (folding it away would
                    // silently drop it, losing the ornament AND the grace note).
                    var chord = BuildChord(note, liveBitmask, fingers, capo, beats, arrangement, templateCache,
                        techNotes, graceFrets);
                    // A tremolo PHRASE covers chords too: EOF's phrase status marks
                    // every note inside it, chords included (EOF_NOTE_FLAG_IS_TREMOLO,
                    // main.c:1770-1784), and its RS export
                    // writes that as each chord note's tremolo (rs.c:5735, :5807). A
                    // split pick scrape is a tremolo chord.
                    if (inTremolo)
                    {
                        foreach (var chordNote in chord.ChordNotes) chordNote.Tremolo = true;
                    }

                    level.Chords.Add(chord);
                }
            }

            arrangement.Levels.Add(level);
        }

        if (arrangement.Levels.Count == 0)
            arrangement.Levels.Add(new ChartLevel());
    }

    /// <summary>
    ///     Builds (and caches) a ChordTemplate from the given string bitmask
    ///     and EOF raw note, using the supplied finger array. Returns the
    ///     template id. Used for ghost-gem-derived handshape shapes that
    ///     never become real played chords.
    /// </summary>
    private static int BuildShapeTemplate(EofRawProGuitarNote raw, byte bitmask, int[]? fingers, int capo,
        Arrangement arrangement, Dictionary<string, ChordTemplate> templateCache)
    {
        var fretMap = new Dictionary<int, int>();
        var fingerMap = new Dictionary<int, int>();
        var fingerless = IsFingerless(raw);
        for (var s = 0; s < 8; s++)
        {
            if ((bitmask & (1 << s)) == 0) continue;
            var (fret, _) = DecodeFret(raw.Frets[s]);
            if (fret > 0) fret += capo;
            fretMap[s] = fret;
            if (!fingerless && fingers != null && s < fingers.Length && fingers[s] > 0)
                fingerMap[s] = RemapFinger(fingers[s]);
        }
        // The EOF ghost bits become the grip's ghost gems (song.h:200 — bit n = string
        // n, the same numbering): held, drawn dimmed, never played.
        var ghostMask = raw.GhostBitmask & bitmask;
        return GetOrCreateTemplate(arrangement, templateCache, fretMap, fingerMap, raw.Name, fingerless, ghostMask).Id;
    }

    private static GuitarNote BuildSingleNote(EofRawProGuitarNote raw, byte liveBitmask, int capo,
        IList<BeatMarker> beats, IReadOnlyList<EofRawProGuitarNote>? techNotes)
    {
        var stringIdx = FirstSetBit(liveBitmask);
        var pos = TimelineMath.PositionFromImportedMs(raw.PositionMs, beats);
        var endPos = TimelineMath.PositionFromImportedMs(raw.PositionMs + raw.LengthMs, beats);

        var (fret, stringMuted) = DecodeFret(raw.Frets[stringIdx]);
        if (fret > 0) fret += capo;

        var applicableTechNote = FindApplicableTechNote(techNotes, raw, stringIdx);
        var (slideTo, unpitchedSlide) = ResolveEffectiveSlide(raw, applicableTechNote, fret, fret > 0 ? fret : null, capo);

        var note = new GuitarNote
        {
            Position = pos,
            Sustain = endPos - pos,
            StringIndex = stringIdx,
            Fret = fret,
            LinkNext = (raw.Flags & EofConstants.PgFlagLinkNext) != 0,
            Accent = (raw.Flags & EofConstants.PgFlagAccent) != 0,
            Ignore = (raw.ExtendedFlags & EofConstants.PgEFlagIgnore) != 0,
            PassOtherNotes = (raw.Flags & EofConstants.NoteFlagCrazy) != 0,
            Vibrato = (raw.Flags & EofConstants.PgFlagVibrato) != 0,
            VibratoStrength = (raw.Flags & EofConstants.PgFlagVibrato) != 0 ? 80 : 0,
            UnpitchedSlide = unpitchedSlide,
            Mute = MapMute(raw.Flags, stringMuted),
            Hopo = MapHopo(raw.Flags),
            Harmonic = MapHarmonic(raw.Flags),
            BassPicking = MapBassPicking(raw.Flags),
            SlideTo = slideTo
        };

        // A tech-note curve (block 7) carries the bend's real shape and
        // timing; only fall back to the folded note-level two-point synthesis
        // when no curve exists.
        if (!TryAddTechNoteBendCurve(note.BendValues, techNotes, raw, stringIdx, beats)
            && (raw.Flags & EofConstants.PgFlagBend) != 0 && raw.BendStrength > 0)
        {
            AddBend(note.BendValues, pos, note.Sustain, raw.BendStrength);
        }

        return note;
    }

    /// <summary>
    ///     Rebuilds a bend curve from BEND tech notes (custom block 7) inside
    ///     the note's window on the given string — the read-side counterpart of
    ///     the exporter's per-bend-value tech-note emission (and how real EOF
    ///     stores GP bend curves). Returns false when the note has no bend
    ///     tech notes at all.
    /// </summary>
    private static bool TryAddTechNoteBendCurve(ObservableCollection<BendValue> bends,
        IReadOnlyList<EofRawProGuitarNote>? techNotes, EofRawProGuitarNote raw, int stringIndex,
        IList<BeatMarker> beats)
    {
        if (techNotes == null || techNotes.Count == 0 || stringIndex is < 0 or > 7) return false;
        var stringBit = 1 << stringIndex;
        var noteEnd = (ulong)raw.PositionMs + raw.LengthMs;
        var added = false;
        // Start at the note's onset instead of index 0 — see FirstTechNoteAtOrAfter.
        for (var i = FirstTechNoteAtOrAfter(techNotes, raw.PositionMs); i < techNotes.Count; i++)
        {
            var tech = techNotes[i];
            if (tech.PositionMs > noteEnd) break; // the list is position-sorted
            if (tech.Difficulty != raw.Difficulty) continue;
            if ((tech.NoteBitmask & stringBit) == 0) continue;
            if ((tech.Flags & EofConstants.PgFlagBend) == 0) continue;
            bends.Add(new BendValue(TimelineMath.PositionFromImportedMs(tech.PositionMs, beats),
                DecodeBendStrengthSemitones(tech.BendStrength)));
            added = true;
        }

        return added;
    }

    private static Chord BuildChord(EofRawProGuitarNote raw, byte liveBitmask, int[]? fingers, int capo,
        IList<BeatMarker> beats, Arrangement arrangement, Dictionary<string, ChordTemplate> templateCache,
        IReadOnlyList<EofRawProGuitarNote>? techNotes, IReadOnlyDictionary<int, int>? graceFrets = null)
    {
        var pos = TimelineMath.PositionFromImportedMs(raw.PositionMs, beats);
        var endPos = TimelineMath.PositionFromImportedMs(raw.PositionMs + raw.LengthMs, beats);
        var sustain = endPos - pos;

        var chord = new Chord
        {
            Position = pos,
            Accent = (raw.Flags & EofConstants.PgFlagAccent) != 0,
            Ignore = (raw.ExtendedFlags & EofConstants.PgEFlagIgnore) != 0,
            PassOtherNotes = (raw.Flags & EofConstants.NoteFlagCrazy) != 0,
            // EOF's Split status (song.h:64): ships as single notes.
            SplitIntoNotes = (raw.Flags & EofConstants.PgFlagSplit) != 0
        };
        var fingerless = IsFingerless(raw);

        var hopo = MapHopo(raw.Flags);
        var harmonic = MapHarmonic(raw.Flags);
        var bassPicking = MapBassPicking(raw.Flags);
        var lowestFrettedNote = GetLowestAdjustedChordFret(raw, liveBitmask, capo);

        // Build fret + finger maps for this chord — these double as the dedup key
        // for the shared ChordTemplate cache.
        var fretMap = new Dictionary<int, int>();
        var fingerMap = new Dictionary<int, int>();

        var hasBend = (raw.Flags & EofConstants.PgFlagBend) != 0 && raw.BendStrength > 0;
        var vibrato = (raw.Flags & EofConstants.PgFlagVibrato) != 0;

        for (var s = 0; s < 8; s++)
        {
            // Ghost strings have already been removed from liveBitmask so the
            // chord only contains the gems EOF would actually render solid.
            if ((liveBitmask & (1 << s)) == 0) continue;
            var (fret, stringMuted) = DecodeFret(raw.Frets[s]);
            if (fret > 0) fret += capo;
            var techNote = FindApplicableTechNote(techNotes, raw, s);
            var (slideTo, unpitchedSlide) = ResolveEffectiveSlide(raw, techNote, fret, lowestFrettedNote, capo);
            var cn = new ChordNote
            {
                StringIndex = s,
                Fret = fret,
                Sustain = sustain,
                Mute = MapMute(raw.Flags, stringMuted),
                Hopo = hopo,
                Harmonic = harmonic,
                BassPicking = bassPicking,
                LinkNext = (raw.Flags & EofConstants.PgFlagLinkNext) != 0,
                Vibrato = vibrato,
                VibratoStrength = vibrato ? 80 : 0,
                UnpitchedSlide = unpitchedSlide,
                SlideTo = slideTo
            };
            if (!TryAddTechNoteBendCurve(cn.BendValues, techNotes, raw, s, beats) && hasBend)
                AddBend(cn.BendValues, pos, sustain, raw.BendStrength);
            if (graceFrets != null && graceFrets.TryGetValue(s, out var graceFret)) cn.GraceFret = graceFret;
            chord.ChordNotes.Add(cn);
            fretMap[s] = fret;
            // Finger numbers on the EOF chord are independent of the handshape
            // window and travel with the note itself; populate them whenever
            // EOF wrote a non-zero finger for the string so the chord template
            // (and thus the fingering renderer) shows them even when no
            // arpeggio/handshape phrase wraps the chord.
            if (!fingerless && fingers != null && s < fingers.Length && fingers[s] > 0)
                fingerMap[s] = RemapFinger(fingers[s]);
        }

        var template = GetOrCreateTemplate(arrangement, templateCache, fretMap, fingerMap, raw.Name, fingerless);
        chord.TemplateId = template.Id;

        return chord;
    }

    internal static EofRawProGuitarNote? FindApplicableTechNote(
        IReadOnlyList<EofRawProGuitarNote>? techNotes, EofRawProGuitarNote raw, int stringIndex)
    {
        if (techNotes == null || techNotes.Count == 0 || stringIndex is < 0 or > 7)
            return null;

        var stringBit = 1 << stringIndex;
        var noteEnd = (ulong)raw.PositionMs + raw.LengthMs;
        EofRawProGuitarNote? match = null;

        // Start at the note's onset instead of index 0 — see FirstTechNoteAtOrAfter.
        for (var i = FirstTechNoteAtOrAfter(techNotes, raw.PositionMs); i < techNotes.Count; i++)
        {
            var tech = techNotes[i];
            if (tech.PositionMs > noteEnd) break;
            if (tech.Difficulty != raw.Difficulty) continue;
            if ((tech.NoteBitmask & stringBit) == 0) continue;
            match = tech;
        }

        return match;
    }

    /// <summary>
    ///     Index of the first tech note at or after <paramref name="positionMs" />.
    ///     <para>
    ///     Both tech-note helpers must not scan from index 0 and <c>continue</c>
    ///     past everything before the note's onset (only the upper bound
    ///     <c>break</c>s). They run once per single note and once per STRING of
    ///     every chord, so a bend-heavy corpus chart (5k notes × 6 strings × 3k
    ///     tech notes) would be ~10^8 comparisons on a single track import.
    ///     </para>
    ///     <para>
    ///     The list is position-sorted — which is already the assumption behind
    ///     the existing <c>break</c> — so this binary search returns exactly the
    ///     index the linear skip would have stopped at.
    ///     </para>
    /// </summary>
    private static int FirstTechNoteAtOrAfter(IReadOnlyList<EofRawProGuitarNote> techNotes, uint positionMs)
    {
        var lo = 0;
        var hi = techNotes.Count;
        while (lo < hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            if (techNotes[mid].PositionMs < positionMs) lo = mid + 1;
            else hi = mid;
        }

        return lo;
    }

    internal static (int? SlideTo, bool UnpitchedSlide) ResolveEffectiveSlide(
        EofRawProGuitarNote raw, EofRawProGuitarNote? techNote, int currentFret, int? lowestFrettedNote, int capo)
    {
        if (techNote != null)
        {
            var techHasPitchedSlide = (techNote.Flags & (EofConstants.PgFlagSlideUp | EofConstants.PgFlagSlideDown)) != 0;
            var techHasUnpitchedSlide = (techNote.Flags & EofConstants.PgFlagUnpitchedSlide) != 0;
            if (techHasUnpitchedSlide)
            {
                var slideTo = ResolveChordSlideTarget(techNote.Flags, techNote.SlideEndFret, techNote.UnpitchedSlideEndFret,
                    currentFret, currentFret > 0 ? currentFret : null, capo);
                return (slideTo, true);
            }

            if (techHasPitchedSlide)
            {
                var slideTo = ResolveChordSlideTarget(techNote.Flags, techNote.SlideEndFret, techNote.UnpitchedSlideEndFret,
                    currentFret, currentFret > 0 ? currentFret : null, capo)
                              ?? ResolveImplicitTechSlideTarget(techNote.Flags, currentFret);
                return (slideTo, false);
            }
        }

        return (
            ResolveChordSlideTarget(raw.Flags, raw.SlideEndFret, raw.UnpitchedSlideEndFret,
                currentFret, lowestFrettedNote, capo),
            (raw.Flags & EofConstants.PgFlagUnpitchedSlide) != 0
        );
    }

    internal static int? ResolveChordSlideTarget(uint flags, byte slideEndFret, byte unpitchedSlideEndFret,
        int currentFret, int? lowestFrettedNote, int capo)
    {
        if (currentFret <= 0 || lowestFrettedNote is not > 0)
            return null;

        if ((flags & (EofConstants.PgFlagSlideUp | EofConstants.PgFlagSlideDown)) != 0
            && slideEndFret > 0)
        {
            return ResolveChordSlideTargetCore(currentFret, lowestFrettedNote.Value,
                (slideEndFret & EofConstants.FretValueMask) + capo);
        }

        if ((flags & EofConstants.PgFlagUnpitchedSlide) != 0 && unpitchedSlideEndFret > 0)
        {
            return ResolveChordSlideTargetCore(currentFret, lowestFrettedNote.Value,
                (unpitchedSlideEndFret & EofConstants.FretValueMask) + capo);
        }

        return null;
    }

    private static int? ResolveChordSlideTargetCore(int currentFret, int lowestFrettedNote, int slideEndFret)
    {
        if (slideEndFret == lowestFrettedNote)
            return null;

        var slideDistance = slideEndFret - lowestFrettedNote;
        return currentFret + slideDistance;
    }

    private static int? ResolveImplicitTechSlideTarget(uint flags, int currentFret)
    {
        if (currentFret <= 0) return null;
        if ((flags & EofConstants.PgFlagSlideUp) != 0) return currentFret + 1;
        if ((flags & EofConstants.PgFlagSlideDown) != 0 && currentFret > 1) return currentFret - 1;
        return null;
    }

    private static int? GetLowestAdjustedChordFret(EofRawProGuitarNote raw, byte liveBitmask, int capo)
    {
        int? lowest = null;
        for (var s = 0; s < 8; s++)
        {
            if ((liveBitmask & (1 << s)) == 0) continue;
            var (fret, _) = DecodeFret(raw.Frets[s]);
            if (fret <= 0) continue;
            fret += capo;
            if (lowest == null || fret < lowest.Value)
                lowest = fret;
        }

        return lowest;
    }

    /// <summary>
    ///     Find-or-create a template for a shape. <paramref name="name" /> is the
    ///     AUTHORED chord name EOF wrote on the note (and that our own exporter
    ///     writes back at <c>EofNoteBuilder</c>) — without it a round trip renames
    ///     every chord, because <see cref="ChordNameSuggester" /> re-guesses the
    ///     blank. The dedupe key stays frets+fingers only (one shape = one shared
    ///     template), so the FIRST non-empty name a shape is seen with wins; a
    ///     later differing name never rewrites a template other chords already use.
    /// </summary>
    private static ChordTemplate GetOrCreateTemplate(Arrangement arrangement,
        Dictionary<string, ChordTemplate> cache,
        Dictionary<int, int> frets, Dictionary<int, int> fingers, string? name = null, bool fingerless = false,
        int ghostMask = 0)
    {
        // Fingerless is in the key: EOF keeps a fingerless chord and an unfingered
        // one on the same frets apart too (rs.c:231, eof_build_chord_list).
        var key = string.Join("|", frets.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value}"))
                  + "/"
                  + string.Join("|", fingers.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value}"))
                  + (fingerless ? "/F" : "")
                  // Ghost gems are in the key: a played chord and a handshape grip
                  // holding the same frets are different templates.
                  + (ghostMask != 0 ? $"/G{ghostMask}" : "");
        if (cache.TryGetValue(key, out var existing))
        {
            if (string.IsNullOrWhiteSpace(existing.Name) && !string.IsNullOrWhiteSpace(name))
            {
                existing.Name = name.Trim();
                RsChordDisplaySuffix.FoldNop(existing);
            }

            return existing;
        }

        var template = new ChordTemplate
        {
            Id = arrangement.ChordTemplates.Count,
            Name = string.IsNullOrWhiteSpace(name) ? "" : name.Trim(),
            Frets = new Dictionary<int, int>(frets),
            Fingers = new Dictionary<int, int>(fingers),
            Fingerless = fingerless,
            GhostMask = ghostMask
        };
        // A "-nop" on the EOF name is Rocksmith's Nop flag, not name text.
        RsChordDisplaySuffix.FoldNop(template);
        arrangement.ChordTemplates.Add(template);
        cache[key] = template;
        return template;
    }

    /// <summary>
    ///     Decodes an EOF pro guitar fret byte. Per <c>song.h</c>: the MSB (0x80)
    ///     marks the string as muted, the lower 7 bits store the fret. The sentinel
    ///     value 0xFF ("muted, no fret specified") is treated as fret 0 + muted.
    /// </summary>
    /// <summary>
    ///     EOF's Fingerless eflag (song.h:137): the chord intentionally has no
    ///     fingering. The finger values EOF may still store under it are DROPPED —
    ///     EOF never exports them (rs.c:910-914, rs.c:5958), and
    ///     <see cref="ChordTemplate.Fingerless" /> keeps an empty map.
    /// </summary>
    private static bool IsFingerless(EofRawProGuitarNote raw) => (raw.ExtendedFlags & EofConstants.PgEFlagFingerless) != 0;

    private static (int fret, bool muted) DecodeFret(byte b)
    {
        if (b == EofConstants.FretMutedNoFret) return (0, true);
        var muted = (b & EofConstants.FretMutedBit) != 0;
        var fret = b & EofConstants.FretValueMask;
        return (fret, muted);
    }

    /// <summary>
    ///     Decodes an EOF bend strength byte into half-step (semitone) units.
    ///     Per <c>song.c</c> (eof_pro_guitar_track_get_active_bendstrength_at_pos):
    ///     if the MSB is set the lower 7 bits are quarter-steps; otherwise the byte
    ///     value is whole half-steps.
    /// </summary>
    private static double DecodeBendStrengthSemitones(byte bendStrength) => (bendStrength & 0x80) != 0 ? (bendStrength & 0x7F) / 2.0 : bendStrength;

    private static void AddBend(ObservableCollection<BendValue> bends,
        FractionalPosition start,
        FractionalPosition sustain, byte bendStrength)
    {
        var semitones = DecodeBendStrengthSemitones(bendStrength);
        bends.Add(new BendValue(start, 0));
        bends.Add(new BendValue(start + sustain, semitones));
    }

    private static MuteType MapMute(uint flags, bool stringMuted = false)
    {
        if (stringMuted) return MuteType.Full;
        if ((flags & EofConstants.PgFlagStringMute) != 0) return MuteType.Full;
        if ((flags & EofConstants.PgFlagPalmMute) != 0) return MuteType.Palm;
        return MuteType.None;
    }

    private static HopoType MapHopo(uint flags)
    {
        if ((flags & EofConstants.PgFlagTap) != 0) return HopoType.Tap;
        if ((flags & EofConstants.PgFlagHo) != 0) return HopoType.HammerOn;
        if ((flags & EofConstants.PgFlagPo) != 0) return HopoType.PullOff;
        // Auto-determined HOPO has no direction information; treat as hammer-on so
        // the note still renders with HOPO styling instead of as a forced strum.
        if ((flags & EofConstants.NoteFlagAutoHopo) != 0
            || (flags & EofConstants.NoteFlagForceHopo) != 0)
        {
            return HopoType.HammerOn;
        }

        return HopoType.None;
    }

    private static HarmonicType MapHarmonic(uint flags)
    {
        if ((flags & EofConstants.PgFlagPHarmonic) != 0) return HarmonicType.Pinch;
        if ((flags & EofConstants.PgFlagHarmonic) != 0) return HarmonicType.Normal;
        return HarmonicType.None;
    }

    private static BassPickingType MapBassPicking(uint flags)
    {
        if ((flags & EofConstants.PgFlagSlap) != 0) return BassPickingType.Slap;
        if ((flags & EofConstants.PgFlagPop) != 0) return BassPickingType.Pop;
        return BassPickingType.None;
    }

    private static int PopCount(byte b)
    {
        var count = 0;
        for (var i = 0; i < 8; i++) if ((b & (1 << i)) != 0) count++;
        return count;
    }

    private static int FirstSetBit(byte b)
    {
        for (var i = 0; i < 8; i++) if ((b & (1 << i)) != 0) return i;
        return 0;
    }
}
