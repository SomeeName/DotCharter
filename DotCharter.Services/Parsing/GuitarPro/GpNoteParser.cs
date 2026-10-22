using System.Globalization;
using System.Xml.Linq;
using BendValue = DotCharter.Core.Domain.Guitar.BendValue;
using ChordNote = DotCharter.Core.Domain.Guitar.ChordNote;
using GuitarNote = DotCharter.Core.Domain.Guitar.GuitarNote;

namespace DotCharter.Services.Parsing.GuitarPro;

/// <summary>
///     Parses a single GP <c>Note</c> XML element into either a stand-alone
///     <see cref="GuitarNote"/> or a chord's <see cref="ChordNote"/>.
/// </summary>
public sealed class GpNoteParser
{
    private readonly NoteProperties _properties = new();

    /// <summary>
    ///     Trill data captured from the most recent <see cref="ParseNote"/> /
    ///     <see cref="ParseChordNote"/> call, or <c>null</c> if the note had no trill.
    ///     Rocksmith has no native trill, so the arrangement parser uses this to
    ///     expand trills into alternating HO/PO notes.
    /// </summary>
    public TrillInfo? LastTrill { get; private set; }

    /// <summary>
    ///     Whether the most recently parsed note's <see cref="IGuitarSound.StringIndex"/>
    ///     is inside the track's string count. A gpif note on a string the track
    ///     does not have is INERT — every post-import pass bounds-checks it away
    ///     (<c>GpPostProcessor.BuildSoundsByString</c> and friends), so keeping it
    ///     only leaves a phantom sound on the level that nothing can edit, render
    ///     or export. Callers must drop such a note, exactly as the GP3-5 binary
    ///     reader has always done (<c>GpLegacyImporter.ReadBeat</c>'s
    ///     <c>stringNo &lt;= track.StringCount</c> guard).
    /// </summary>
    public bool LastStringValid { get; private set; }

    /// <summary>
    ///     Whether the most recently parsed note carried GP's <c>LeftHandTapped</c>
    ///     property. Left-hand tapping is normalised to a hammer-on
    ///     (<see cref="HopoType.HammerOn"/>), but unlike an ordinary legato
    ///     hammer-on it is a fresh TAP-ON and must never be re-derived into a
    ///     pull-off when it lands on a lower fret (<c>GpPostProcessor.AdjustSustains</c>
    ///     flips descending hammer-ons to pull-offs). Such notes accumulate in
    ///     <see cref="LeftHandTappedNotes"/> so that pass can skip them (owner
    ///     report: a left-hand-tapped 3 after a 5 imported as a pull-off in an
    ///     all-hammer tapping run). RIGHT-hand tapping (GP <c>Tapped</c>) is a
    ///     different technique and stays <see cref="HopoType.Tap"/> — it is NOT in
    ///     this set and NOT turned into a hammer-on.
    /// </summary>
    public bool LastLeftHandTapped { get; private set; }

    /// <summary>
    ///     The AUTHORED left-hand finger of the most recently parsed note
    ///     (<c>&lt;Property name="LeftFingering"&gt;</c>), or <c>null</c> when the
    ///     file did not say. Guitar Pro carries it; ignoring it would leave <c>ChordTemplateFingerSetter</c> to guess. The caller knows the
    ///     note's chart position, so it — not this parser — records the value into
    ///     the per-track <see cref="GpAuthoredFingering" />.
    /// </summary>
    public int? LastLeftFingering { get; private set; }

    /// <summary>
    ///     Import-only set of the LEFT-hand-tapped notes parsed so far, by
    ///     reference identity — handed to <c>GpPostProcessor.Run</c> so the
    ///     descending-hammer-on → pull-off derivation skips them. The arrangement
    ///     parser clears it per track (via <see cref="ResetLeftHandTappedNotes"/>)
    ///     so each track's <c>Run</c> only sees its own; entries for notes on other
    ///     levels never match anyway (reference identity).
    /// </summary>
    public HashSet<IGuitarSound> LeftHandTappedNotes { get; } = [];

    /// <summary>Clears <see cref="LeftHandTappedNotes"/> — called once per track
    /// before its bars are walked, mirroring the per-track <see cref="Capo"/>
    /// reset.</summary>
    public void ResetLeftHandTappedNotes() => LeftHandTappedNotes.Clear();

    /// <summary>
    ///     The capo fret of the track currently being parsed. Guitar Pro writes
    ///     tab numbers RELATIVE to it while our model is ABSOLUTE, so every fret
    ///     read here is converted through <see cref="GpCapoFrets.ToAbsolute"/>
    ///     (see that class for the whole invariant). The arrangement parser sets
    ///     this from <c>Arrangement.Capo</c> before walking each track's bars —
    ///     the parser instance is reused across tracks and each GP track carries
    ///     its OWN capo, so it must be re-set per track, not once per file.
    ///     <para>
    ///     Doing the conversion HERE, at the single point a GP fret enters the
    ///     model, is what makes everything downstream correct by construction:
    ///     the slide-out targets computed from <c>note.Fret</c> below, the trill's
    ///     upper fret, grace ornaments (which parse through
    ///     <see cref="ParseNote"/> too), and then every consumer that reasons
    ///     about fret VALUES — chord templates, suggested fingering and names,
    ///     hand shapes and FHP anchor generation — all run on absolute frets.
    ///     </para>
    ///     Default <c>0</c> ⇒ no capo ⇒ a strict no-op.
    /// </summary>
    public int Capo { get; set; }

    public readonly record struct TrillInfo(int UpperFret);

    /// <summary>
    ///     Sentinel <see cref="IGuitarSound.SlideTo"/> for a GP "slide in from
    ///     below" ornament (<c>Slide</c> flag 16): the note is approached from a
    ///     LOWER fret that GP never names. Resolved into a real lead-in note by
    ///     <see cref="GpPostProcessor.MaterializeSlideIns"/>; it must never
    ///     survive import (<see cref="GpPostProcessor.LinkSlides"/> scrubs any
    ///     leftover negative slide target on the notation-only voices too).
    /// </summary>
    public const int SlideInFromBelow = -2;

    /// <summary>Sentinel for "slide in from above" (<c>Slide</c> flag 32) — the
    /// mirror of <see cref="SlideInFromBelow"/>.</summary>
    public const int SlideInFromAbove = -3;

    /// <summary>
    ///     Whether the node is the DESTINATION half of a tie — not a new strike
    ///     at all, but the continuation of a sound already ringing on that
    ///     string. (<c>Tie origin</c>, the other half, is what
    ///     <see cref="ApplyNoteAttributes" /> turns into
    ///     <see cref="IGuitarSound.LinkNext" />.) Readable WITHOUT parsing the
    ///     note so a caller can classify a whole beat before deciding which of
    ///     its notes to emit at all.
    /// </summary>
    public static bool IsTieDestination(XElement noteNode) =>
        noteNode.Element("Tie")?.Attribute("destination")?.Value == "true";

    /// <summary>
    ///     Whether the node is the ORIGIN half of a hammer-on or pull-off (GP's
    ///     <c>HopoOrigin</c> property; the destination note carries
    ///     <c>HopoDestination</c>, which <see cref="ApplyHopo" /> reads). Needed on its
    ///     own for a grace note: the grace is the sound the legato starts from.
    /// </summary>
    public static bool IsHopoOrigin(XElement noteNode) =>
        noteNode.Element("Properties")?.Elements("Property")
            .Any(p => p.Attribute("name")?.Value == "HopoOrigin") == true;

    /// <summary>
    ///     The node's GP string index (already DotCharter's low-pitch-first
    ///     convention, see <see cref="ApplyNoteAttributes" />), or <c>-1</c> when
    ///     the node names none. Same "classify before parsing" purpose as
    ///     <see cref="IsTieDestination" />.
    /// </summary>
    public static int ReadStringIndex(XElement noteNode)
    {
        var stringProp = noteNode.Element("Properties")?.Elements("Property")
            .FirstOrDefault(p => p.Attribute("name")?.Value == "String");
        return stringProp != null && int.TryParse(stringProp.Element("String")?.Value, CultureInfo.InvariantCulture, out var s) ? s : -1;
    }

    public GuitarNote ParseNote(XElement noteNode, FractionalPosition pos, FractionalPosition sustain,
        int stringCount, bool hasTremolo)
    {
        LastTrill = null;
        LastLeftHandTapped = false;
        LastLeftFingering = null;
        var note = new GuitarNote
        { Position = pos, Sustain = sustain, StringIndex = 0, Fret = 0, Tremolo = hasTremolo };
        ApplyNoteAttributes(noteNode, note, pos);
        TryCaptureTrill(noteNode, note.Fret);
        LastStringValid = (uint)note.StringIndex < (uint)stringCount;
        if (LastLeftHandTapped && LastStringValid) LeftHandTappedNotes.Add(note);
        return note;
    }

    public ChordNote ParseChordNote(XElement noteNode, FractionalPosition absolutePos, FractionalPosition sustain,
        int stringCount, bool hasTremolo)
    {
        LastTrill = null;
        LastLeftHandTapped = false;
        LastLeftFingering = null;
        var note = new ChordNote { Sustain = sustain, StringIndex = 0, Fret = 0, Tremolo = hasTremolo };
        ApplyNoteAttributes(noteNode, note, absolutePos);
        TryCaptureTrill(noteNode, note.Fret);
        LastStringValid = (uint)note.StringIndex < (uint)stringCount;
        if (LastLeftHandTapped && LastStringValid) LeftHandTappedNotes.Add(note);
        return note;
    }

    private void TryCaptureTrill(XElement noteNode, int baseFret)
    {
        var trillNode = noteNode.Element("Trill");
        if (trillNode == null) return;
        if (!int.TryParse(trillNode.Value, CultureInfo.InvariantCulture, out var trillMidi)) return;

        // GP stores the trill's upper note as a MIDI pitch. Derive the fret on the
        // same string by combining the base note's MIDI with the delta. Falls back
        // to a +2-fret trill if the Midi property is missing.
        var midiProp = noteNode.Element("Properties")?.Elements("Property")
            .FirstOrDefault(p => p.Attribute("name")?.Value == "Midi");
        var upperFret = baseFret + 2;
        if (midiProp != null && int.TryParse(midiProp.Element("Number")?.Value, CultureInfo.InvariantCulture, out var baseMidi))
            upperFret = baseFret + (trillMidi - baseMidi);

        if (upperFret < 0) upperFret = 0;
        if (upperFret > 24) upperFret = 24;
        if (upperFret == baseFret) return;

        LastTrill = new TrillInfo(upperFret);
    }

    /// <summary>
    ///     Maps the GP <c>Slide</c> bitmask onto a sound. 1 = shift slide and
    ///     2 = legato slide both target the NEXT sound on the string (resolved by
    ///     <see cref="GpPostProcessor.LinkSlides"/> from the <c>-1</c> marker);
    ///     4 / 8 are the unpitched slide-out down / up; 16 / 32 are the slide-IN
    ///     from below / above; 64 / 128 are the PICK SCRAPE down / up
    ///     (<see cref="GpPickScrape"/>).
    ///     <para>
    ///     A slide OUT (or to the next note) needs a fretted start: an authored
    ///     one on an OPEN string is a scrape/fall the fret hand cannot perform, and
    ///     importing it produces the inspector's L09
    ///     (<see cref="ArrangementIssueType.SlideFromOpenString"/>) on every save —
    ///     so the note survives and the ornament is dropped. A slide IN is exempt
    ///     from that rule: its origin is a SYNTHESIZED fret above/below the target,
    ///     so the fretted start lives on the materialized lead-in note rather than
    ///     on this one.
    ///     </para>
    /// </summary>
    private void ApplySlideFlags(IGuitarSound note, int flags)
    {
        // A PICK SCRAPE (64 down / 128 up) is the whole gesture: it outranks the
        // ordinary slide bits and is charted the Rocksmith way — see GpPickScrape.
        // It is exempt from the open-string rule because it MOVES the fret.
        if (GpPickScrape.IsPickScrape(flags))
        {
            GpPickScrape.Apply(note, flags, Capo);
            return;
        }

        if (note.Fret > 0)
        {
            if ((flags & 4) > 0)
            {
                note.UnpitchedSlide = true;
                // `note.Fret` is ABSOLUTE by now, so the scrape's landing must
                // floor at the CAPO — a hard 1 put it below the capo on every
                // capo'd track, which ChartExportValidator rejects as
                // KeyNoteBelowCapo (Error, blocks the export dialog). Capo 0
                // keeps the historical fret-1 floor exactly.
                note.SlideTo = Math.Max(GpCapoFrets.MinFrettedFret(Capo), note.Fret - 5);
            }

            if ((flags & 8) > 0)
            {
                note.UnpitchedSlide = true;
                note.SlideTo = Math.Min(GpCapoFrets.MaxAbsoluteFret, note.Fret + 5);
            }

            if ((flags & 1) > 0) note.SlideTo = -1;
            if ((flags & 2) > 0)
            {
                note.SlideTo = -1;
                note.LinkNext = true;
            }
        }

        // A slide-in only claims the note when no other slide already did — RS
        // can carry ONE slide per note, and an authored slide OUT of a note is
        // the more chart-significant of the two.
        if (note.SlideTo.HasValue) return;
        if ((flags & 16) > 0) note.SlideTo = SlideInFromBelow;
        else if ((flags & 32) > 0) note.SlideTo = SlideInFromAbove;
    }

    private void ApplyNoteAttributes(XElement noteNode, IGuitarSound note, FractionalPosition absolutePos)
    {
        ApplyDirectChildren(noteNode, note);

        var props = noteNode.Element("Properties");
        if (props == null) return;

        var found = _properties.Scan(props);
        ApplyStringAndFret(found, note);
        ApplyTechniques(found, note);

        if (found.Slide != null && int.TryParse(found.Slide.Element("Flags")?.Value, CultureInfo.InvariantCulture, out var flags))
            ApplySlideFlags(note, flags);

        if (found.Bended != null)
            ApplyBend(found.Bended, found, note, absolutePos);
    }

    private static void ApplyDirectChildren(XElement noteNode, IGuitarSound note)
    {
        var tieNode = noteNode.Element("Tie");
        if (tieNode != null && tieNode.Attribute("origin")?.Value == "true") note.LinkNext = true;

        if (noteNode.Element("Vibrato") != null) note.Vibrato = true;

        // GP exposes Accent as a direct child element (not Properties). It must
        // land on chord notes too — accents on strummed beats otherwise vanish.
        // Trill is handled separately via TryCaptureTrill (Rocksmith has no
        // native trill so we expand it into alternating HO/PO notes upstream).
        if (noteNode.Element("Accent") != null) note.Accent = true;
    }

    private void ApplyStringAndFret(NoteProperties found, IGuitarSound note)
    {
        if (found.String != null && int.TryParse(found.String.Element("String")?.Value, CultureInfo.InvariantCulture, out var s))
        {
            // GP6+ XML already stores the String index in DotCharter's
            // low-pitch-first convention (0 = low E), unlike the GP3-5
            // binary format which the legacy reader explicitly flips.
            note.StringIndex = s;
        }

        // GP's number is RELATIVE to the track capo; ours is absolute. Convert
        // at the read so everything below (slide targets, trill, graces) and
        // every downstream pass sees an absolute fret. Capo 0 ⇒ identity.
        if (found.Fret != null && int.TryParse(found.Fret.Element("Fret")?.Value, CultureInfo.InvariantCulture, out var f))
            note.Fret = GpCapoFrets.ToAbsolute(f, Capo);
    }

    private void ApplyTechniques(NoteProperties found, IGuitarSound note)
    {
        if (found.Vibrato != null) note.Vibrato = true;

        // The AUTHORED left-hand finger. GP has always written it and we
        // never looked, so every imported chord's fingering came from the
        // suggester's guess instead of from the file.
        if (found.Fingering != null)
        {
            LastLeftFingering = GpAuthoredFingering.MapGpifFinger(
                found.Fingering.Element("Fingering")?.Value ?? found.Fingering.Value);
        }

        // GP7+ writes slap and pop as note PROPERTIES. Neither reader looked,
        // so the only way slap/pop reached a chart was the manual "S"/"P"
        // beat-text override — and since the GP3-5 binary reader started
        // reading the string-effect byte (0.33.2) the two importers
        // disagreed, which is exactly what the legacy-vs-gpif parity rule
        // forbids: the binary reader is measured against THIS one. Matters
        // most on bass, where the technique is the point.
        if (found.Slapped) note.BassPicking = BassPickingType.Slap;
        else if (found.Popped) note.BassPicking = BassPickingType.Pop;

        ApplyHopo(found, note);

        if (found.Muted != null) note.Mute = MuteType.Full;

        if (found.PalmMuted != null) note.Mute = MuteType.Palm;

        // Guitar Pro writes a `Harmonic` switch beside `HarmonicType`; alphaTab's writer (and
        // so our own .gp export) writes only `HarmonicType` + `HarmonicFret`. Keying on the
        // switch alone would lose every harmonic of an exported chart on re-import.
        var hType = found.HarmonicType?.Element("HType")?.Value ?? "";
        if (found.Harmonic != null ||
            (found.HarmonicType != null && !string.Equals(hType, "NoHarmonic", StringComparison.OrdinalIgnoreCase)))
        {
            note.Harmonic = string.Equals(hType, "Pinch", StringComparison.OrdinalIgnoreCase)
                ? HarmonicType.Pinch
                : HarmonicType.Normal;
        }
    }

    // Left-hand tapping is functionally a hammer-on for charting purposes,
    // so we normalise it. Right-hand "Tapped" stays as Tap. A left-hand tap
    // is flagged so the descending-hammer-on → pull-off derivation skips it
    // (it is a fresh tap-on, not a legato pull-off — see LastLeftHandTapped).
    private void ApplyHopo(NoteProperties found, IGuitarSound note)
    {
        if (found.LeftHandTapped != null)
        {
            note.Hopo = HopoType.HammerOn;
            LastLeftHandTapped = true;
        }
        else if (found.Tapped != null)
        {
            note.Hopo = HopoType.Tap;
        }
        else if (found.HopoDestination != null)
        {
            note.Hopo = HopoType.HammerOn;
        }
    }

    private static void ApplyBend(XElement bended, NoteProperties found, IGuitarSound note, FractionalPosition absolutePos)
    {
        var bendNode = bended.Element("Bend");
        if (bendNode != null)
            AddBendPoints(bendNode, note, absolutePos);
        else
            AddBendFromProperties(found, note, absolutePos);

        GpBendUtilities.SortUniqueBends(note.BendValues);
    }

    private static void AddBendPoints(XElement bendNode, IGuitarSound note, FractionalPosition absolutePos)
    {
        var bendPoints = new List<(int Offset, int Value)>();
        var maxOffset = 0;
        foreach (var pt in bendNode.Elements("Point"))
        {
            var offset = int.TryParse(pt.Attribute("offset")?.Value ?? pt.Attribute("position")?.Value, CultureInfo.InvariantCulture,
                out var o)
                ? o
                : 0;
            var val = int.TryParse(pt.Attribute("value")?.Value ?? "0", CultureInfo.InvariantCulture, out var v) ? v : 0;
            bendPoints.Add((offset, val));
            if (offset > maxOffset) maxOffset = offset;
        }

        foreach (var point in bendPoints)
        {
            var step = point.Value / 50.0;
            var pct = maxOffset == 60 ? point.Offset / 60.0 * 100.0 : point.Offset;

            var bp = BendPosition(note, absolutePos, pct);
            if (!GpBendUtilities.ContainsBendAtPosition(note.BendValues, bp))
                note.BendValues.Add(new BendValue(bp, step));
        }
    }

    private static void AddBendFromProperties(NoteProperties found, IGuitarSound note, FractionalPosition absolutePos)
    {
        var originOffset = PropFloat(found.BendOriginOffset);
        var originValue = PropFloat(found.BendOriginValue);
        var midOffset1 = PropFloat(found.BendMiddleOffset1);
        var midOffset2 = PropFloat(found.BendMiddleOffset2);
        var midValue = PropFloat(found.BendMiddleValue);
        var destOffset = PropFloat(found.BendDestinationOffset);
        var destValue = PropFloat(found.BendDestinationValue);

        note.BendValues.Add(new BendValue(BendPosition(note, absolutePos, originOffset), originValue / 50.0));
        note.BendValues.Add(new BendValue(BendPosition(note, absolutePos, midOffset1), midValue / 50.0));
        if (Math.Abs(midOffset2 - midOffset1) > 0.001)
            note.BendValues.Add(new BendValue(BendPosition(note, absolutePos, midOffset2), midValue / 50.0));
        note.BendValues.Add(new BendValue(BendPosition(note, absolutePos, destOffset), destValue / 50.0));
    }

    private static double PropFloat(XElement? property)
    {
        if (property != null && double.TryParse(property.Element("Float")?.Value, NumberStyles.Any,
                CultureInfo.InvariantCulture, out var val))
        {
            return val;
        }

        return 0;
    }

    private static FractionalPosition BendPosition(IGuitarSound note, FractionalPosition absolutePos, double pct)
    {
        pct = Math.Max(0, Math.Min(100, pct));
        var totalSustain = new Fraction(note.Sustain.BeatId, 1) + note.Sustain.Fraction;
        var bpTotalFrac = totalSustain * new Fraction((int)Math.Round(pct), 100);
        var bBeats = bpTotalFrac.Numerator / bpTotalFrac.Denominator;
        var bFrac = new Fraction(bpTotalFrac.Numerator % bpTotalFrac.Denominator, bpTotalFrac.Denominator);
        return absolutePos + new FractionalPosition(bBeats, bFrac);
    }

    /// <summary>
    ///     The note's <c>&lt;Property&gt;</c> children, read in ONE pass. The parser
    ///     must not ask the same child list up to TWENTY-THREE separate
    ///     <c>Elements("Property").FirstOrDefault(name == "X")</c> questions PER NOTE,
    ///     each re-walking the list. <c>??=</c> keeps the FIRST element per name —
    ///     exactly what FirstOrDefault returned. <c>Bended</c>/<c>Bend</c> share a slot,
    ///     so the first of EITHER wins. One instance per parser is reused for every
    ///     note, so the scan allocates nothing.
    /// </summary>
    private sealed class NoteProperties
    {
        private static readonly Dictionary<string, Slot> SlotByName = new(StringComparer.Ordinal)
        {
            ["String"] = Slot.String,
            ["Fret"] = Slot.Fret,
            ["Vibrato"] = Slot.Vibrato,
            ["LeftFingering"] = Slot.Fingering,
            ["Slapped"] = Slot.Slapped,
            ["Popped"] = Slot.Popped,
            ["Tapped"] = Slot.Tapped,
            ["LeftHandTapped"] = Slot.LeftHandTapped,
            ["HopoDestination"] = Slot.HopoDestination,
            ["Muted"] = Slot.Muted,
            ["PalmMuted"] = Slot.PalmMuted,
            ["Harmonic"] = Slot.Harmonic,
            ["HarmonicType"] = Slot.HarmonicType,
            ["Slide"] = Slot.Slide,
            ["Bended"] = Slot.Bended,
            ["Bend"] = Slot.Bended,
            ["BendOriginOffset"] = Slot.BendOriginOffset,
            ["BendOriginValue"] = Slot.BendOriginValue,
            ["BendMiddleOffset1"] = Slot.BendMiddleOffset1,
            ["BendMiddleOffset2"] = Slot.BendMiddleOffset2,
            ["BendMiddleValue"] = Slot.BendMiddleValue,
            ["BendDestinationOffset"] = Slot.BendDestinationOffset,
            ["BendDestinationValue"] = Slot.BendDestinationValue,
        };

        private readonly XElement?[] _first = new XElement?[(int)Slot.Count];

        private enum Slot
        {
            String, Fret, Vibrato, Fingering, Slapped, Popped, Tapped, LeftHandTapped, HopoDestination,
            Muted, PalmMuted, Harmonic, HarmonicType, Slide, Bended,
            BendOriginOffset, BendOriginValue, BendMiddleOffset1, BendMiddleOffset2, BendMiddleValue,
            BendDestinationOffset, BendDestinationValue,
            Count,
        }

        public XElement? String => _first[(int)Slot.String];
        public XElement? Fret => _first[(int)Slot.Fret];
        public XElement? Vibrato => _first[(int)Slot.Vibrato];
        public XElement? Fingering => _first[(int)Slot.Fingering];
        public bool Slapped => _first[(int)Slot.Slapped] != null;
        public bool Popped => _first[(int)Slot.Popped] != null;
        public XElement? Tapped => _first[(int)Slot.Tapped];
        public XElement? LeftHandTapped => _first[(int)Slot.LeftHandTapped];
        public XElement? HopoDestination => _first[(int)Slot.HopoDestination];
        public XElement? Muted => _first[(int)Slot.Muted];
        public XElement? PalmMuted => _first[(int)Slot.PalmMuted];
        public XElement? Harmonic => _first[(int)Slot.Harmonic];
        public XElement? HarmonicType => _first[(int)Slot.HarmonicType];
        public XElement? Slide => _first[(int)Slot.Slide];
        public XElement? Bended => _first[(int)Slot.Bended];
        public XElement? BendOriginOffset => _first[(int)Slot.BendOriginOffset];
        public XElement? BendOriginValue => _first[(int)Slot.BendOriginValue];
        public XElement? BendMiddleOffset1 => _first[(int)Slot.BendMiddleOffset1];
        public XElement? BendMiddleOffset2 => _first[(int)Slot.BendMiddleOffset2];
        public XElement? BendMiddleValue => _first[(int)Slot.BendMiddleValue];
        public XElement? BendDestinationOffset => _first[(int)Slot.BendDestinationOffset];
        public XElement? BendDestinationValue => _first[(int)Slot.BendDestinationValue];

        public NoteProperties Scan(XElement props)
        {
            Array.Clear(_first);
            foreach (var property in props.Elements("Property"))
            {
                var name = property.Attribute("name")?.Value;
                if (name != null && SlotByName.TryGetValue(name, out var slot))
                    _first[(int)slot] ??= property;
            }

            return this;
        }
    }
}
