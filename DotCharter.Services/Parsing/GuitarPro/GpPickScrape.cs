using DotCharter.Services.Editing;

namespace DotCharter.Services.Parsing.GuitarPro;

/// <summary>
///     Guitar Pro's PICK SCRAPE ("pick slide" down/up) as a Rocksmith chart spells
/// it. Rocksmith has no scrape technique; the charting
///     community's convention, confirmed on the owner's charts, is
///     <b>tap + tremolo + fret-hand mute + an unpitched slide in the scrape's
///     direction + ignore</b> (the ignore keeps the gesture out of scoring). A
///     scrape on several strings is ONE chord with <see cref="Chord.SplitIntoNotes" />
///     — EOF's "split" status: it selects and edits as one scrape, and every export
///     writes it as one <c>&lt;note&gt;</c> per string, with no <c>&lt;chord&gt;</c>
///     and no hand shape (<c>LinkLandingBreakup</c>). A scrape on one string is a
///     single note.
///     <para>
/// The start fret is <see cref="StartFrets">24 or 12</see> ("pick
///     scrapes are usually on the 24th fret or 12th fret"), whichever is nearer
///     the lane the convention prefers on the FRET-HAND POSITION in force at the
///     scrape: toward the nut (GP "down") the anchor's 4th lane
///     (<c>anchor + </c><see cref="NutwardStartLane" />), toward the bridge (GP
///     "up") its 1st lane (the anchor fret); a tie goes to 24. A start must lie on
///     the neck (the track's last fret, <see cref="LastFret" />) and leave room to
///     travel, so a scrape toward the bridge never starts on the last fret — on a
///     24-fret neck it always starts on 12. Only when neither fits (a capo at 12 or
///     above) does the start fall back to the lane itself. The landing travels
///     <see cref="LongTravel" /> frets (an octave), or
///     <see cref="ShortTravel" /> for a scrape shorter than a quarter note,
///     and a nutward scrape stops at fret <see cref="LowestNutwardLanding" />.
///     Measured on the owner's charts: <i>Shinbatsu wo Tadaori</i> m197, FHP 21,
///     eighth notes, A+D <c>24 → 18</c>; m198 and <i>Matasaburo</i> m160, FHP 21,
///     longer, E+A <c>24 → 12</c>; <i>Crucifix X</i> m102 and <i>Symbol I</i> m75,
///     FHP 12, E+A <c>12 → 5</c> — every one on 24 or 12.
///     </para>
///     <para>
///     The anchors are generated after the parse, so the parse (<see cref="Apply" />)
///     only marks the sound and places it provisionally against the tab's own
///     fret; <see cref="IsolateScrapes" /> (first step of
///     <see cref="GpPostProcessor.Run" />) gives every scrape a sound of its own —
///     a split chord, or a note for one string — before any chord template or hand
///     shape is built; and <see cref="RunImportFhpPass" /> lifts the scrapes out of
///     the level while the FHP is generated — their frets are not where the hand
///     is, so they must not pull an anchor — then places each one on the anchor in
///     force at its time and gives a scrape chord its fingerless template. Every
///     fret floors at <see cref="GpCapoFrets.MinFrettedFret" /> and caps at
///     <see cref="GpCapoFrets.MaxAbsoluteFret" />.
///     </para>
///     <para>
///     Both tab formats carry the scrape in the note's SLIDE bitmask, above the
///     six ordinary slide bits: <c>0x40</c> = scrape down, <c>0x80</c> = scrape
///     up — gpif <c>&lt;Property name="Slide"&gt;&lt;Flags&gt;</c> and the GP5
///     binary slide byte alike. GP3/GP4's discrete slide value has no scrape.
///     </para>
/// </summary>
internal static class GpPickScrape
{
    /// <summary>Slide-bitmask bit for a pick scrape DOWN the neck (towards the nut).</summary>
    public const int DownFlag = 0x40;

    /// <summary>Slide-bitmask bit for a pick scrape UP the neck (towards the bridge).</summary>
    public const int UpFlag = 0x80;

    /// <summary>A scrape toward the nut starts this many frets above the anchor: its 4th lane.</summary>
    public const int NutwardStartLane = 3;

    /// <summary>Frets a scrape of a quarter note or longer travels (24 → 12).</summary>
    public const int LongTravel = 12;

    /// <summary>Frets a scrape shorter than a quarter note travels (24 → 18).</summary>
    public const int ShortTravel = 6;

    /// <summary>A scrape toward the nut lands no lower than this (12 → 5).</summary>
    public const int LowestNutwardLanding = 5;

    /// <summary>The frets a scrape starts on, the higher first so a tie goes to it.</summary>
    public static readonly IReadOnlyList<int> StartFrets = [24, 12];

    private static readonly Fraction QuarterNote = new(1, 1);

    /// <summary>True when a slide bitmask carries a pick scrape in either direction.</summary>
    public static bool IsPickScrape(int slideFlags) => (slideFlags & (DownFlag | UpFlag)) != 0;

    /// <summary>
    ///     True for a sound <see cref="Apply" /> charted as a scrape: tapped,
    ///     tremolo-picked, ignored, with an unpitched slide. A Guitar Pro import
    ///     sets <c>Ignore</c> nowhere else, so the combination is unique there.
    /// </summary>
    public static bool IsScrape(IGuitarSound sound)
        => sound is GuitarNote { Ignore: true, Hopo: HopoType.Tap, Tremolo: true, UnpitchedSlide: true, SlideTo: not null }
            or ChordNote { Ignore: true, Hopo: HopoType.Tap, Tremolo: true, UnpitchedSlide: true, SlideTo: not null };

    /// <summary>
    ///     True for a sound charted as a WHOLE scrape: <see cref="IsScrape" /> plus
    ///     the fret-hand mute <see cref="IsolateScrapes" /> gives every scrape
    ///     string. The Guitar Pro export writes exactly these back as a pick slide,
    /// so an export re-imports as the scrape it was; the mute is part of
    ///     the test because the import mutes every scrape, and an unmuted sound
    ///     would come back changed.
    /// </summary>
    public static bool IsChartedScrape(IGuitarSound sound) => IsScrape(sound) && sound.Mute == MuteType.Full;

    /// <summary>True when a scrape travels toward the nut — Guitar Pro's scrape
    /// DOWN (<see cref="DownFlag" />); otherwise it is a scrape up.</summary>
    public static bool IsTowardNut(IGuitarSound scrape) => scrape.SlideTo < scrape.Fret;

    /// <summary>True for a chord <see cref="IsolateScrapes" /> made of a multi-string
    /// scrape: split into notes, every string a scrape.</summary>
    public static bool IsScrapeChord(Chord chord)
        => chord.SplitIntoNotes && chord.ChordNotes.Count > 0 && chord.ChordNotes.All(IsScrape);

    /// <summary>
    ///     Charts <paramref name="note" /> as a pick scrape: tap, tremolo, ignore,
    ///     fret-hand mute and an unpitched slide in the scrape's direction. Both
    ///     bits set reads as a scrape down. The tab's fret stands in for the hand
    ///     position until <see cref="RunImportFhpPass" /> places the scrape on the
    ///     real one; the caller has already made <see cref="IGuitarSound.Fret" />
    ///     absolute.
    /// </summary>
    public static void Apply(IGuitarSound note, int slideFlags, int capo)
    {
        var towardNut = (slideFlags & DownFlag) != 0;
        note.Hopo = HopoType.Tap;
        note.Tremolo = true;
        note.Mute = MuteType.Full;
        note.LinkNext = false;
        switch (note)
        {
            case GuitarNote single: single.Ignore = true; break;
            case ChordNote chordNote: chordNote.Ignore = true; break;
        }

        var provisionalAnchor = towardNut ? note.Fret - NutwardStartLane : note.Fret;
        Place(note, provisionalAnchor, towardNut, shortScrape: false, capo);
    }

    /// <summary>
    ///     A track's last fret from the fret count its tab stores (gpif
    ///     <c>FretCount</c>, the GP3-5 track header's fret int): capped at
    ///     <see cref="GpCapoFrets.MaxAbsoluteFret" />; a missing or implausible
    ///     count (below 12) reads as 24.
    /// </summary>
    public static int LastFret(int? fretCount)
        => fretCount is >= 12 and var n ? Math.Min(n, GpCapoFrets.MaxAbsoluteFret) : GpCapoFrets.MaxAbsoluteFret;

    /// <summary>
    ///     Sets a scrape's start and landing frets for the hand position
    ///     <paramref name="anchorFret" />: the one of <see cref="StartFrets" />
    ///     nearest the 4th lane toward the nut or the 1st toward the bridge,
    ///     travelling <see cref="ShortTravel" /> or <see cref="LongTravel" /> frets.
    /// </summary>
    public static void Place(IGuitarSound note, int anchorFret, bool towardNut, bool shortScrape, int capo,
        int lastFret = GpCapoFrets.MaxAbsoluteFret)
    {
        var floor = GpCapoFrets.MinFrettedFret(capo);
        var ceiling = Math.Clamp(lastFret, Math.Min(floor + 1, GpCapoFrets.MaxAbsoluteFret),
            GpCapoFrets.MaxAbsoluteFret);
        var anchor = Math.Clamp(anchorFret, floor, Math.Max(floor, ceiling - 1));
        var lane = towardNut ? Math.Min(anchor + NutwardStartLane, ceiling) : anchor;
        var start = SnapStart(lane, towardNut, floor, ceiling) ?? lane;
        var travel = shortScrape ? ShortTravel : LongTravel;
        if (towardNut)
        {
            var lowest = Math.Max(floor, Math.Min(LowestNutwardLanding, start - 1));
            note.Fret = start;
            note.SlideTo = Math.Max(start - travel, lowest);
        }
        else
        {
            note.Fret = start;
            note.SlideTo = Math.Min(start + travel, ceiling);
        }

        note.UnpitchedSlide = true;
    }

    /// <summary>
    ///     The <see cref="StartFrets" /> entry nearest <paramref name="lane" /> with
    ///     room to travel between <paramref name="floor" /> and
    ///     <paramref name="ceiling" />; null when none has.
    /// </summary>
    private static int? SnapStart(int lane, bool towardNut, int floor, int ceiling)
    {
        int? best = null;
        foreach (var fret in StartFrets)
        {
            var fits = towardNut ? fret > floor && fret <= ceiling : fret >= floor && fret < ceiling;
            if (fits && (best is not { } b || Math.Abs(fret - lane) < Math.Abs(b - lane))) best = fret;
        }

        return best;
    }

    /// <summary>
    ///     Gives every scrape a sound of its own before any chord template or hand
    ///     shape is built: the scrape strings of a chord become ONE chord split into
    /// notes and ignored as a whole, or a single note when the scrape has
    ///     one string; strings of the same strum that do not scrape stay a chord, or
    ///     become a note when one is left. Every scrape string is fret-hand muted
    ///     here as well, whatever a later mute bit of the tab said.
    /// </summary>
    public static void IsolateScrapes(ChartLevel level)
    {
        foreach (var note in level.Notes)
        {
            if (IsScrape(note)) note.Mute = MuteType.Full;
        }

        List<GuitarNote>? loose = null;
        List<Chord>? scrapeChords = null;
        for (var i = level.Chords.Count - 1; i >= 0; i--)
        {
            var chord = level.Chords[i];
            var scrapes = chord.ChordNotes.Where(IsScrape).ToList();
            if (scrapes.Count == 0) continue;
            foreach (var cn in scrapes) cn.Mute = MuteType.Full;

            if (scrapes.Count == chord.ChordNotes.Count)
            {
                if (scrapes.Count > 1)
                {
                    MarkScrapeChord(chord);
                    continue;
                }

                level.Chords.RemoveAt(i);
                (loose ??= []).Add(GpPostProcessor.ToSingleNote(chord, scrapes[0]));
                continue;
            }

            foreach (var cn in scrapes) chord.ChordNotes.Remove(cn);
            if (scrapes.Count > 1)
            {
                var scrapeChord = new Chord
                {
                    Position = chord.Position,
                    NotationDuration = chord.NotationDuration,
                    Accent = chord.Accent
                };
                foreach (var cn in scrapes) scrapeChord.ChordNotes.Add(cn);
                MarkScrapeChord(scrapeChord);
                (scrapeChords ??= []).Add(scrapeChord);
            }
            else
            {
                (loose ??= []).Add(GpPostProcessor.ToSingleNote(chord, scrapes[0]));
            }

            if (chord.ChordNotes.Count > 1) continue;
            level.Chords.RemoveAt(i);
            if (chord.ChordNotes.Count == 1) (loose ??= []).Add(GpPostProcessor.ToSingleNote(chord, chord.ChordNotes[0]));
        }

        foreach (var chord in scrapeChords ?? []) InsertByPosition(level.Chords, chord);
        foreach (var note in loose ?? []) InsertByPosition(level.Notes, note);
    }

    private static void MarkScrapeChord(Chord chord)
    {
        chord.SplitIntoNotes = true;
        chord.Ignore = true;
    }

    /// <summary>
    ///     The import's FHP pass with the scrapes set aside: they leave the level,
    ///     <see cref="ImportFhpPass.Run" /> generates the anchors, and each scrape
    ///     comes back placed on the anchor in force at its time (the first anchor
    ///     before any; the capo floor when there is none), on a neck that ends at
    ///     <paramref name="lastFret" /> (see <see cref="LastFret" />). A scrape chord
    ///     then plays a FINGERLESS template of its placed frets (EOF's "fingerless"
    ///     status: a fret-hand-muted scrape is no grip).
    /// </summary>
    public static void RunImportFhpPass(Arrangement arrangement, ChartLevel level,
        Func<ChordTemplate, bool>? isAuthored, int lastFret = GpCapoFrets.MaxAbsoluteFret)
    {
        var scrapes = level.Notes.Where(IsScrape).ToList();
        foreach (var scrape in scrapes) level.Notes.Remove(scrape);
        var scrapeChords = level.Chords.Where(IsScrapeChord).ToList();
        foreach (var chord in scrapeChords) level.Chords.Remove(chord);

        ImportFhpPass.Run(arrangement, level, arrangement.ArrangementType == ArrangementType.Bass, isAuthored);

        foreach (var scrape in scrapes)
        {
            PlaceOnAnchor(arrangement, level, scrape, scrape.Position, scrape.NotationDuration, lastFret);
            InsertByPosition(level.Notes, scrape);
        }

        foreach (var chord in scrapeChords)
        {
            foreach (var chordNote in chord.ChordNotes)
                PlaceOnAnchor(arrangement, level, chordNote, chord.Position, chord.NotationDuration, lastFret);
            chord.TemplateId = FingerlessTemplate(arrangement, chord).Id;
            InsertByPosition(level.Chords, chord);
        }
    }

    private static void PlaceOnAnchor(Arrangement arrangement, ChartLevel level, IGuitarSound scrape,
        FractionalPosition position, Fraction? notationDuration, int lastFret)
    {
        var anchor = level.Anchors.LastOrDefault(a => a.Position.CompareTo(position) <= 0)
                     ?? level.Anchors.FirstOrDefault();
        var towardNut = IsTowardNut(scrape);
        var shortScrape = notationDuration is { } d && d < QuarterNote;
        Place(scrape, anchor?.Fret ?? GpCapoFrets.MinFrettedFret(arrangement.Capo), towardNut, shortScrape,
            arrangement.Capo, lastFret);
    }

    /// <summary>
    ///     A plain template with the scrape chord's frets and NO fingers, the
    ///     arrangement's own when one exists (templates are shared and never
    ///     mutated), else a nameless mint.
    /// </summary>
    private static ChordTemplate FingerlessTemplate(Arrangement arrangement, Chord chord)
    {
        var frets = ChordTemplateResolver.FretsOf(chord);
        var existing = arrangement.ChordTemplates.FirstOrDefault(t =>
            !t.IsArpeggio && t.Fingers.Count == 0 && t.HasFrets(frets));
        if (existing != null) return existing;

        var template = new ChordTemplate
        {
            Name = string.Empty,
            Id = arrangement.ChordTemplates.Count == 0 ? 0 : arrangement.ChordTemplates.Max(t => t.Id) + 1
        };
        foreach (var (stringIndex, fret) in frets) template.Frets[stringIndex] = fret;
        arrangement.ChordTemplates.Add(template);
        return template;
    }

    private static void InsertByPosition(IList<GuitarNote> notes, GuitarNote note)
    {
        var index = notes.Count;
        while (index > 0 && notes[index - 1].Position.CompareTo(note.Position) > 0) index--;
        notes.Insert(index, note);
    }

    private static void InsertByPosition(IList<Chord> chords, Chord chord)
    {
        var index = chords.Count;
        while (index > 0 && chords[index - 1].Position.CompareTo(chord.Position) > 0) index--;
        chords.Insert(index, chord);
    }
}
