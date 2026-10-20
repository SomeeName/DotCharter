using System.Globalization;
using System.Numerics;
using DotCharter.Services.Export;
using Arrangement = DotCharter.Core.Domain.Project.Arrangement;

namespace DotCharter.Services.Parsing.Eof;

/// <summary>An EOF-ready pro guitar note plus its per-string finger numbers (DotCharter values, 0 = thumb).</summary>
internal sealed class EofExportNote
{
    public required EofRawProGuitarNote Raw { get; init; }
    public Dictionary<int, int> Fingers { get; } = [];

    /// <summary>Bend-curve points destined for the track's tech-note custom
    /// block (ID 7) — one 1 ms BEND|RS_NOTATION note per authored bend value.</summary>
    public List<EofRawProGuitarNote> BendTechNotes { get; } = [];
}

/// <summary>
///     Converts a DotCharter <see cref="Arrangement"/>'s levels into flat EOF
///     pro guitar notes — the write-side inverse of
///     <see cref="EofProGuitarTrackParser"/>'s note building. Frets are stored
///     capo-relative (the importer re-adds the capo), technique flags follow
///     the EOF_PRO_GUITAR_NOTE_FLAG_* mapping, chord techniques are folded to
///     the note level (EOF stores one flag set per note), and slides encode the
///     end fret relative to the chord's lowest fretted string. Anything that
///     cannot survive that representation lands on the
///     <see cref="EofConversionReport"/>.
/// </summary>
internal static class EofNoteBuilder
{
    public static List<EofExportNote> Build(Arrangement arrangement, IList<BeatMarker> beats,
        EofConversionReport? report = null)
    {
        PlayOnlyExportGuard.ThrowIfPlayOnly(arrangement, "Editor on Fire export");
        var capo = arrangement.Capo;
        var templates = new Dictionary<int, ChordTemplate>();
        foreach (var template in arrangement.ChordTemplates)
            templates.TryAdd(template.Id, template);

        var notes = new List<(double SortMs, int Diff, EofExportNote Note)>();
        foreach (var level in arrangement.Levels)
        {
            var diff = (byte)Math.Clamp(level.DifficultyIndex, 0, 254);
            foreach (var note in level.Notes)
            {
                var built = BuildSingleNote(note, diff, capo, beats, arrangement.Name, report);
                if (built == null) continue;

                var mainMs = ToMs(note.Position, beats);
                if (note.GraceFret is { } graceFret &&
                    TryBuildGraceNote(note, built, graceFret, capo, beats, arrangement.Name, report) is
                    { } grace)
                {
                    // Grace ON the beat, main displaced after it (EOF's own
                    // GP-import shape) — keep the sort keys in that order.
                    notes.Add((mainMs, diff, grace));
                    notes.Add((mainMs + grace.Raw.LengthMs, diff, built));
                }
                else
                {
                    notes.Add((mainMs, diff, built));
                }
            }

            foreach (var chord in level.Chords)
            {
                var built = BuildChord(chord, diff, capo, beats, templates, arrangement.Name, report);
                if (built == null) continue;

                var chordMs = ToMs(chord.Position, beats);
                if (TryBuildChordGraceNote(chord, built, capo, beats, arrangement.Name, report) is { } chordGrace)
                {
                    // Same shape as the single-note grace: the ornament sits ON
                    // the beat and the strum is displaced after it.
                    notes.Add((chordMs, diff, chordGrace));
                    notes.Add((chordMs + chordGrace.Raw.LengthMs, diff, built));
                }
                else
                {
                    notes.Add((chordMs, diff, built));
                }
            }

            AddGhostGems(level, diff, notes, templates, capo, beats, arrangement.Name, report);
        }

        // EOF keeps notes sorted by position (then difficulty).
        return notes.OrderBy(n => n.SortMs).ThenBy(n => n.Diff).Select(n => n.Note).ToList();
    }

    private static EofExportNote? BuildSingleNote(GuitarNote note, byte diff, int capo, IList<BeatMarker> beats,
        string subject, EofConversionReport? report)
    {
        if (note.StringIndex is < 0 or > 7)
        {
            report?.Add(EofConversionKind.Dropped, EofLoss.NoteStringOutOfRange, subject,
                $"Note on string {note.StringIndex} is outside EOF's 8-string range.",
                ToMs(note.Position, beats), StringDetail(note.StringIndex));
            return null;
        }

        var raw = new EofRawProGuitarNote
        {
            Difficulty = diff,
            NoteBitmask = (byte)(1 << note.StringIndex)
        };
        SetTimes(raw, note.Position, note.EndPosition, beats);
        raw.Frets[note.StringIndex] = EncodeFret(note.Fret, capo, raw.PositionMs, subject, report);

        var flags = BuildTechniqueFlags(note.Mute, note.Hopo, note.Harmonic, note.BassPicking,
            note.LinkNext, note.Vibrato, note.Accent);
        ApplySlide(raw, ref flags, note.UnpitchedSlide, note.SlideTo,
            currentFret: note.Fret, lowestFrettedFret: note.Fret > 0 ? note.Fret : null, capo, subject, report);
        var bendTech = ApplyBend(raw, ref flags, note.BendValues, note.Position, note.EndPosition, beats,
            subject, report);
        if (note.PassOtherNotes) flags |= EofConstants.NoteFlagCrazy;
        raw.Flags = flags;

        if (note.Ignore) raw.ExtendedFlags |= EofConstants.PgEFlagIgnore;

        ReportCommonLoss(note.Vibrato, note.VibratoStrength, raw.PositionMs, subject, report);
        var exportNote = new EofExportNote { Raw = raw };
        if (bendTech != null) exportNote.BendTechNotes.AddRange(bendTech);
        return exportNote;
    }

    /// <summary>
    ///     Materializes a <see cref="GuitarNote.GraceFret"/> hint the way EOF's
    ///     own GP import writes graces: a short grace note ON the beat
    ///     (LINKNEXT + a pitched RS_NOTATION slide into the target fret) with
    ///     the main note displaced after it. The hint model stores no duration,
    ///     so the grace length is synthesized as a 32nd at the local tempo —
    ///     the same magnitude EOF emits (34–68 ms). The importer's
    ///     <c>FoldGraceNotePairs</c> is the exact inverse.
    /// </summary>
    private static EofExportNote? TryBuildGraceNote(GuitarNote note, EofExportNote main, int graceFret,
        int capo, IList<BeatMarker> beats, string subject, EofConversionReport? report)
    {
        if (graceFret <= 0 || note.Fret <= 0 || graceFret == note.Fret)
        {
            report?.Add(EofConversionKind.Dropped, EofLoss.GraceNote, subject,
                $"Grace ornament at {main.Raw.PositionMs} ms needs distinct fretted grace/target frets to survive as an EOF slide-in; the hint was dropped.",
                main.Raw.PositionMs);
            return null;
        }

        var mainMs = ToMs(note.Position, beats);
        var thirtySecondMs = mainMs - ToMs(note.Position - new FractionalPosition(0, new Fraction(1, 8)), beats);
        var graceLen = (uint)Math.Clamp(Math.Round(thirtySecondMs), 1, 200);

        var raw = new EofRawProGuitarNote
        {
            Difficulty = main.Raw.Difficulty,
            NoteBitmask = (byte)(1 << note.StringIndex),
            PositionMs = main.Raw.PositionMs,
            LengthMs = graceLen
        };
        raw.Frets[note.StringIndex] = EncodeFret(graceFret, capo, raw.PositionMs, subject, report);
        raw.Flags = EofConstants.PgFlagLinkNext | EofConstants.PgFlagRsNotation |
                    (graceFret < note.Fret ? EofConstants.PgFlagSlideUp : EofConstants.PgFlagSlideDown);
        raw.SlideEndFret = EncodeFret(note.Fret, capo, raw.PositionMs, subject, report);

        main.Raw.PositionMs += graceLen;
        main.Raw.LengthMs = main.Raw.LengthMs > graceLen ? main.Raw.LengthMs - graceLen : 0;

        return new EofExportNote { Raw = raw };
    }

    /// <summary>
    ///     Materializes a CHORD's <see cref="ChordNote.GraceFret"/> hints the
    ///     same way <see cref="TryBuildGraceNote"/> does for a single note: one
    ///     short EOF note ON the beat carrying every graced string, LINKNEXT and
    ///     a pitched RS_NOTATION slide into the strum, with the strum displaced
    ///     after it. <see cref="EofProGuitarTrackParser"/>'s <c>FoldGraceNotePairs</c>
    ///     is the exact inverse.
    ///     <para>EOF stores ONE slide end fret per note — the end fret of the
    ///     chord's LOWEST fretted string — and the importer projects that one
    ///     distance onto every string. A chord grace is therefore only
    ///     representable when every graced string travels the SAME (non-zero)
    ///     fretted interval; anything else lands on the report and keeps the
    ///     ornament out of the file rather than writing a wrong shape.</para>
    /// </summary>
    private static EofExportNote? TryBuildChordGraceNote(Chord chord, EofExportNote main, int capo,
        IList<BeatMarker> beats, string subject, EofConversionReport? report)
    {
        var hinted = chord.ChordNotes
            .Where(cn => cn.GraceFret.HasValue && cn.StringIndex is >= 0 and <= 7)
            .ToList();
        if (hinted.Count == 0) return null;

        // A slide needs a fretted origin AND a fretted landing on every string.
        var graced = hinted.Where(cn => cn.GraceFret is > 0 && cn.Fret > 0).ToList();
        var distance = graced.Count > 0 ? graced[0].Fret - graced[0].GraceFret!.Value : 0;
        if (graced.Count < hinted.Count || distance == 0 ||
            graced.Any(cn => cn.Fret - cn.GraceFret!.Value != distance))
        {
            report?.Add(EofConversionKind.Dropped, EofLoss.ChordGraceNote, subject,
                $"Chord grace ornament at {main.Raw.PositionMs} ms needs one uniform, non-zero fretted interval on every graced string to survive as an EOF slide-in; the hints were dropped.",
                main.Raw.PositionMs);
            return null;
        }

        var chordMs = ToMs(chord.Position, beats);
        var thirtySecondMs = chordMs - ToMs(chord.Position - new FractionalPosition(0, new Fraction(1, 8)), beats);
        var graceLen = (uint)Math.Clamp(Math.Round(thirtySecondMs), 1, 200);

        var raw = new EofRawProGuitarNote
        {
            Difficulty = main.Raw.Difficulty,
            PositionMs = main.Raw.PositionMs,
            LengthMs = graceLen
        };

        var lowestGraceFret = int.MaxValue;
        foreach (var cn in graced)
        {
            raw.NoteBitmask |= (byte)(1 << cn.StringIndex);
            raw.Frets[cn.StringIndex] = EncodeFret(cn.GraceFret!.Value, capo, raw.PositionMs, subject, report);
            if (cn.GraceFret.Value < lowestGraceFret) lowestGraceFret = cn.GraceFret.Value;
        }

        raw.Flags = EofConstants.PgFlagLinkNext | EofConstants.PgFlagRsNotation |
                    (distance > 0 ? EofConstants.PgFlagSlideUp : EofConstants.PgFlagSlideDown);
        raw.SlideEndFret = EncodeFret(lowestGraceFret + distance, capo, raw.PositionMs, subject, report);

        main.Raw.PositionMs += graceLen;
        main.Raw.LengthMs = main.Raw.LengthMs > graceLen ? main.Raw.LengthMs - graceLen : 0;

        return new EofExportNote { Raw = raw };
    }

    /// <summary>The name EOF stores on a chord note: the template's name with
    /// <see cref="ChordTemplate.NopInRs" /> spelled back as the "-nop" suffix.</summary>
    private static string EofChordName(ChordTemplate template)
    {
        var (name, nop) = RsChordDisplaySuffix.ForExport(template);
        return nop ? name + RsChordDisplaySuffix.Nop : name;
    }

    private static EofExportNote? BuildChord(Chord chord, byte diff, int capo, IList<BeatMarker> beats,
        IReadOnlyDictionary<int, ChordTemplate> templates, string subject, EofConversionReport? report)
    {
        var chordNotes = chord.ChordNotes.Where(cn => cn.StringIndex is >= 0 and <= 7).ToList();
        if (chordNotes.Count < chord.ChordNotes.Count)
        {
            report?.Add(EofConversionKind.Dropped, EofLoss.ChordStringOutOfRange, subject,
                "Chord notes outside EOF's 8-string range were dropped.", ToMs(chord.Position, beats),
                string.Join(", ", chord.ChordNotes.Where(cn => cn.StringIndex is < 0 or > 7).Select(cn => StringDetail(cn.StringIndex))));
        }

        if (chordNotes.Count == 0) return null;

        templates.TryGetValue(chord.TemplateId, out var template);
        var raw = new EofRawProGuitarNote
        {
            // EOF has no Nop field: it keeps the flag as the "-nop" name text its
            // own RS export reads, and our import folds it back.
            Name = template == null ? "" : EofChordName(template),
            Difficulty = diff
        };
        SetTimes(raw, chord.Position, chord.Position + chord.GetMaxSustain(), beats);

        foreach (var cn in chordNotes)
        {
            raw.NoteBitmask |= (byte)(1 << cn.StringIndex);
            raw.Frets[cn.StringIndex] = EncodeFret(cn.Fret, capo, raw.PositionMs, subject, report);
        }

        // EOF stores ONE technique flag set per note; fold the chord notes'
        // techniques note-level like EOF (which ORs any string's status) —
        // NOT from chordNotes[0], or a technique on a later string vanishes.
        // Per-string FULL mutes survive via the fret byte's mute MSB when mixed.
        var first = chordNotes[0];
        ReportChordUniformityLoss(chordNotes, raw.PositionMs, subject, report);
        var uniformMute = chordNotes.All(cn => cn.Mute == first.Mute);
        var flags = BuildTechniqueFlags(uniformMute ? first.Mute : MuteType.None,
            chordNotes.Select(cn => cn.Hopo).FirstOrDefault(h => h != HopoType.None),
            chordNotes.Select(cn => cn.Harmonic).FirstOrDefault(h => h != HarmonicType.None),
            chordNotes.Select(cn => cn.BassPicking).FirstOrDefault(b => b != BassPickingType.None),
            chordNotes.Any(cn => cn.LinkNext), chordNotes.Any(cn => cn.Vibrato),
            chord.Accent || chordNotes.Any(cn => cn.Accent));
        if (!uniformMute)
        {
            foreach (var cn in chordNotes.Where(cn => cn.Mute == MuteType.Full))
                raw.Frets[cn.StringIndex] |= EofConstants.FretMutedBit;
        }

        // Slides: EOF stores the end fret of the chord's lowest fretted string;
        // the importer projects the slide distance onto every fretted string.
        var lowestFretted = chordNotes.Where(cn => cn.Fret > 0).Select(cn => (int?)cn.Fret).DefaultIfEmpty(null).Min();
        var slideNote = chordNotes.FirstOrDefault(cn => cn.Fret > 0 && cn.SlideTo.HasValue);
        var anyUnpitched = chordNotes.Any(cn => cn.UnpitchedSlide);
        ApplySlide(raw, ref flags, anyUnpitched, slideNote?.SlideTo,
            currentFret: slideNote?.Fret ?? 0, lowestFrettedFret: lowestFretted, capo, subject, report);

        var bendTech = ApplyBend(raw, ref flags,
            chordNotes.Select(cn => cn.BendValues).FirstOrDefault(b => b.Count > 0),
            chord.Position, chord.Position + chord.GetMaxSustain(), beats, subject, report);
        // Strings whose slide distance differs from the chord's own ride per-string
        // tech notes (custom block 7), which the importer lets override the chord's.
        var slideTech = BuildMixedSlideTechNotes(raw, chordNotes, slideNote, bendTech, capo, subject, report);
        // "Split notes" is EOF's Split status (song.h:64), and EOF exports it the
        // same way our writers do: as single notes (rs.c:2082-2124).
        if (chord.SplitIntoNotes) flags |= EofConstants.PgFlagSplit;
        if (chord.PassOtherNotes) flags |= EofConstants.NoteFlagCrazy;
        raw.Flags = flags;

        // A deliberately fingerless grip is EOF's Fingerless eflag (song.h:137).
        // EOF keeps it per NOTE, so every chord on the template carries it.
        if (template is { Fingerless: true }) raw.ExtendedFlags |= EofConstants.PgEFlagFingerless;

        // EOF stores ONE note per chord, so the chord's own ignore flag has a home
        // (eflags bit 0) but a per-STRING ignore does not — that one is reported.
        if (chord.Ignore || chordNotes.All(cn => cn.Ignore))
        {
            raw.ExtendedFlags |= EofConstants.PgEFlagIgnore;
        }
        else if (chordNotes.Any(cn => cn.Ignore))
        {
            report?.Add(EofConversionKind.Approximated, EofLoss.ChordPartialIgnore, subject,
                $"Chord at {raw.PositionMs} ms ignores only some strings; EOF stores one ignore flag per chord.",
                raw.PositionMs);
        }

        ReportCommonLoss(first.Vibrato, first.VibratoStrength, raw.PositionMs, subject, report);
        var note = new EofExportNote { Raw = raw };
        if (bendTech != null) note.BendTechNotes.AddRange(bendTech);
        note.BendTechNotes.AddRange(slideTech);
        if (template != null)
        {
            foreach (var (stringIndex, finger) in template.Fingers)
            {
                if (stringIndex >= 0 && stringIndex <= 7 && (raw.NoteBitmask & (1 << stringIndex)) != 0)
                    note.Fingers[stringIndex] = finger;
            }
        }

        return note;
    }

    /// <summary>
    ///     A chord whose strings slide by DIFFERENT distances: the note-level slide end fret
    ///     carries the first sliding string's distance (the importer projects it onto every
    ///     string), and each string with another distance gets a 1 ms SLIDE|RS_NOTATION tech
    ///     note at the chord's onset whose end fret is that string's own target - exactly what
    ///     <see cref="EofProGuitarTrackParser.ResolveEffectiveSlide"/> reads back. A uniform
    ///     chord slide writes nothing. A string the tech note cannot carry (unpitched, out of
    ///     range, or shadowed by a later bend tech note on the same string) stays
    ///     <see cref="EofLoss.ChordMixedSlide"/>.
    /// </summary>
    private static List<EofRawProGuitarNote> BuildMixedSlideTechNotes(EofRawProGuitarNote raw,
        IReadOnlyList<ChordNote> chordNotes, ChordNote? slideNote, List<EofRawProGuitarNote>? bendTech,
        int capo, string subject, EofConversionReport? report)
    {
        var result = new List<EofRawProGuitarNote>();
        if (slideNote == null) return result;
        var distance = slideNote.SlideTo!.Value - slideNote.Fret;
        var unpitched = chordNotes.Any(cn => cn.UnpitchedSlide);
        foreach (var cn in chordNotes)
        {
            if (cn.Fret <= 0 || !cn.SlideTo.HasValue || cn.SlideTo.Value - cn.Fret == distance) continue;
            var target = cn.SlideTo.Value - capo;
            var bit = (byte)(1 << cn.StringIndex);
            var carriable = !unpitched && cn.SlideTo.Value != cn.Fret
                            && target is > 0 and <= EofConstants.FretValueMask
                            && (bendTech == null || bendTech.All(t => (t.NoteBitmask & bit) == 0));
            if (!carriable)
            {
                report?.Add(EofConversionKind.Approximated, EofLoss.ChordMixedSlide, subject,
                    $"Chord at {raw.PositionMs} ms mixes per-string slide distances; EOF stores one value per chord.",
                    raw.PositionMs);
                continue;
            }

            var tech = new EofRawProGuitarNote
            {
                Difficulty = raw.Difficulty,
                NoteBitmask = bit,
                PositionMs = raw.PositionMs,
                LengthMs = 1,
                Flags = EofConstants.PgFlagRsNotation
                        | (cn.SlideTo.Value > cn.Fret ? EofConstants.PgFlagSlideUp : EofConstants.PgFlagSlideDown),
                SlideEndFret = (byte)target
            };
            tech.Frets[cn.StringIndex] = raw.Frets[cn.StringIndex];
            result.Add(tech);
        }

        return result;
    }

    /// <summary>Chord techniques that EOF folds to note level: report any per-string variation.</summary>
    private static void ReportChordUniformityLoss(IReadOnlyList<ChordNote> chordNotes, uint posMs,
        string subject, EofConversionReport? report)
    {
        if (report == null || chordNotes.Count <= 1) return;
        var first = chordNotes[0];

        void Check(bool uniform, EofLoss loss, string what)
        {
            if (!uniform)
            {
                report.Add(EofConversionKind.Approximated, loss, subject,
                    $"Chord at {posMs} ms mixes per-string {what}; EOF stores one value per chord.", posMs);
            }
        }

        Check(chordNotes.All(cn => cn.Hopo == first.Hopo), EofLoss.ChordMixedHopo, "HOPO status");
        Check(chordNotes.All(cn => cn.Harmonic == first.Harmonic), EofLoss.ChordMixedHarmonic, "harmonic status");
        Check(chordNotes.All(cn => cn.BassPicking == first.BassPicking), EofLoss.ChordMixedSlapPop, "slap/pop status");
        Check(chordNotes.All(cn => cn.Vibrato == first.Vibrato), EofLoss.ChordMixedVibrato, "vibrato status");
        Check(chordNotes.All(cn => cn.LinkNext == first.LinkNext), EofLoss.ChordMixedLinkNext, "link-next status");
        Check(chordNotes.All(cn => cn.Sustain == first.Sustain), EofLoss.ChordMixedSustain, "sustain lengths");
        Check(chordNotes.All(cn => cn.Mute == first.Mute || cn.Mute != MuteType.Palm), EofLoss.ChordMixedPalmMute, "palm mutes");

        // Bends: EOF applies one bend to every chord string, so every string
        // must carry the same curve (or none at all).
        Check(chordNotes.Select(cn => cn.BendValues.Count).Distinct().Count() == 1, EofLoss.ChordMixedBend, "bend curves");
    }

    private static void ReportCommonLoss(bool vibrato, int vibratoStrength, uint posMs,
        string subject, EofConversionReport? report)
    {
        if (report == null) return;
        if (vibrato && vibratoStrength != 80)
        {
            report.Add(EofConversionKind.Approximated, EofLoss.VibratoStrength, subject,
                $"Vibrato strength {vibratoStrength} at {posMs} ms is not stored by EOF (re-imports as 80).",
                posMs, vibratoStrength.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// A hand shape's GHOST gems go where EOF keeps them: as ghost-flagged gems
    ///     (<c>song.h:200</c>, bit n = string n) on the note that carries the shape — the one
    ///     EOF's RS export reads the handshape's chord from (<c>rs.c:2302-2316</c>: the FIRST note in
    ///     the phrase with two or more gems, ghosts counted) and the importer reads back from the
    ///     full bitmask (<c>EofProGuitarTrackParser.ApplyArpeggioHandShapes</c>). See
    /// <see cref="GhostCarrier" /> for which note that is.
    ///     <para>
    ///         A ghost on a string the carrier itself plays has NO EOF spelling: one EOF note holds
    ///         one fret per string, two notes at one timestamp are merged with the played gem
    ///         winning (<c>song.c:6905-6960</c>), and a disjointed pair sharing a string is merged
    ///         too (<c>song.h:141</c>). That is the tapped harmonic over its own held fret when
    ///         nothing else under the bar can carry it, and a bar over no sound at all. Those
    ///         ghosts are reported as <see cref="EofLoss.HandShapeGhostGem" />, never invented
    ///         onto another time.
    ///     </para>
    /// </summary>
    private static void AddGhostGems(ChartLevel level, byte diff, List<(double SortMs, int Diff, EofExportNote Note)> notes,
        Dictionary<int, ChordTemplate> templates, int capo, IList<BeatMarker> beats, string subject,
        EofConversionReport? report)
    {
        foreach (var shape in level.HandShapes)
        {
            if (!templates.TryGetValue(shape.TemplateId, out var grip) || grip.GhostMask == 0) continue;
            var startMs = EofExportSession.ToMsU32(ToMs(shape.Position, beats));
            var endMs = EofExportSession.ToMsU32(ToMs(shape.EndPosition, beats));
            var carrier = GhostCarrier(notes, diff, startMs, endMs, grip, capo);
            var lost = new List<int>();
            for (var s = 0; s <= 7; s++)
            {
                if (!grip.IsGhost(s)) continue;
                var bit = (byte)(1 << s);
                var raw = carrier?.Raw;
                if (raw == null || ((raw.NoteBitmask & ~raw.GhostBitmask) & bit) != 0)
                {
                    lost.Add(s);
                    continue;
                }

                raw.NoteBitmask |= bit;
                raw.GhostBitmask |= bit;
                raw.Frets[s] = EncodeFret(grip.Frets[s], capo);
                if (grip.Fingers.TryGetValue(s, out var finger)) carrier!.Fingers[s] = finger;
            }

            if (lost.Count == 0 || report == null) continue;
            var strings = string.Join(",", lost.Select(s => s.ToString(CultureInfo.InvariantCulture)));
            report.Add(EofConversionKind.Dropped, EofLoss.HandShapeGhostGem, subject,
                carrier == null
                    ? $"Hand shape at {startMs} ms holds ghost gems on string(s) {strings} but covers no sound to carry them — EOF stores a ghost only on a note."
                    : $"Hand shape at {startMs} ms holds a ghost gem on string(s) {strings} that its carrying note plays — an EOF note holds one fret per string.",
                startMs, strings);
        }
    }

    /// <summary>
    ///     The note under <paramref name="grip" />'s bar that takes its ghost gems: the first sound in
    ///     the bar's window (<c>[start, end)</c>, the window itself when it has no length) that
    ///     plays none of the ghost strings and whose played frets are the grip's own, so the importer
    ///     reads back exactly this grip. The search stops at a chord that does not qualify, because
    ///     EOF's RS export takes the FIRST multi-gem note in the phrase as the handshape's chord
    ///     (<c>rs.c:2312-2316</c>); before such a chord only single notes are skipped — e.g. the tapped
    ///     harmonic that opens a bar whose chord follows. With no such note the first sound carries
    ///     what it can; <see langword="null" /> when the bar covers no sound.
    /// </summary>
    private static EofExportNote? GhostCarrier(List<(double SortMs, int Diff, EofExportNote Note)> notes, byte diff,
        uint startMs, uint endMs, ChordTemplate grip, int capo)
    {
        var inside = notes
            .Where(n => n.Diff == diff && n.Note.Raw.PositionMs >= startMs &&
                        (n.Note.Raw.PositionMs < endMs || n.Note.Raw.PositionMs == startMs))
            .OrderBy(n => n.SortMs)
            .Select(n => n.Note)
            .ToList();
        if (inside.Count == 0) return null;

        var ghostBits = (byte)(grip.GhostMask & 0xFF);
        foreach (var note in inside)
        {
            var raw = note.Raw;
            var live = (byte)(raw.NoteBitmask & ~raw.GhostBitmask);
            if ((live & ghostBits) == 0 && PlaysOnlyTheGrip(raw, live, grip, capo)) return note;
            if (BitOperations.PopCount(raw.NoteBitmask) > 1) break;
        }

        return inside[0];
    }

    private static bool PlaysOnlyTheGrip(EofRawProGuitarNote raw, byte live, ChordTemplate grip, int capo)
    {
        for (var s = 0; s <= 7; s++)
        {
            if ((live & (1 << s)) == 0) continue;
            if (!grip.Frets.TryGetValue(s, out var fret) || grip.IsGhost(s)) return false;
            if ((raw.Frets[s] & EofConstants.FretValueMask) != EncodeFret(fret, capo)) return false;
        }

        return true;
    }

    private static void SetTimes(EofRawProGuitarNote raw, FractionalPosition start, FractionalPosition end,
        IList<BeatMarker> beats)
    {
        var startU32 = EofExportSession.ToMsU32(ToMs(start, beats));
        var endU32 = EofExportSession.ToMsU32(ToMs(end, beats));
        raw.PositionMs = startU32;
        raw.LengthMs = endU32 > startU32 ? endU32 - startU32 : 0;
    }

    /// <summary>Frets are stored relative to the un-capoed fretboard; the importer re-adds the capo.</summary>
    internal static byte EncodeFret(int fret, int capo, uint posMs = 0, string? subject = null,
        EofConversionReport? report = null)
    {
        if (fret <= 0) return 0;
        var encoded = fret - capo;
        if (encoded is < 1 or > EofConstants.FretValueMask)
        {
            report?.Add(EofConversionKind.Approximated, EofLoss.FretClamped, subject ?? "",
                $"Fret {fret} at {posMs} ms cannot be stored capo-relative (capo {capo}); clamped.",
                posMs, string.Create(CultureInfo.InvariantCulture, $"{fret} (capo {capo})"));
            encoded = Math.Clamp(encoded, 1, EofConstants.FretValueMask);
        }

        return (byte)encoded;
    }

    internal static uint BuildTechniqueFlags(MuteType mute, HopoType hopo, HarmonicType harmonic,
        BassPickingType bassPicking, bool linkNext, bool vibrato, bool accent)
    {
        uint flags = 0;
        flags |= mute switch
        {
            MuteType.Full => EofConstants.PgFlagStringMute,
            MuteType.Palm => EofConstants.PgFlagPalmMute,
            _ => 0u
        };
        flags |= hopo switch
        {
            HopoType.HammerOn => EofConstants.PgFlagHo,
            HopoType.PullOff => EofConstants.PgFlagPo,
            HopoType.Tap => EofConstants.PgFlagTap,
            _ => 0u
        };
        flags |= harmonic switch
        {
            HarmonicType.Normal => EofConstants.PgFlagHarmonic,
            HarmonicType.Pinch => EofConstants.PgFlagPHarmonic,
            _ => 0u
        };
        flags |= bassPicking switch
        {
            BassPickingType.Pop => EofConstants.PgFlagPop,
            BassPickingType.Slap => EofConstants.PgFlagSlap,
            _ => 0u
        };
        if (linkNext) flags |= EofConstants.PgFlagLinkNext;
        if (vibrato) flags |= EofConstants.PgFlagVibrato;
        if (accent) flags |= EofConstants.PgFlagAccent;
        return flags;
    }

    /// <summary>
    ///     Encodes a slide onto the raw note. Pitched slides need the
    ///     RS_NOTATION flag for the end fret to be written at all (the
    ///     conditional-field trap); the end fret is stored capo-relative for
    ///     the LOWEST fretted string — the importer rebuilds each string's
    ///     target from the distance. Open-string slides cannot be represented.
    /// </summary>
    private static void ApplySlide(EofRawProGuitarNote raw, ref uint flags, bool unpitched, int? slideTo,
        int currentFret, int? lowestFrettedFret, int capo, string subject, EofConversionReport? report)
    {
        if (unpitched)
        {
            flags |= EofConstants.PgFlagUnpitchedSlide;
            if (slideTo is > 0 && currentFret > 0 && lowestFrettedFret is > 0)
            {
                var endFret = lowestFrettedFret.Value + (slideTo.Value - currentFret) - capo;
                if (endFret > 0 && endFret <= EofConstants.FretValueMask && endFret != lowestFrettedFret - capo)
                {
                    raw.UnpitchedSlideEndFret = (byte)endFret;
                }
                else if (slideTo.Value != currentFret)
                {
                    report?.Add(EofConversionKind.Approximated, EofLoss.SlideTargetOutOfRange, subject,
                        $"Unpitched slide target at {raw.PositionMs} ms is out of EOF's range; the slide stays targetless.",
                        raw.PositionMs);
                }
            }

            return;
        }

        if (slideTo is null) return;
        if (slideTo is not > 0 || currentFret <= 0 || lowestFrettedFret is not > 0)
        {
            report?.Add(EofConversionKind.Dropped, EofLoss.SlideFromOpenString, subject,
                $"Slide at {raw.PositionMs} ms starts on an open/muted string — EOF slides need a fretted origin.",
                raw.PositionMs);
            return;
        }

        var distance = slideTo.Value - currentFret;
        if (distance == 0)
        {
            report?.Add(EofConversionKind.Dropped, EofLoss.SlideToSameFret, subject,
                $"Slide at {raw.PositionMs} ms targets its own fret and cannot be represented.", raw.PositionMs);
            return;
        }

        var slideEnd = lowestFrettedFret.Value + distance - capo;
        if (slideEnd is <= 0 or > EofConstants.FretValueMask)
        {
            report?.Add(EofConversionKind.Dropped, EofLoss.SlideOutOfRange, subject,
                $"Slide at {raw.PositionMs} ms ends outside EOF's fret range.", raw.PositionMs);
            return;
        }

        flags |= distance > 0 ? EofConstants.PgFlagSlideUp : EofConstants.PgFlagSlideDown;
        flags |= EofConstants.PgFlagRsNotation;
        raw.SlideEndFret = (byte)slideEnd;
    }

    /// <summary>
    ///     Encodes a bend as EOF's note-level bend strength (half steps, or
    ///     quarter steps with the MSB set). A curve that is not the simple
    ///     two-point start→peak shape additionally emits its full point list
    ///     as tech notes for the track's custom block 7 — one 1 ms
    ///     BEND|RS_NOTATION note per bend value in the quarter-step MSB
    ///     encoding, the exact shape real EOF writes for GP bend curves — so
    ///     the curve timing survives instead of folding to the peak.
    /// </summary>
    private static List<EofRawProGuitarNote>? ApplyBend(EofRawProGuitarNote raw, ref uint flags,
        IReadOnlyList<BendValue>? bends, FractionalPosition start, FractionalPosition end,
        IList<BeatMarker> beats, string subject, EofConversionReport? report)
    {
        if (bends == null || bends.Count == 0) return null;
        var semitones = bends.Max(b => b.Step);
        var strength = EncodeBendStrength(semitones);
        if (strength == 0)
        {
            report?.Add(EofConversionKind.Dropped, EofLoss.FlatBend, subject,
                $"Bend at {raw.PositionMs} ms never rises above zero steps and was dropped.", raw.PositionMs);
            return null;
        }

        List<EofRawProGuitarNote>? techNotes = null;
        if (!IsSimpleTwoPointBend(bends, start, end, semitones))
        {
            techNotes = new List<EofRawProGuitarNote>(bends.Count);
            foreach (var b in bends)
            {
                var tech = new EofRawProGuitarNote
                {
                    Difficulty = raw.Difficulty,
                    NoteBitmask = raw.NoteBitmask,
                    PositionMs = EofExportSession.ToMsU32(ToMs(b.Position, beats)),
                    LengthMs = 1,
                    Flags = EofConstants.PgFlagBend | EofConstants.PgFlagRsNotation,
                    BendStrength = (byte)(0x80 |
                                          (int)Math.Clamp(Math.Round(b.Step * 2), 0, EofConstants.FretValueMask))
                };
                for (var s = 0; s < 8; s++)
                {
                    if ((raw.NoteBitmask & (1 << s)) != 0)
                        tech.Frets[s] = raw.Frets[s];
                }

                techNotes.Add(tech);
            }
        }
        else if (report != null && DecodedBendSemitones(strength) != semitones)
        {
            report.Add(EofConversionKind.Approximated, EofLoss.BendRounded, subject,
                $"Bend of {semitones} steps at {raw.PositionMs} ms rounded to {DecodedBendSemitones(strength)} (EOF stores quarter steps).",
                raw.PositionMs,
                string.Create(CultureInfo.InvariantCulture, $"{semitones:0.###} → {DecodedBendSemitones(strength):0.###}"));
        }

        flags |= EofConstants.PgFlagBend | EofConstants.PgFlagRsNotation;
        raw.BendStrength = strength;
        return techNotes;
    }

    /// <summary>The exact bend shape the importer synthesizes: 0 at the note start, the peak at its end.</summary>
    internal static bool IsSimpleTwoPointBend(IReadOnlyList<BendValue> bends, FractionalPosition start,
        FractionalPosition end, double peak)
    {
        return bends.Count == 2
               && bends[0].Position == start && bends[0].Step == 0
               && bends[1].Position == end && bends[1].Step.Equals(peak);
    }

    /// <summary>Whole half-steps as a plain byte; otherwise quarter-steps with the MSB set
    /// (rounded to the nearest quarter step when the value doesn't land on one).</summary>
    internal static byte EncodeBendStrength(double semitones)
    {
        if (double.IsNaN(semitones) || semitones <= 0) return 0;
        if (Math.Abs(semitones - Math.Round(semitones)) < 1e-9 && semitones <= 127)
            return (byte)Math.Round(semitones);

        var quarterSteps = (int)Math.Clamp(Math.Round(semitones * 2), 1, EofConstants.FretValueMask);
        return (byte)(0x80 | quarterSteps);
    }

    private static double DecodedBendSemitones(byte strength) => (strength & 0x80) != 0 ? (strength & 0x7F) / 2.0 : strength;

    /// <summary>A string as the report names it: 1-based, the way the editor counts strings.</summary>
    private static string StringDetail(int stringIndex) => (stringIndex + 1).ToString(CultureInfo.InvariantCulture);

    private static double ToMs(FractionalPosition position, IList<BeatMarker> beats) => TimelineMath.CalculateTimeMs(position, beats);
}
