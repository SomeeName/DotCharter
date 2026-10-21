using ChartLevel = DotCharter.Core.Domain.Project.ChartLevel;
using Chord = DotCharter.Core.Domain.Guitar.Chord;
using ChordNote = DotCharter.Core.Domain.Guitar.ChordNote;
using GuitarNote = DotCharter.Core.Domain.Guitar.GuitarNote;

namespace DotCharter.Services.Parsing.GuitarPro;

/// <summary>
///     Post-import cleanup applied to a <see cref="ChartLevel"/> after its raw
///     notes and chords have been parsed from GP: merges tied sounds, resolves
///     "link to next" slides, and trims sustains from notes that don't need
///     them. All operations are pure over the level.
/// </summary>
public static class GpPostProcessor
{
    /// <summary>
    ///     Runs the standard post-import passes in the order the original
    ///     monolithic importer did, plus the auto-handshape pass shared by every
    ///     tab importer. <see cref="ChordHandShapeBuilder.BuildAndTrim"/> runs
    ///     BEFORE <see cref="AdjustSustains"/> so a chord's handshape spans its
    ///     AUTHORED duration before the sustain is trimmed to a short strike.
    ///     <paramref name="settings"/> carries the tab-import options (dead
    ///     strums, EOF short-note/short-chord truncation); null ⇒ EOF defaults.
    /// </summary>
    /// <param name="level">The level whose parsed notes/chords are finalized.</param>
    /// <param name="arrangement">The arrangement the level belongs to — supplies the
    /// string count and receives the chord templates the handshape pass creates.</param>
    /// <param name="settings">Tab-import options; null ⇒ <see cref="GpImportSettings.Default"/>.</param>
    /// <param name="onBeatGraces">Import-only carrier for GP's per-grace
    /// BeforeBeat/OnBeat flavour, forwarded to
    /// <see cref="MaterializeGraceSlides"/>. Only the GP3-5 BINARY importer fills
    /// it (that path materializes single-note graces here); the gpif and Songsterr
    /// paths pass null, which is the GP default of BEFORE-beat and leaves their
    /// behaviour byte-for-byte unchanged.</param>
    /// <param name="tapOriginated">Import-only set of LEFT-hand-tapped notes
    /// (normalised to hammer-ons). <see cref="AdjustSustains"/> skips these when it
    /// re-derives a descending hammer-on into a pull-off, because a left-hand tap
    /// is a fresh tap-on, not a legato pull-off. Null ⇒ no exemptions (the
    /// Songsterr path and any caller without taps).</param>
    /// <param name="authoredFingers">Import-only per-(string, position) LEFT-hand
    /// fingering read from the source file, forwarded to
    /// <see cref="ChordHandShapeBuilder.BuildAndTrim" /> so a new chord template
    /// prefers the AUTHORED finger over the suggested one. Both GP importers fill
    /// it; the Songsterr path passes null (its payload carries no fingering),
    /// which leaves that path byte-for-byte unchanged.</param>
    /// <param name="authoredChordNames">Import-only per-position chord NAME read
    /// from the source file's chord diagram, forwarded to
    /// <see cref="ChordHandShapeBuilder.BuildAndTrim" /> so a new chord template
    /// keeps the ARRANGER's name instead of <c>ChordNameSuggester</c>'s
    /// pitch-class guess. Only the GP3-5 BINARY importer fills it so far — gpif
    /// chord diagrams and the Songsterr payload are both still unread, and both
    /// pass null, which leaves those paths byte-for-byte unchanged.</param>
    /// <returns>The slide-in ornaments materialized (<see cref="MaterializeSlideIns"/>).</returns>
    public static int Run(ChartLevel level, Arrangement arrangement,
        GpImportSettings? settings = null, IReadOnlySet<GuitarNote>? onBeatGraces = null,
        IReadOnlySet<IGuitarSound>? tapOriginated = null,
        GpAuthoredFingering? authoredFingers = null,
        GpAuthoredChordNames? authoredChordNames = null)
    {
        settings ??= GpImportSettings.Default;
        var stringCount = arrangement.StringCount;
        // A pick scrape is its own sound — a chord split into notes, or a note
        // — so no strum keeps a scrape string and no hand shape
        // below is ever built for one.
        GpPickScrape.IsolateScrapes(level);
        // Turn slide-in ornaments and "sl." grace hints into real sliding notes
        // FIRST, so every later pass (merge, link, handshape, sustain) sees them
        // as ordinary sounds — and so no slide-in sentinel can leak downstream.
        var slideIns = MaterializeSlideIns(level, settings.SlideInFretDistance, arrangement.Capo);
        MaterializeGraceSlides(level, onBeatGraces);
        // Materialization inserts sounds; a same-string/same-instant collision
        // (authored or inserted) is physically impossible and poisons every
        // downstream pass, so it is resolved before anything reads pairs.
        RemoveDuplicateSounds(level);
        MergeTiedNotes(level, stringCount);
        LinkSlides(level, stringCount);
        CleanDeadChordNotes(level);
        ChordHandShapeBuilder.BuildAndTrim(level, arrangement, authoredFingers, authoredChordNames,
            settings.PreserveShortRests);
        ClampHandShapes(level);
        // AdjustSustains IS the sub-quarter-note truncation (EOF
        // truncate_short_notes/chords): it drops sustains authored shorter than a
        // quarter note. The two EOF toggles gate that drop — a sound needing its
        // sustain to render a technique is never truncated, so I08's swung slide
        // keeps its 2/3 sustain regardless. Runs last, on the finalized sustains.
        AdjustSustains(level, stringCount, settings.TruncateShortNotes, settings.TruncateShortChords,
            tapOriginated);
        // A pitched slide must END at the note it slides into — an over-long
        // authored sustain otherwise runs the slide tail past the landing
        // (owner report). Runs after AdjustSustains so it survives
        // the chord-level max-sustain assignment.
        ClampSlideSustains(level, stringCount);
        // Bend points are positioned against the AUTHORED sustain at parse time,
        // but swing placement, tie-merge and the trim above can all shorten the
        // final sustain — leaving a bend's tail beyond the note's end, where it
        // renders on the NEXT sound (owner report). Clamp last, once
        // sustains are final.
        ClampBendsToSustain(level);
        return slideIns;
    }

    /// <summary>
    ///     Ensures no bend point lies past its note's finalized end — a bend
    ///     cannot sound after the note stops. Any point beyond the end is pulled
    ///     back to the end (the curve holds its reached value to the note end);
    ///     points before the start are pulled up to the start. Runs after every
    ///     sustain-changing pass.
    /// </summary>
    /// <summary>
    ///     Clamps a pitched slide's sustain so it ends exactly at the sound it
    ///     slides into (the next sound on the same string) — the slide travels
    ///     across the sustain and must land on, not overshoot, its target. Only
    ///     shortens; a slide whose sustain already stops at or before its target
    ///     is untouched. Unpitched (whammy/scrape) slides are left alone.
    /// </summary>
    public static void ClampSlideSustains(ChartLevel level, int stringCount)
    {
        var soundsByString = BuildSoundsByString(level, stringCount);
        for (var str = 0; str < stringCount; str++)
        {
            var stringSounds = soundsByString[str];
            for (var i = 0; i < stringSounds.Count - 1; i++)
            {
                var cur = stringSounds[i].Sound;
                if (cur.SlideTo is not > 0 || cur.UnpitchedSlide) continue;
                var gap = stringSounds[i + 1].Position - stringSounds[i].Position;
                if (cur.Sustain.CompareTo(gap) > 0) cur.Sustain = gap;
            }
        }
    }

    public static void ClampBendsToSustain(ChartLevel level)
    {
        // The single shared invariant (Core BendGeometry): a bend point can never
        // lie outside [onset, onset + sustain]. Import and the save/edit spacing
        // enforcer both call it, so a sustain-shortening pass in either place can
        // never leave a bend hanging past the note.
        foreach (var note in level.Notes)
            BendGeometry.ClampBendsToSustain(note, note.Position);

        foreach (var chord in level.Chords)
        {
            foreach (var cn in chord.ChordNotes)
                BendGeometry.ClampBendsToSustain(cn, chord.Position);
        }
    }

    /// <summary>
    ///     Removes the dead ("x") notes from a PARTIALLY dead chord. Dead notes
    ///     inside a sounding chord carry TAB-ARBITRARY frets — GP stores
    ///     whatever the engraver left on the string. Real-chart hit (owner
    ///     report): an 11-x-11 octave shape arrived as frets
    ///     11-1-11; the fret-1 ghost then drove an 11-fret-wide chord
    ///     template, a fret-1 FHP anchor under a fret-11 chord and nonsense
    ///     auto-fingering. Owner-decided rule, unchanged: such a note is
    ///     REMOVED (the mute is a strum artifact, not chart content). Runs
    ///     BEFORE template creation so templates, fingering and anchors never
    ///     see tab-noise frets.
    ///     <para>
    ///     A FULLY dead strum (every string x) is the opposite case and is left
    ///     ALONE — shape, fingering and <see cref="MuteType.Full" /> all survive.
    ///     There is no sounding note for the frets to contradict, so they are the
    ///     real fret-hand position the player mutes at, and EOF / Log Charter both
    ///     preserve them (verified against EOF's RS2 output for the same
    ///     source file: <c>fretHandMute="1"</c> carrying the shape's real frets and
    ///     the SAME chordId as the surrounding unmuted chords). This
    ///     branch must not zero every fret (as a <c>DeadStrumsAsOpenMutes</c>
    ///     setting would): that destroys the shape, forces a bogus all-open chord template
    ///     and drops a fret-0 FHP anchor under an 11th-position passage — the very
    ///     failure class the partial-dead rule above exists to prevent. Because
    ///     <see cref="ChordHandShapeBuilder" />'s find-or-create matches on FRETS
    ///     alone, preserving them makes a muted strum resolve to the same template
    ///     as its identically-shaped unmuted neighbours automatically.
    ///     </para>
    ///     <para>
    ///     A chord the removal leaves with ONE note is no chord: it becomes a
    ///     single note carrying every technique of the surviving string
    ///     (<c>5/x</c> imports as the note <c>5</c>, not a one-note chord with a
    ///     one-string template and hand shape).
    ///     </para>
    /// </summary>
    public static void CleanDeadChordNotes(ChartLevel level)
    {
        List<Chord>? singles = null;
        foreach (var chord in level.Chords)
        {
            if (chord.ChordNotes.Count == 0) continue;
            var hasSounding = false;
            var hasDead = false;
            foreach (var cn in chord.ChordNotes)
            {
                if (cn.Mute == MuteType.Full) hasDead = true;
                else hasSounding = true;
            }

            // Nothing dead, or EVERYTHING dead (a fret-hand-muted strum, which
            // keeps its authored shape verbatim) ⇒ leave the chord untouched.
            if (!hasDead || !hasSounding) continue;

            for (var i = chord.ChordNotes.Count - 1; i >= 0; i--)
            {
                if (chord.ChordNotes[i].Mute == MuteType.Full)
                    chord.ChordNotes.RemoveAt(i);
            }

            if (chord.ChordNotes.Count == 1) (singles ??= []).Add(chord);
        }

        if (singles == null) return;
        foreach (var chord in singles)
        {
            level.Chords.Remove(chord);
            var note = ToSingleNote(chord, chord.ChordNotes[0]);
            var index = 0;
            while (index < level.Notes.Count && level.Notes[index].Position.CompareTo(note.Position) <= 0) index++;
            level.Notes.Insert(index, note);
        }
    }

    /// <summary>The one string left of a partially dead chord as a standalone
    /// note. Every playable field travels; the chord's <see cref="Chord.Accent" />
    /// and <see cref="Chord.Ignore" /> fold in because they describe the strike
    /// as a whole.</summary>
    internal static GuitarNote ToSingleNote(Chord chord, ChordNote chordNote)
    {
        var note = new GuitarNote
        {
            Position = chord.Position,
            NotationDuration = chord.NotationDuration,
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

    /// <summary>A grace ornament's lead-in length in quarter-note units: a 32nd
    /// note — short enough to read as an ornament, long enough to render the
    /// slide, and exactly what EOF and Log Charter emit for the grace slides in
    /// the cross-tool reference file (0.063 s at 120 bpm). GP's grace beats carry
    /// their own rhythm, but every grace in the reference score IS a 32nd, so the
    /// constant matches the source as well as the references.</summary>
    public static readonly Fraction GraceLeadIn = new(1, 8);

    /// <summary>A slide-in's lead-in length in quarter-note units: a 16th note.
    /// EOF renders both slide-ins of the reference file as a 0.125 s origin note
    /// followed by the target for the rest of the quarter — a 1:3 split.</summary>
    public static readonly Fraction SlideInLeadIn = new(1, 4);

    /// <summary>Default distance a materialized slide-in's origin sits below (or
    /// above) the target. GP's <c>Slide</c> flags 16/32 say only "arrive from
    /// under/over" and name NO origin fret, so the origin is fabricated: EOF picks
    /// ONE fret (3→2, 5→4), Log Charter picks TWO (3→1, 5→3). The owner's chosen
    /// default is TWO; it is configurable 1–3 via
    /// <see cref="GpImportSettings.SlideInFretDistance"/> (the "slide-in distance"
    /// import setting), clamped to that range in <see cref="MaterializeSlideIns"/>.</summary>
    public const int DefaultSlideInFretDistance = 2;

    /// <summary>Minimum / maximum origin distance a slide-in may be fabricated at
    /// (a 0-fret origin is no slide; beyond 3 stops reading as an ornament).</summary>
    public const int MinSlideInFretDistance = 1;

    public const int MaxSlideInFretDistance = 3;

    /// <summary>
    ///     Where an ornament of the requested length sits against its target.
    ///     <c>TargetShift</c> is how far the target itself moves (and how
    ///     much of its sustain the ornament eats): zero for a BEFORE-beat grace,
    ///     which steals time ahead of the beat, and the ornament's length for an
    ///     ON-beat one, which sits ON the beat and pushes the target late.
    /// </summary>
    public readonly record struct GracePlacement(
        FractionalPosition GracePosition, FractionalPosition GraceLength, FractionalPosition TargetShift);

    /// <summary>
    ///     Places an ornament of <paramref name="length"/> against a target at
    ///     <paramref name="targetPos"/>.
    ///     <para>
    ///     Guitar Pro distinguishes the two grace flavours
    ///     (<c>&lt;GraceNotes&gt;BeforeBeat|OnBeat&lt;/GraceNotes&gt;</c>) and they
    ///     place differently: a BEFORE-beat grace is squeezed in ahead of the beat
    ///     (the target does not move); an ON-beat grace takes the front of the beat
    ///     and pushes the target — which is what EOF and Log Charter both emit
    ///     (reference file: grace at 22.540 with the target moved to 22.603).
    ///     ON-beat is ALSO the fallback when a before-beat grace has no room left
    ///     by <paramref name="earliest"/> (the end of the previous sound on that
    ///     string). That fallback matters: the old "no room ⇒ give up" rule
    ///     silently DROPPED two of the reference file's grace slides, because their
    ///     targets abut the previous note exactly.
    ///     </para>
    ///     The ornament never eats more than half its target, so it stays an
    ///     ornament instead of becoming the note.
    /// </summary>
    public static GracePlacement PlaceGrace(FractionalPosition targetPos, FractionalPosition targetSustain,
        Fraction length, bool beforeBeat, FractionalPosition earliest)
    {
        var lead = new FractionalPosition(0, length);
        if (targetSustain.CompareTo(FractionalPosition.Zero) > 0)
        {
            var half = targetSustain * new Fraction(1, 2);
            if (lead.CompareTo(half) > 0) lead = half;
        }

        if (lead.CompareTo(FractionalPosition.Zero) <= 0)
            return new GracePlacement(targetPos, FractionalPosition.Zero, FractionalPosition.Zero);

        if (beforeBeat)
        {
            var start = targetPos - lead;
            if (start.CompareTo(earliest) >= 0)
                return new GracePlacement(start, lead, FractionalPosition.Zero);
        }

        return new GracePlacement(targetPos, lead, lead);
    }

    /// <summary>Subtracts an ornament's length from a sustain, never going
    /// negative.</summary>
    public static FractionalPosition ShortenSustain(FractionalPosition sustain, FractionalPosition by)
    {
        var result = sustain - by;
        return result.CompareTo(FractionalPosition.Zero) > 0 ? result : FractionalPosition.Zero;
    }

    /// <summary>
    ///     Turns a GP slide-IN ornament (<c>Slide</c> flag 16 "from below" / 32
    ///     "from above", parsed onto the note as the
    ///     <see cref="GpNoteParser.SlideInFromBelow"/> /
    ///     <see cref="GpNoteParser.SlideInFromAbove"/> sentinel) into a real
    ///     pitched slide: a short origin note <paramref name="originFretDistance"/>
    ///     frets below/above the target that slides into it, with the target pushed
    ///     back by the lead-in. Both references materialize the ornament this way
    ///     — leaving it unhandled rendered a static note and lost the gesture.
    ///     <para>
    ///     A slide-in inside a CHORD materializes the same way the GP3-5 path's
    ///     "sl." chord grace does (<c>GpLegacyImporter.ApplyChordGraces</c>): one
    ///     origin CHORD holding just the sliding strings, each sliding into its
    ///     own target, with the whole strum pushed back by the lead-in (a lone
    ///     sliding string gets a single origin note); clearing only the sentinel would
    ///     lose every strummed slide-in. The sentinel NEVER survives this pass.
    ///     </para>
    /// </summary>
    /// <param name="level">The level whose slide-in sentinels are materialized.</param>
    /// <param name="originFretDistance">How far below/above the target the origin
    /// sits (owner setting; <see cref="DefaultSlideInFretDistance"/> = 2, EOF = 1,
    /// Log Charter = 2). Clamped to
    /// [<see cref="MinSlideInFretDistance"/>, <see cref="MaxSlideInFretDistance"/>].</param>
    /// <param name="capo">The arrangement's capo. The synthesized origin is an
    /// ABSOLUTE fret, so its floor is <see cref="GpCapoFrets.MinFrettedFret"/>,
    /// not a hard <c>1</c> — a fret below the capo is an export-blocking
    /// <c>KeyNoteBelowCapo</c> error. Default <c>0</c> ⇒ the historical fret-1
    /// floor, so a direct caller is unchanged.</param>
    /// <returns>How many slide-in ornaments became real slides (one per target
    /// note or chord note) — the import's own tally, which the tab-corpus census
    /// compares with what the score authored.</returns>
    public static int MaterializeSlideIns(ChartLevel level, int originFretDistance = DefaultSlideInFretDistance,
        int capo = 0)
    {
        var distance = Math.Clamp(originFretDistance, MinSlideInFretDistance, MaxSlideInFretDistance);
        var minFret = GpCapoFrets.MinFrettedFret(capo);
        var added = new List<GuitarNote>();
        var addedChords = new List<Chord>();
        var materialized = 0;

        // The origin fret of a slide-in sentinel, or null when the ornament
        // cannot become a slide. The sentinel is consumed either way — it is not
        // a fret.
        int? Origin(IGuitarSound sound)
        {
            var direction = sound.SlideTo switch
            {
                GpNoteParser.SlideInFromBelow => -1,
                GpNoteParser.SlideInFromAbove => 1,
                _ => 0
            };
            if (direction == 0) return null;
            sound.SlideTo = null;

            // The LANDING must be a real fret. GpNoteParser sets the slide-in
            // sentinel even on an OPEN note (deliberately — the L09 "no slide
            // FROM an open string" guard is about the ORIGIN, and a slide-in's
            // origin is synthesized), so an open target reached here and minted
            // `SlideTo = 0`: a slide into an open string, which is not a thing a
            // hand can play. LinkSlides drops exactly that case, but only while
            // RESOLVING the -1 marker — a slide this pass SYNTHESIZES never goes
            // through it, and every later pass tests `SlideTo is > 0`, so the
            // zero was invisible all the way to SngFromDomainMapper, which sets
            // RsNoteMask.Slide for anything that is not -1.
            if (sound.Fret <= 0) return null;
            var originFret = sound.Fret + direction * distance;
            return originFret < minFret || originFret > GpCapoFrets.MaxAbsoluteFret ? null : originFret;
        }

        foreach (var target in level.Notes.OrderBy(n => n.Position).ToList())
        {
            if (Origin(target) is not { } originFret) continue;

            var place = PlaceGrace(target.Position, target.Sustain, SlideInLeadIn,
                beforeBeat: false, FractionalPosition.Zero);
            if (place.GraceLength.CompareTo(FractionalPosition.Zero) <= 0) continue;

            added.Add(SlideInOrigin(target.StringIndex, originFret, target.Fret, place));
            target.Position += place.TargetShift;
            target.Sustain = ShortenSustain(target.Sustain, place.TargetShift);
            materialized++;
        }

        foreach (var chord in level.Chords.OrderBy(c => c.Position).ToList())
        {
            List<(ChordNote Target, int OriginFret)>? pairs = null;
            foreach (var cn in chord.ChordNotes)
            {
                if (Origin(cn) is { } originFret) (pairs ??= []).Add((cn, originFret));
            }

            if (pairs == null) continue;

            var place = PlaceGrace(chord.Position, chord.GetMaxSustain(), SlideInLeadIn,
                beforeBeat: false, FractionalPosition.Zero);
            if (place.GraceLength.CompareTo(FractionalPosition.Zero) <= 0) continue;

            if (pairs.Count == 1)
            {
                var (target, originFret) = pairs[0];
                added.Add(SlideInOrigin(target.StringIndex, originFret, target.Fret, place));
            }
            else
            {
                var origin = new Chord { Position = place.GracePosition, NotationDuration = SlideInLeadIn };
                foreach (var (target, originFret) in pairs)
                {
                    origin.ChordNotes.Add(new ChordNote
                    {
                        StringIndex = target.StringIndex,
                        Fret = originFret,
                        Sustain = place.GraceLength,
                        SlideTo = target.Fret,
                        LinkNext = true
                    });
                }

                addedChords.Add(origin);
            }

            chord.Position += place.TargetShift;
            foreach (var cn in chord.ChordNotes) cn.Sustain = ShortenSustain(cn.Sustain, place.TargetShift);
            materialized += pairs.Count;
        }

        foreach (var g in added) level.Notes.Add(g);
        foreach (var c in addedChords) level.Chords.Add(c);
        return materialized;

        static GuitarNote SlideInOrigin(int stringIndex, int originFret, int targetFret, GracePlacement place) => new()
        {
            Position = place.GracePosition,
            Sustain = place.GraceLength,
            StringIndex = stringIndex,
            Fret = originFret,
            SlideTo = targetFret,
            LinkNext = true,
            NotationDuration = SlideInLeadIn
        };
    }

    /// <summary>
    ///     Materializes a "sl." grace ornament (a <see cref="GuitarNote.GraceFret"/>
    ///     flagged <see cref="GuitarNote.GraceSlide"/>) into a real short note that
    ///     slides into its target — the representation EOF and Log Charter both
    ///     produce (<c>SlideTo</c> = target fret, <c>LinkNext</c>) — instead of a
    ///     notation-only hint that never shows on the highway. The hint is consumed
    ///     (both fields cleared on the target). A same-fret / open grace degrades
    ///     back to a plain hint.
    ///     <para>
    ///     This is the GP3-5 BINARY path's materializer. Guitar Pro distinguishes
    ///     BEFORE-beat from ON-beat grace ornaments and the difference is audible,
    ///     so the flavour is honoured here exactly as the gpif path honours its
    ///     <c>&lt;GraceNotes&gt;</c> element: a note named in
    ///     <paramref name="onBeatGraces"/> places ON the beat and pushes its target
    ///     late, anything else is tried ahead of the beat first and falls back to
    ///     on-beat when the previous sound leaves no room (see
    ///     <see cref="PlaceGrace"/>). A null set therefore means "every grace is
    ///     before-beat", which is BOTH the pre-0.33.2 behaviour of this pass and
    ///     GP's own default for the versions that store no flavour bit at all
    ///     (GP3/GP4). The gpif path materializes its own graces in
    ///     <c>GpArrangementParser</c>, where the authored flag and a whole grace
    ///     CHORD are still available, and passes null here.
    ///     </para>
    /// </summary>
    /// <param name="level">The level whose grace hints are materialized in place.</param>
    /// <param name="onBeatGraces">The notes whose grace ornament was authored
    /// ON-beat, by reference identity. Null (or a note not in it) ⇒ before-beat.</param>
    public static void MaterializeGraceSlides(ChartLevel level, IReadOnlySet<GuitarNote>? onBeatGraces = null)
    {
        var prevEndByString = new Dictionary<int, FractionalPosition>();
        var added = new List<GuitarNote>();

        foreach (var note in level.Notes.OrderBy(n => n.Position).ToList())
        {
            var target = note; // the grace hint lives on the note it slides INTO
            prevEndByString.TryGetValue(target.StringIndex, out var prevEnd);

            // `target.Fret > 0` is the LANDING half of the rule: the guard used
            // to constrain only the GRACE fret, so a "sl." ornament onto an OPEN
            // string minted `SlideTo = 0` — the same slide-into-an-open-string
            // LinkSlides drops when it resolves the -1 marker, and the same one
            // no later pass can see because they all test `SlideTo is > 0`. An
            // open target keeps the notation-only GraceFret hint instead.
            if (target.GraceFret is { } graceFret && target.GraceSlide &&
                graceFret > 0 && target.Fret > 0 && graceFret != target.Fret)
            {
                // GP5 spells the flavour in the grace block's flag byte (bit 0x02
                // set = ON the beat); GP3/GP4 have no such byte, so they default to
                // before-beat — exactly what a null/absent entry means here.
                var beforeBeat = onBeatGraces?.Contains(target) != true;
                var place = PlaceGrace(target.Position, target.Sustain, GraceLeadIn,
                    beforeBeat, prevEnd);
                if (place.GraceLength.CompareTo(FractionalPosition.Zero) > 0)
                {
                    added.Add(new GuitarNote
                    {
                        Position = place.GracePosition,
                        Sustain = place.GraceLength,
                        StringIndex = target.StringIndex,
                        Fret = graceFret,
                        SlideTo = target.Fret,
                        LinkNext = true,
                        NotationDuration = GraceLeadIn
                    });
                    target.Position += place.TargetShift;
                    target.Sustain = ShortenSustain(target.Sustain, place.TargetShift);
                    target.GraceFret = null; // consumed — it's a real landing note now
                }
            }

            target.GraceSlide = false; // transient flag never persists
            var end = target.EndPosition;
            if (!prevEndByString.TryGetValue(target.StringIndex, out var e) || end.CompareTo(e) > 0)
                prevEndByString[target.StringIndex] = end;
        }

        foreach (var g in added) level.Notes.Add(g);
    }

    /// <summary>
    ///     Removes duplicate sounds — two events on the SAME string at the SAME
    ///     position. A string can only sound once at an instant, so these are never
    ///     legitimate chart content; nothing downstream deleted them either
    ///     (<c>ChartSpacingEnforcer</c> only ever SHORTENS sustains), so a
    ///     mis-authored tab or a stacked ornament survived all the way into the
    ///     exported chart as an unplayable double hit.
    ///     <para>
    ///     Deliberately conservative about WHICH one goes:
    ///     <list type="bullet">
    ///     <item>Note vs. chord on the same string+instant ⇒ the standalone NOTE is
    ///     dropped. Removing a chord note would rewrite the strum's shape, its
    ///     find-or-create template and its hand shape.</item>
    ///     <item>Note vs. note ⇒ the one carrying MORE playable information wins
    ///     (slide / bend / link / vibrato / a sustain at all); an exact tie keeps
    ///     the FIRST one, so the result is order-stable and idempotent.</item>
    ///     <item>Two CHORDS at one position are left completely alone — merging
    ///     them is a shape decision, not a dedup.</item>
    ///     </list>
    ///     </para>
    /// </summary>
    public static void RemoveDuplicateSounds(ChartLevel level)
    {
        if (level.Notes.Count == 0) return;

        var claimedByChord = new HashSet<(int String, FractionalPosition Position)>();
        foreach (var chord in level.Chords)
        {
            foreach (var cn in chord.ChordNotes)
                claimedByChord.Add((cn.StringIndex, chord.Position));
        }

        var kept = new Dictionary<(int String, FractionalPosition Position), GuitarNote>();
        var doomed = new List<GuitarNote>();

        foreach (var note in level.Notes)
        {
            var key = (note.StringIndex, note.Position);
            if (claimedByChord.Contains(key))
            {
                doomed.Add(note);
                continue;
            }

            if (!kept.TryGetValue(key, out var incumbent))
            {
                kept[key] = note;
                continue;
            }

            if (DuplicateWeight(note) > DuplicateWeight(incumbent))
            {
                kept[key] = note;
                doomed.Add(incumbent);
            }
            else
            {
                doomed.Add(note);
            }
        }

        foreach (var d in doomed) level.Notes.Remove(d);
    }

    /// <summary>How much playable information a note carries — the tie-break when
    /// two notes collide on one string at one instant.</summary>
    private static int DuplicateWeight(GuitarNote note)
    {
        var weight = 0;
        if (note.SlideTo.HasValue || note.UnpitchedSlide) weight += 4;
        if (note.BendValues.Count > 0) weight += 4;
        if (note.LinkNext) weight += 2;
        if (note.Vibrato || note.Tremolo) weight += 1;
        if (note.Harmonic != HarmonicType.None) weight += 1;
        if (note.Sustain.CompareTo(FractionalPosition.Zero) > 0) weight += 1;
        return weight;
    }

    public static void MergeTiedNotes(ChartLevel level, int stringCount)
    {
        var notesByString = BuildNotesByString(level, stringCount);
        for (var str = 0; str < stringCount; str++)
        {
            var stringNotes = notesByString[str];
            for (var i = 0; i < stringNotes.Count - 1; i++)
            {
                var current = stringNotes[i];
                if (!current.LinkNext) continue;

                var next = stringNotes[i + 1];
                if (current.Fret != next.Fret || (current.Position + current.Sustain).CompareTo(next.Position) != 0)
                    continue;

                var sameTechniques = current.Vibrato == next.Vibrato && current.Tremolo == next.Tremolo &&
                                     current.Harmonic == next.Harmonic && current.Mute == next.Mute;

                if (!sameTechniques || !TieSlidesCompatible(current, next)) continue;

                current.Sustain += next.Sustain;
                current.LinkNext = next.LinkNext;
                AdoptTieTailSlide(current, next);
                // Merged tied beats: the authored notation length is the SUM of
                // the tied rhythms (the sheet re-splits it into tied glyphs).
                if (current.NotationDuration is { } cd && next.NotationDuration is { } ndur)
                    current.NotationDuration = cd + ndur;
                GpBendUtilities.AddMissingBends(current.BendValues, next.BendValues);
                level.Notes.Remove(next);
                stringNotes.RemoveAt(i + 1);
                i--;
            }
        }

        var chords = new List<Chord>(level.Chords);
        chords.Sort((left, right) => left.Position.CompareTo(right.Position));
        for (var i = 0; i < chords.Count - 1; i++)
        {
            var current = chords[i];
            var next = chords[i + 1];
            if (current.ChordNotes.Count == 0 || next.ChordNotes.Count == 0) continue;

            var currentEnd = current.Position + current.ChordNotes[0].Sustain;
            if (currentEnd.CompareTo(next.Position) != 0) continue;
            if (!CanMergeTiedChords(current, next)) continue;

            foreach (var cn in current.ChordNotes)
            {
                var nn = FindChordNoteByString(next, cn.StringIndex);
                if (nn == null) continue;

                cn.Sustain += nn.Sustain;
                cn.LinkNext = nn.LinkNext;
                AdoptTieTailSlide(cn, nn);
                GpBendUtilities.AddMissingBends(cn.BendValues, nn.BendValues);
            }

            if (current.NotationDuration is { } cd && next.NotationDuration is { } ndur)
                current.NotationDuration = cd + ndur;

            level.Chords.Remove(next);
            chords.RemoveAt(i + 1);
            i--;
        }
    }

    /// <summary>
    ///     Resolves every "slide to the next sound" marker (<c>-1</c>) to the
    ///     actual landing fret on that string, and gives the resolved slide the
    ///     <see cref="IGuitarSound.LinkNext"/> RS2014 needs to SUSTAIN it into
    ///     that landing note. EOF sets <c>linkNext</c> on all twelve pitched
    ///     slides of the cross-tool reference file; without it a slide renders as
    ///     an arrow that stops dead instead of a connected gesture. The link is
    ///     only claimed when the slide's sustain actually reaches the landing
    ///     sound — a slide followed by a rest links to nothing.
    /// </summary>
    /// <summary>How far after a "slide to next" ends its landing note may start (one beat).</summary>
    internal static readonly FractionalPosition MaxSlideLandingGap = new(1);

    public static void LinkSlides(ChartLevel level, int stringCount)
    {
        var soundsByString = BuildSoundsByString(level, stringCount);

        for (var str = 0; str < stringCount; str++)
        {
            var stringSounds = soundsByString[str];
            for (var i = 0; i < stringSounds.Count - 1; i++)
            {
                var current = stringSounds[i];
                if (current.Sound.SlideTo != -1) continue;

                var next = stringSounds[i + 1];
                // The landing has to follow the slide (owner rule): a
                // slide followed by a rest must not take whatever note comes next on
                // that string as its target, bars later if need be. Past one beat
                // after the slide's end there is no landing; it imports as a plain
                // note.
                if (next.Position - (current.Position + current.Sound.Sustain) > MaxSlideLandingGap)
                {
                    current.Sound.SlideTo = null;
                    continue;
                }

                var targetFret = next.Sound.Fret;
                // A slide that resolves to its own fret is no slide at all
                // (inspector E01: "slide ends on its own start fret") — drop
                // the slide and keep any legato link, which degrades the note
                // to a plain linked note. A landing on an OPEN string is the
                // same non-slide, and it is the LANDING-side twin of the L09
                // (SlideFromOpenString) rule the parsers already enforce on the
                // slide OUT (GpNoteParser.ApplySlideFlags / SongsterrConverter
                // only set the -1 marker when Fret > 0): there is no fretted
                // finish to slide TO. Fret 0 must be dropped HERE because every
                // later pass tests `SlideTo is > 0` and would wave it through,
                // and the SNG mapper exports any non-(-1) value as a real slide.
                if (targetFret <= 0 || targetFret == current.Sound.Fret)
                {
                    current.Sound.SlideTo = null;
                    continue;
                }

                current.Sound.SlideTo = targetFret;
                if ((current.Position + current.Sound.Sustain).CompareTo(next.Position) >= 0)
                    current.Sound.LinkNext = true;
            }

            // Anything still holding a negative slide target has no landing note:
            // a trailing "slide to next" with no next, or a slide-in sentinel on a
            // NOTATION-only voice (those run this pass without
            // MaterializeSlideIns). A negative fret must never reach the chart.
            foreach (var sound in stringSounds)
            {
                if (sound.Sound.SlideTo is < 0)
                    sound.Sound.SlideTo = null;
            }
        }
    }

    /// <summary>
    ///     Clips each hand shape's end to the next hand shape's start so
    ///     consecutive shapes never overlap (inspector L17) — the convention
    ///     mature charters enforce as a post-import fix. Shapes are clamped in
    ///     place; a shape fully inside its successor degrades to zero width
    ///     rather than being removed (keeps the per-chord idempotency contract).
    /// </summary>
    public static void ClampHandShapes(ChartLevel level)
    {
        var shapes = new List<HandShape>(level.HandShapes);
        shapes.Sort((left, right) => left.Position.CompareTo(right.Position));
        for (var i = 0; i < shapes.Count - 1; i++)
        {
            if (shapes[i].EndPosition.CompareTo(shapes[i + 1].Position) > 0)
                shapes[i].EndPosition = shapes[i + 1].Position;
        }
    }

    /// <summary>
    ///     Finalizes sustains after import: flips HO→PO on a lower target, drops a
    ///     dead link-next, and — the EOF truncation — zeroes sub-quarter-note
    ///     sustains. <paramref name="truncateShortNotes"/> (default on, the app's
    ///     historical behaviour) gates the single-note drop; a note that needs its
    ///     sustain to render a technique (bend / slide / vibrato / tremolo /
    ///     link-next / being a slide-or-link target) is NEVER truncated. Turning it
    ///     off preserves the authored short sustain.
    ///     <para>
    ///     "Slide-or-link target" means the gesture REACHES it: the previous sound
    ///     on that string ends at or after this one's onset, and it is a pitched
    ///     slide or a link — an unpitched slide-OUT lands nowhere and claims
    ///     nothing. Without both guards a single slide-out kept the sustain of the
    ///     next note on its string however many bars later that note fell.
    ///     </para> <paramref name="truncateShortChords"/>
    ///     (default OFF — the handshape bar already carries a chord's duration)
    ///     additionally zeroes the sustain of a chord authored shorter than a
    ///     quarter note even when a sustain-visible technique would otherwise keep it.
    /// </summary>
    public static void AdjustSustains(ChartLevel level, int stringCount,
        bool truncateShortNotes = true, bool truncateShortChords = false,
        IReadOnlySet<IGuitarSound>? tapOriginated = null)
    {
        var targetSounds = NormaliseLegatoAndFindTargets(level, stringCount, tapOriginated);

        foreach (var note in level.Notes)
        {
            var isShort = note.Sustain.BeatId == 0;
            var keepSustain = RequiresSustain(note, targetSounds);
            var isMutedOrHarmonic = note.Harmonic != HarmonicType.None || note.Mute != MuteType.None;
            // EOF truncate_short_notes gates the sub-quarter drop; the mute/harmonic
            // rule is independent of it. Either way a technique note keeps its sustain.
            var truncShort = isShort && truncateShortNotes;
            if ((truncShort || isMutedOrHarmonic) && !keepSustain) note.Sustain = FractionalPosition.Zero;
        }

        foreach (var chord in level.Chords)
        {
            if (chord.ChordNotes.Count > 0)
                AdjustChordSustain(chord, targetSounds, truncateShortChords);
        }
    }

    /// <summary>
    ///     Walks each string's sounds in order: turns a descending hammer-on into the
    ///     pull-off it is, drops a link between two frets with no slide, and returns
    ///     the sounds a slide or link actually REACHES (they keep their sustain).
    /// </summary>
    private static HashSet<IGuitarSound> NormaliseLegatoAndFindTargets(ChartLevel level, int stringCount,
        IReadOnlySet<IGuitarSound>? tapOriginated)
    {
        var soundsByString = BuildSoundsByString(level, stringCount);
        var targetSounds = new HashSet<IGuitarSound>();

        for (var str = 0; str < stringCount; str++)
        {
            var stringSounds = soundsByString[str];
            for (var i = 0; i < stringSounds.Count - 1; i++)
            {
                var current = stringSounds[i].Sound;
                var next = stringSounds[i + 1].Sound;
                // A descending legato hammer-on is really a pull-off (GP's HOPO flag
                // is direction-agnostic) — EXCEPT a left-hand TAP, which is a fresh
                // tap-on that stays a hammer regardless of pitch (owner report: an
                // all-hammer tapping run got one spurious pull-off).
                if (next.Hopo == HopoType.HammerOn && next.Fret < current.Fret &&
                    tapOriginated?.Contains(next) != true)
                {
                    next.Hopo = HopoType.PullOff;
                }

                if (current.LinkNext && current.Fret != next.Fret && !current.SlideTo.HasValue)
                    current.LinkNext = false;
                // A sound is a slide-or-link TARGET only when the gesture it is
                // supposed to receive actually REACHES it. The old blanket test
                // claimed the next sound on the string unconditionally, which was
                // wrong twice over (owner report, measures 9 and 17 of
                // a real .gp: "why does this eighth have a tail"):
                //   (a) no adjacency — a slide-out in bar 5 claimed a plain
                //       eighth FIFTEEN beats later as its target, and the note
                //       then survived the sub-quarter truncation below with a
                //       visible tail no notation asked for;
                //   (b) an UNPITCHED slide (GP "slide off", Slide flags 4/8)
                //       targets nothing at all — it slides into silence. Its
                //       SlideTo is a synthetic fret±5 landing invented by
                //       GpNoteParser.ApplySlideFlags, not a real destination.
                var reachesNext = (stringSounds[i].Position + current.Sustain)
                    .CompareTo(stringSounds[i + 1].Position) >= 0;
                if (reachesNext && !current.UnpitchedSlide && (current.SlideTo is > 0 || current.LinkNext))
                    targetSounds.Add(next);
            }
        }

        return targetSounds;
    }

    private static bool RequiresSustain(IGuitarSound note, HashSet<IGuitarSound> targetSounds) =>
        note.Vibrato || note.Tremolo || note.SlideTo.HasValue || note.UnpitchedSlide || note.LinkNext ||
        note.BendValues.Count > 0 || targetSounds.Contains(note);

    private static void AdjustChordSustain(Chord chord, HashSet<IGuitarSound> targetSounds, bool truncateShortChords)
    {
        var keepSustain = false;
        var isShort = true;
        var isMutedOrHarmonic = false;
        var maxSustain = chord.ChordNotes[0].Sustain;

        foreach (var chordNote in chord.ChordNotes)
        {
            if (RequiresSustain(chordNote, targetSounds)) keepSustain = true;
            if (chordNote.Sustain.BeatId != 0) isShort = false;
            if (chordNote.Harmonic != HarmonicType.None || chordNote.Mute != MuteType.None)
                isMutedOrHarmonic = true;
            if (chordNote.Sustain.CompareTo(maxSustain) > 0)
                maxSustain = chordNote.Sustain;
        }

        // EOF truncate_short_chords: drop a sub-quarter chord sustain even when
        // a sustain-visible technique would otherwise keep it (default OFF —
        // the handshape bar already carries the chord's duration).
        // A chord split into notes ships as notes, so the note rule above is
        // the one it follows (a pick scrape keeps its tremolo tail).
        var truncate = (truncateShortChords && isShort && !chord.SplitIntoNotes)
                       || ((isShort || isMutedOrHarmonic) && !keepSustain);
        var sustain = truncate ? FractionalPosition.Zero : maxSustain;
        foreach (var cn in chord.ChordNotes)
            cn.Sustain = sustain;
    }

    private static List<GuitarNote>[] BuildNotesByString(ChartLevel level, int stringCount)
    {
        var normalizedStringCount = Math.Max(0, stringCount);
        var notesByString = new List<GuitarNote>[normalizedStringCount];
        for (var i = 0; i < notesByString.Length; i++)
            notesByString[i] = [];

        foreach (var note in level.Notes)
        {
            if ((uint)note.StringIndex < (uint)normalizedStringCount)
                notesByString[note.StringIndex].Add(note);
        }

        foreach (var stringNotes in notesByString)
            stringNotes.Sort((left, right) => left.Position.CompareTo(right.Position));

        return notesByString;
    }

    private static bool CanMergeTiedChords(Chord current, Chord next)
    {
        if (current.ChordNotes.Count != next.ChordNotes.Count) return false;

        foreach (var currentNote in current.ChordNotes)
        {
            if (!currentNote.LinkNext) return false;

            var nextNote = FindChordNoteByString(next, currentNote.StringIndex);
            if (nextNote == null || nextNote.Fret != currentNote.Fret) return false;

            if (currentNote.Vibrato != nextNote.Vibrato ||
                currentNote.Tremolo != nextNote.Tremolo ||
                currentNote.Harmonic != nextNote.Harmonic ||
                currentNote.Mute != nextNote.Mute)
            {
                return false;
            }

            if (!TieSlidesCompatible(currentNote, nextNote)) return false;
        }

        return true;
    }

    /// <summary>
    ///     Whether a tie between two sounds may be folded into ONE sound given
    ///     their slides. A tie says "this is the same struck note held longer",
    ///     so the pair collapses unless a slide makes the two halves genuinely
    ///     different gestures:
    ///     <list type="bullet">
    ///     <item>Neither slides — the ordinary tie.</item>
    ///     <item>IDENTICAL slide state on both halves. Guitar Pro copies a
    ///     slide-out onto BOTH sides of a tie, which is what a whole-note chord
    ///     tied across a barline and slid off looks like (owner report,
    ///     measures 100–101: the two halves were byte-identical and
    ///     came in as two chords, two hand shapes and a visible break at the
    ///     barline).</item>
    ///     <item>The first half has no slide and the tail carries an UNPITCHED
    ///     one — the slide-off belongs to the END of the held sound, so it moves
    ///     onto the merged sound rather than splitting it.</item>
    ///     </list>
    ///     Anything else — most importantly a PITCHED slide on the first half,
    ///     which travels to a landing the second half IS — keeps its own sound.
    /// </summary>
    private static bool TieSlidesCompatible(IGuitarSound current, IGuitarSound next)
    {
        var currentSlides = current.SlideTo.HasValue || current.UnpitchedSlide;
        var nextSlides = next.SlideTo.HasValue || next.UnpitchedSlide;

        if (!currentSlides && !nextSlides) return true;
        if (current.SlideTo == next.SlideTo && current.UnpitchedSlide == next.UnpitchedSlide) return true;
        return !currentSlides && next.UnpitchedSlide;
    }

    /// <summary>Moves a tie tail's slide-off onto the merged sound. Only fires
    /// for the third <see cref="TieSlidesCompatible"/> case; when both halves
    /// already carry the same slide the merged sound keeps its own.</summary>
    private static void AdoptTieTailSlide(IGuitarSound current, IGuitarSound next)
    {
        if (current.SlideTo.HasValue || current.UnpitchedSlide) return;
        current.SlideTo = next.SlideTo;
        current.UnpitchedSlide = next.UnpitchedSlide;
    }

    private static ChordNote? FindChordNoteByString(Chord chord, int stringIndex)
    {
        foreach (var note in chord.ChordNotes)
        {
            if (note.StringIndex == stringIndex)
                return note;
        }

        return null;
    }

    private static List<PositionedSound>[] BuildSoundsByString(ChartLevel level, int stringCount)
    {
        var normalizedStringCount = Math.Max(0, stringCount);
        var soundsByString = new List<PositionedSound>[normalizedStringCount];
        for (var i = 0; i < soundsByString.Length; i++)
            soundsByString[i] = [];

        foreach (var note in level.Notes)
        {
            if ((uint)note.StringIndex < (uint)normalizedStringCount)
                soundsByString[note.StringIndex].Add(new PositionedSound(note, note.Position));
        }

        foreach (var chord in level.Chords)
        {
            foreach (var chordNote in chord.ChordNotes)
            {
                if ((uint)chordNote.StringIndex < (uint)normalizedStringCount)
                    soundsByString[chordNote.StringIndex].Add(new PositionedSound(chordNote, chord.Position));
            }
        }

        foreach (var stringSounds in soundsByString)
            stringSounds.Sort((left, right) => left.Position.CompareTo(right.Position));

        return soundsByString;
    }

    private readonly struct PositionedSound
    {
        public PositionedSound(IGuitarSound sound, FractionalPosition position)
        {
            Sound = sound;
            Position = position;
        }

        public IGuitarSound Sound { get; }
        public FractionalPosition Position { get; }
    }
}
