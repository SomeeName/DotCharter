using System.Globalization;
using System.Xml.Linq;
using Chord = DotCharter.Core.Domain.Guitar.Chord;

namespace DotCharter.Services.Parsing.GuitarPro;

/// <summary>
///     What one GP beat leaves for the next ones in its voice: a pending GRACE
///     beat waiting for the struck beat it ornaments, a tie-spelled ROLL whose
///     notes keep ringing, and a STACCATO mark carried onto the note it shortens.
///     Pure helpers over per-voice state that <see cref="GpArrangementParser" />
///     owns for the length of a track walk.
/// </summary>
internal static class GpBeatCarry
{
    /// <summary>
    ///     One string's share of a grace beat. <c>Slide</c> marks a "sl." grace
    ///     that slides INTO the target (parsed onto the grace note's
    ///     SlideTo/LinkNext) rather than a plain ornament.
    /// </summary>
    internal readonly record struct GraceEntry(int StringIndex, int Fret, bool Slide);

    /// <summary>
    ///     A whole GP grace beat waiting for the next struck beat in the same
    ///     voice. GP graces a CHORD by putting one grace note per string on the
    ///     grace beat, so the ornament is a COLLECTION keyed by string — the old
    ///     one-note/one-string model could not represent that and dropped every
    ///     chord grace on the floor (the reference file's 3-note 3/5/5 → 5/7/7
    ///     shift vanished entirely, leaving only the landing chord).
    ///     <see cref="BeforeBeat"/> carries GP's
    ///     <c>&lt;GraceNotes&gt;BeforeBeat|OnBeat&lt;/GraceNotes&gt;</c> flavour,
    ///     which decides whether the ornament steals time ahead of the beat or
    ///     sits ON it and pushes the target late.
    /// </summary>
    internal sealed class PendingGrace
    {
        public PendingGrace(bool beforeBeat) => BeforeBeat = beforeBeat;
        public bool BeforeBeat { get; }
        public List<GraceEntry> Entries { get; } = [];
    }

    /// <summary>
    ///     Consumes the pending grace ornament against a SINGLE-NOTE target. A
    ///     "sl." grace on the target's string materializes into a real short note
    ///     that slides into it (what EOF and Log Charter both emit); a plain grace
    ///     stays the notation-only <see cref="GuitarNote.GraceFret"/> hint. Grace
    ///     entries on other strings have no playable target here and are dropped —
    ///     a GP grace beat attaches to the very next beat only.
    /// </summary>
    internal static void ApplyPendingGrace(GuitarNote note, ref PendingGrace? pendingGrace,
        IList<GuitarNote> notesOut, IList<Chord> chordsOut)
    {
        var grace = pendingGrace;
        pendingGrace = null;
        if (grace == null) return;

        GraceEntry? found = null;
        foreach (var candidate in grace.Entries)
        {
            if (candidate.StringIndex == note.StringIndex)
            {
                found = candidate;
                break;
            }
        }

        if (found is not { } entry) return;

        if (!entry.Slide || entry.Fret <= 0 || entry.Fret == note.Fret)
        {
            note.GraceFret = entry.Fret; // a plain ornament stays notation-only
            return;
        }

        var earliest = LastEndOnString(notesOut, chordsOut, note.StringIndex);
        var place = GpPostProcessor.PlaceGrace(note.Position, note.Sustain, GpPostProcessor.GraceLeadIn,
            grace.BeforeBeat, earliest);
        if (place.GraceLength.CompareTo(FractionalPosition.Zero) <= 0)
        {
            note.GraceFret = entry.Fret; // no room to render it — keep the hint
            return;
        }

        notesOut.Add(new GuitarNote
        {
            Position = place.GracePosition,
            Sustain = place.GraceLength,
            StringIndex = note.StringIndex,
            Fret = entry.Fret,
            SlideTo = note.Fret,
            LinkNext = true,
            NotationDuration = GpPostProcessor.GraceLeadIn
        });
        note.Position += place.TargetShift;
        note.Sustain = GpPostProcessor.ShortenSustain(note.Sustain, place.TargetShift);
    }

    /// <summary>
    ///     Consumes the pending grace ornament against a CHORD target. GP graces a
    ///     whole strum by putting one grace note per string on the grace beat, so a
    ///     "sl." chord grace materializes a real origin CHORD that slides into the
    ///     target — exactly what EOF and Log Charter emit (the reference file's
    ///     (1,3)(2,5)(3,5) → (1,5)(2,7)(3,7) shift, which must not be lost
    ///     completely). Only the graced strings take part. A plain (non-slide)
    ///     chord grace degrades to the notation-only
    ///     <see cref="ChordNote.GraceFret"/> hint on the string it belongs to,
    ///     exactly as the single-note path does — it must not be dropped outright
    ///     (<see cref="ChordNote"/> has a field for it).
    /// </summary>
    internal static void ApplyPendingGrace(Chord chord, ref PendingGrace? pendingGrace,
        IList<GuitarNote> notesOut, IList<Chord> chordsOut)
    {
        var grace = pendingGrace;
        pendingGrace = null;
        if (grace == null || chord.ChordNotes.Count == 0) return;

        var pairs = new List<(GraceEntry Entry, ChordNote Target)>();
        var hints = new List<(GraceEntry Entry, ChordNote Target)>();
        foreach (var entry in grace.Entries)
        {
            foreach (var cn in chord.ChordNotes)
            {
                if (cn.StringIndex == entry.StringIndex)
                {
                    // Same rule as the single-note path: only a "sl." grace that
                    // travels to a DIFFERENT fretted position can become a real
                    // sliding note; anything else stays a notation ornament.
                    if (entry.Slide && entry.Fret > 0 && cn.Fret != entry.Fret) pairs.Add((entry, cn));
                    else hints.Add((entry, cn));
                    break;
                }
            }
        }

        // Ornaments on the strings that cannot slide keep their hint whatever
        // happens to the sliding ones (the hint lives on the ChordNote, so the
        // materialization below never disturbs it).
        foreach (var (entry, target) in hints) target.GraceFret = entry.Fret;

        if (pairs.Count == 0) return;

        var earliest = FractionalPosition.Zero;
        foreach (var pair in pairs)
        {
            var end = LastEndOnString(notesOut, chordsOut, pair.Target.StringIndex);
            if (end.CompareTo(earliest) > 0) earliest = end;
        }

        var place = GpPostProcessor.PlaceGrace(chord.Position, chord.GetMaxSustain(),
            GpPostProcessor.GraceLeadIn, grace.BeforeBeat, earliest);
        if (place.GraceLength.CompareTo(FractionalPosition.Zero) <= 0)
        {
            // No room to render the ornament — keep it as a hint instead of
            // losing it (mirrors the single-note fallback).
            foreach (var (entry, target) in pairs) target.GraceFret = entry.Fret;
            return;
        }

        var graceChord = new Chord
        {
            Position = place.GracePosition,
            NotationDuration = GpPostProcessor.GraceLeadIn
        };
        foreach (var (entry, target) in pairs)
        {
            graceChord.ChordNotes.Add(new ChordNote
            {
                StringIndex = target.StringIndex,
                Fret = entry.Fret,
                Sustain = place.GraceLength,
                SlideTo = target.Fret,
                LinkNext = true
            });
        }

        chordsOut.Add(graceChord);
        chord.Position += place.TargetShift;
        foreach (var cn in chord.ChordNotes)
            cn.Sustain = GpPostProcessor.ShortenSustain(cn.Sustain, place.TargetShift);
    }

    /// <summary>The end of the last sound already emitted on a string — the point a
    /// BEFORE-beat grace may not reach back past.</summary>
    internal static FractionalPosition LastEndOnString(IList<GuitarNote> notesOut, IList<Chord> chordsOut,
        int stringIndex)
    {
        var end = FractionalPosition.Zero;
        foreach (var note in notesOut)
        {
            if (note.StringIndex == stringIndex && note.EndPosition.CompareTo(end) > 0)
                end = note.EndPosition;
        }

        foreach (var chord in chordsOut)
        {
            foreach (var cn in chord.ChordNotes)
            {
                if (cn.StringIndex == stringIndex)
                {
                    var chordEnd = chord.Position + cn.Sustain;
                    if (chordEnd.CompareTo(end) > 0) end = chordEnd;
                }
            }
        }

        return end;
    }

    /// <summary>
    ///     Import-only state for a Guitar Pro "rolled" chord in progress — see
    ///     <see cref="FilterTieRoll" />. One per voice slot, alive for the whole
    ///     track walk because a roll's ring-out beat can sit in the next bar.
    /// </summary>
    internal sealed class TieRollState
    {
        /// <summary>String → the note this roll emitted for it. Single notes
        /// only: a beat that emits a CHORD ends the roll, because a
        /// <c>ChordNote</c> carries no per-string notation duration to extend.</summary>
        public Dictionary<int, GuitarNote> Held { get; } = [];

        /// <summary>String → the note the PREVIOUS timed beat emitted. A roll is
        /// only recognised at its SECOND beat (the first beat that mixes a tie
        /// destination with a fresh strike), so its first beat has to be
        /// recoverable after the fact.</summary>
        public Dictionary<int, GuitarNote> Previous { get; } = [];

        public FractionalPosition PreviousPosition { get; set; }

        /// <summary>True once a mixed beat has been seen and the roll's notes are
        /// being collapsed. Only an ACTIVE roll consumes an all-destination beat;
        /// otherwise that beat is an ordinary tied chord and belongs to
        /// <see cref="GpPostProcessor.MergeTiedNotes" />.</summary>
        public bool Active { get; set; }

        public void End()
        {
            Held.Clear();
            Active = false;
        }
    }

    /// <summary>
    ///     Collapses a Guitar Pro ROLLED chord — an arpeggio spelled as growing
    ///     TIED chords — down to the notes the player actually strikes, and marks
    ///     the run so it becomes one hand shape.
    ///     <para>
    ///     GP writes a roll by repeating every already-ringing string on each
    ///     beat as a tie DESTINATION and adding one new string. Imported
    ///     literally that is a stack of ever-bigger chords: the owner's report
    ///     (measure 2 of a real GP8 file) produced a note, then a
    ///     2-note chord, then a 3-note, then a 4-note, with three junk chord
    ///     templates and two zero-width hand shapes to match — for a gesture the
    ///     player performs as four single strikes that ring together, which is
    ///     exactly what the properly-tabbed let-ring run two beats later gets.
    ///     </para>
    ///     <para>
    ///     Rules. A beat MIXING tie destinations with fresh strikes is the roll
    ///     signature: the destinations are dropped (that string is already
    ///     sounding) and the beat's position joins <paramref name="tieRollSet" />
    ///     so <see cref="GpHandShapeBuilder" /> spans the run with one shape. An
    ///     all-destination beat is consumed ENTIRELY, but only while a roll is
    ///     active and only when every string it names is one the roll holds —
    ///     otherwise it is an ordinary tied chord and
    ///     <see cref="GpPostProcessor.MergeTiedNotes" /> owns it (which is what
    ///     the chord-with-a-rest figure in measures 74+ of the same file needs).
    ///     Each collapsed beat's authored rhythm is added to the
    ///     <see cref="GuitarNote.NotationDuration" /> of every string still
    ///     ringing, the same re-spelling <c>MergeTiedNotes</c> does for a plain
    ///     tie — it is what gives the hand shape its true ring-out.
    ///     </para>
    /// </summary>
    /// <returns>The note ids to emit, or <c>null</c> when the whole beat was
    /// consumed as ring-out and nothing should be emitted for it.</returns>
    internal static string[]? FilterTieRoll(TieRollState roll, string[] notesList,
        Dictionary<int, XElement> noteMap, FractionalPosition placementPos, Fraction beatRhythm,
        HashSet<FractionalPosition> tieRollSet)
    {
        List<string>? fresh = null;
        List<int>? tiedStrings = null;

        foreach (var idStr in notesList)
        {
            if (!int.TryParse(idStr, CultureInfo.InvariantCulture, out var id) || !noteMap.TryGetValue(id, out var node)) continue;
            if (GpNoteParser.IsTieDestination(node))
                (tiedStrings ??= []).Add(GpNoteParser.ReadStringIndex(node));
            else
                (fresh ??= []).Add(idStr);
        }

        // Nothing tied INTO this beat ⇒ it is a fresh attack and ends any roll.
        if (tiedStrings == null)
        {
            roll.End();
            return notesList;
        }

        if (fresh != null)
        {
            if (!roll.Active)
            {
                // Recognised one beat late: seed the roll from the beat these
                // ties continue. If that beat did not emit single notes on those
                // strings (a rolled CHORD start), leave the whole figure alone.
                foreach (var stringIndex in tiedStrings)
                {
                    if (roll.Previous.TryGetValue(stringIndex, out var seed))
                        roll.Held[stringIndex] = seed;
                }

                if (roll.Held.Count == 0) return notesList;

                roll.Active = true;
                tieRollSet.Add(roll.PreviousPosition);
            }

            ExtendRingingNotes(roll, beatRhythm);
            tieRollSet.Add(placementPos);
            return fresh.ToArray();
        }

        // Every note is a continuation. Only an active roll whose held strings
        // cover all of them may swallow the beat.
        if (!roll.Active) return notesList;
        foreach (var stringIndex in tiedStrings)
        {
            if (!roll.Held.ContainsKey(stringIndex))
                return notesList;
        }

        ExtendRingingNotes(roll, beatRhythm);
        return null;
    }

    /// <summary>Adds a collapsed beat's authored rhythm to every note the roll is
    /// still holding — the tie re-spelling that gives the run its real ring-out.
    /// Their LinkNext goes with it: the tie's other half is gone.</summary>
    internal static void ExtendRingingNotes(TieRollState roll, Fraction beatRhythm)
    {
        foreach (var note in roll.Held.Values)
        {
            note.NotationDuration = note.NotationDuration is { } nd ? nd + beatRhythm : beatRhythm;
            note.LinkNext = false;
        }
    }

    /// <summary>Records what a beat emitted, so the next beat can recognise a
    /// roll starting at this one and so an active roll can keep holding the
    /// strings it strikes. A beat that emitted a CHORD ends the roll.</summary>
    internal static void ObserveTieRollBeat(TieRollState roll, IList<GuitarNote> notesOut,
        int emittedFrom, bool emittedChord, FractionalPosition placementPos, bool sounded)
    {
        if (!sounded)
        {
            roll.End(); // a rest is the end of any ring-out
            roll.Previous.Clear();
            return;
        }

        roll.Previous.Clear();
        for (var i = emittedFrom; i < notesOut.Count; i++)
        {
            var note = notesOut[i];
            // Skip a materialized grace: it carries the ornament's own onset.
            if (note.Position.CompareTo(placementPos) != 0) continue;
            roll.Previous[note.StringIndex] = note;
            if (roll.Active) roll.Held[note.StringIndex] = note;
        }

        roll.PreviousPosition = placementPos;
        if (emittedChord) roll.End();
    }

    /// <summary>
    ///     Import-only state for the SHORT-REST (staccato) rule — one per voice
    ///     slot, alive for the whole track walk because the rest that articulates
    ///     a bar's LAST note falls in the next bar. See
    ///     <see cref="ObserveStaccatoBeat" />.
    /// </summary>
    internal sealed class StaccatoCarry
    {
        /// <summary>The single note waiting to find out whether a rest follows
        /// it. Null when the last beat was a rest, a chord, or nothing.</summary>
        public GuitarNote? Note { get; set; }

        /// <summary>That note's own authored value — the rest may not exceed it.</summary>
        public Fraction OwnRhythm { get; set; }

        /// <summary>The rests seen since <see cref="Note" />, as one fraction in
        /// quarter-note units.</summary>
        public long RestNum { get; set; }

        public long RestDen { get; set; } = 1;

        public void Clear()
        {
            Note = null;
            RestNum = 0;
            RestDen = 1;
        }
    }

    /// <summary>
    ///     Applies the SHORT-REST rule — the only way Rocksmith can spell an
    ///     articulation it has no mark for.
    ///     <para>
    ///     A rest is the one thing a tab writes that the chart model cannot hold:
    ///     RS drops a sub-quarter sustain, so an eighth-note / eighth-rest /
    ///     eighth-note riff (owner report, measures 40-41 of a real
    ///     GP8 file at 220 bpm) arrives as a run of bare strikes with the
    ///     articulation gone. RS has no staccato mark and the charting convention
    ///     is to spell staccato as an ACCENT, so that is what the note before the
    ///     rest gets.
    ///     </para>
    ///     <para>
    ///     A note qualifies when the rest (or run of rests) after it is at most
    ///     the note's own authored value AND at most a quarter, and
    ///     something sounds again afterwards. Both length rules exist to keep an
    ///     ordinary phrase ENDING — a short note before a bar of silence — from
    ///     reading as staccato, and the "sounds again" rule is why the decision is
    ///     deferred to the NEXT sound rather than computed per bar: the rest that
    ///     articulates a bar's last note lives in the FOLLOWING bar, which is a
    ///     different call (the owner's own example, the last eighth of measure 40,
    ///     was missed by a per-bar pass). CHORDS are deliberately excluded — they
    ///     take the other half of this setting, where a rest breaks their shared
    ///     hand-shape bar (<see cref="ChordHandShapeBuilder" />) and no accent is
    ///     wanted.
    ///     </para>
    /// </summary>
    internal static void ObserveStaccatoBeat(StaccatoCarry carry, IList<GuitarNote> notesOut,
        int emittedFrom, FractionalPosition placementPos, bool sounded, Fraction rhythm,
        bool emittedChord)
    {
        if (!sounded)
        {
            // A rest only matters while a note is waiting to be articulated.
            if (carry.Note == null || rhythm.Denominator <= 0) return;
            carry.RestNum = carry.RestNum * rhythm.Denominator + rhythm.Numerator * carry.RestDen;
            carry.RestDen *= rhythm.Denominator;
            // Longer than a quarter note is silence, not an articulation.
            if (carry.RestNum > carry.RestDen) carry.Clear();
            return;
        }

        // Something sounds: it closes whatever gap preceded it.
        if (carry.Note is { } waiting && carry.RestNum > 0 && carry.OwnRhythm.Denominator > 0 &&
            carry.RestNum * carry.OwnRhythm.Denominator <= carry.OwnRhythm.Numerator * carry.RestDen)
        {
            waiting.Accent = true;
        }

        carry.Clear();
        if (emittedChord) return;

        GuitarNote? single = null;
        for (var i = emittedFrom; i < notesOut.Count; i++)
        {
            // Skip a materialized grace: it carries the ornament's own onset.
            if (notesOut[i].Position.CompareTo(placementPos) != 0) continue;
            if (single != null) return; // several notes at one onset is not a single strike
            single = notesOut[i];
        }

        if (single == null) return;
        carry.Note = single;
        carry.OwnRhythm = rhythm;
    }
}
