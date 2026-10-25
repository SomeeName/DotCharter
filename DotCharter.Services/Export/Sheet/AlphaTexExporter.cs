using System.Globalization;
using System.Text;

namespace DotCharter.Services.Export.Sheet;

/// <summary>
/// Converts a (project, arrangement, level, beats) snapshot into an AlphaTex
/// document that AlphaTab 1.8 can render with proper rhythm, ornaments and
/// measure layout.
///
/// Syntax conventions (per AlphaTab docs, verified against the bundled parser):
/// <list type="bullet">
///   <item>Staff metadata (<c>\tuning</c>, <c>\instrument</c>, <c>\capo</c>) comes right
///         after the song-metadata dot. <c>\instrument</c> also makes AlphaTab apply the
///         GP-style −12 display transposition, so guitar/bass notation reads an octave
///         up like every published tab book.</item>
///   <item>Bar metadata (<c>\ts num den</c>, <c>\tempo bpm</c>, <c>\clef</c>, <c>\ks</c>)
///         goes BEFORE the beats of the bar.</item>
///   <item>Each beat is <c>[:duration] content [{beatProps}]</c> with content being
///         <c>fret.string{noteProps}</c>, <c>(fret.string{...} ...)</c> or <c>r</c>.</item>
///   <item>Note ornaments (<c>h</c>, <c>p</c>, <c>sl</c>, <c>sou</c>, <c>nh</c>,
///         <c>v</c>/<c>vw</c>, <c>pm</c>, <c>lr</c>, <c>ac</c>, ...) are PER-NOTE props in <c>{}</c>.</item>
///   <item>Beat-level effects like dotted (<c>d</c>), triplet (<c>tu 3</c>) and tremolo
///         picking (<c>tp marks</c>) are AFTER content.</item>
///   <item>Tied notes use <c>-.string</c> (the dash acts as the fret).</item>
/// </list>
///
/// Rhythm model: all quantization runs on the EXACT fractional beat grid
/// (<see cref="FractionalPosition"/> = beat index + rational fraction), never on
/// millisecond round-trips — a tempo-mapped (non-uniform) beat grid therefore
/// cannot drift grid-aligned notes off the grid (the historical "dotted/tied
/// sliver" beaming killer). 48 ticks per quarter expresses 64ths AND triplets
/// (8th/16th/quarter triplets) exactly.
/// </summary>
public static class AlphaTexExporter
{
    // Quarter-note resolution = 48 ticks → 64ths (3) and triplets (16/8/32) are exact.
    private const int TicksPerQuarter = 48;

    /// <summary>
    /// Standard durations, longest first. <c>Tuplet</c> 3 = triplet (emitted as
    /// <c>{tu 3}</c>). No dotted triplets — a dotted triplet is numerically a plain
    /// binary value and would be ambiguous.
    /// </summary>
    private static readonly (int Ticks, int Denominator, bool Dotted, int Tuplet)[] StandardDurations =
    [
        (192, 1,  false, 0),
        (144, 2,  true,  0),
        (96,  2,  false, 0),
        (72,  4,  true,  0),
        (64,  2,  false, 3),
        (48,  4,  false, 0),
        (36,  8,  true,  0),
        (32,  4,  false, 3),
        (24,  8,  false, 0),
        (18,  16, true,  0),
        (16,  8,  false, 3),
        (12,  16, false, 0),
        (9,   32, true,  0),
        (8,   16, false, 3),
        (6,   32, false, 0),
        (4,   32, false, 3),
        (3,   64, false, 0),
        (2,   64, false, 3)
    ];

    // Standard-tuning MIDI pitches per string count, lowest string first —
    // the same tables the GP importer folds TuningOffset.Step over.
    private static int[] StandardMidiPitches(int stringCount, ArrangementType type) => stringCount switch
    {
        4 => [28, 33, 38, 43],                     // bass E1 A1 D2 G2
        5 => TuningUtilities.GetStandardMidiPitchesFor(5, type), // bass E1 A1 D2 G2 B2 (guitar: E2 .. B3)
        // 7-string B1 E2 A2 D3 G3 B3 E4, 8-string F#1 B1 E2 ..., 9-string C#1 F#1 ... — read from the one
        // table (Core/Utilities/StandardOpenStrings) the importers compute their
        // offsets against and the tuning chips print. An 8-string must NOT fall to the
        // `_` six-string row: the `Math.Min(domainString, Length - 1)` clamp in
        // AppendStaffMetadata then handed both extra low strings the top E4.
        // Arrangement.StringCount tops out at 9 (StandardOpenStrings.MaxStringCount).
        // 5 is E A D G B (an octave down for a bass): the app reads 5 strings that way and
        // the GP importers subtract it, so a B E A D G bass carries offsets -5 -5 -5 -5 -4.
        >= 7 => TuningUtilities.GetStandardMidiPitches(Math.Min(stringCount, StandardOpenStrings.MaxStringCount)),
        _ => [40, 45, 50, 55, 59, 64]              // guitar E2 A2 D3 G3 B3 E4
    };

    private static readonly string[] SharpNames = ["c", "c#", "d", "d#", "e", "f", "f#", "g", "g#", "a", "a#", "b"];

    public static ExportResult Export(
        ChartProject? project,
        Arrangement? arrangement,
        ChartLevel? level,
        IList<BeatMarker> beats)
    {
        var sb = new StringBuilder(16384);
        var title = project?.Chart.SongName ?? string.Empty;
        var artist = project?.Chart.Artist ?? string.Empty;
        var stringCount = arrangement?.StringCount ?? 6;
        var measureSpans = new List<MeasureSpan>();
        var beatMap = new List<SheetBeat>();

        if (!string.IsNullOrEmpty(title))
            sb.Append("\\title \"").Append(Escape(title)).Append("\" ");
        if (!string.IsNullOrEmpty(artist))
            sb.Append("\\subtitle \"").Append(Escape(artist)).Append("\" ");

        if (arrangement == null || level == null || beats.Count == 0)
        {
            sb.Append('.').Append(' ').Append(":4 r");
            return new ExportResult(sb.ToString(), measureSpans, beatMap);
        }

        sb.Append('.').Append(' ');

        // --- Staff metadata: real tuning, capo, instrument (pitch + the GP-style
        // display transposition side effect). Without these a bass chart rendered
        // with AlphaTab's guitar defaults shows entirely wrong pitches.
        AppendStaffMetadata(sb, arrangement, stringCount);

        var isBass = IsBassArrangement(arrangement, stringCount);
        // Frets are emitted through this map, never raw — see StaffMap for why a
        // capoed arrangement must emit CAPO-RELATIVE frets.
        var staff = new StaffMap(stringCount, arrangement.Capo);

        var measures = BuildMeasures(beats);
        if (measures.Count == 0)
        {
            sb.Append(":4 r");
            return new ExportResult(sb.ToString(), measureSpans, beatMap);
        }

        // Notation-only sidecar voices (GP voices 2–4) render as additional
        // alphaTex voices on the same staff. Contract with the bundled parser
        // (probe-verified): when a staff is multi-voice, EVERY voice — the
        // first included — is prefixed with \voice, and each voice lists ALL
        // its bars in sequence. Without the leading marker a single-bar first
        // voice makes the parser read voice 2 as bar 2. Single-voice charts
        // emit no \voice token at all, keeping their output byte-identical.
        var sidecars = new List<NotationVoice>();
        foreach (var voice in arrangement.NotationVoices.OrderBy(v => v.VoiceIndex))
        {
            if (voice.Notes.Count > 0 || voice.Chords.Count > 0)
                sidecars.Add(voice);
        }

        if (sidecars.Count > 0) sb.Append("\\voice ");

        var slots = BuildSlots(arrangement, level.Notes, level.Chords, measures, resolveChordNames: true);
        var state = new EmitState();
        EmitVoiceBars(sb, project, measures, slots, staff, isBass, state, beatMap, measureSpans,
            emitBarMeta: true);

        foreach (var voice in sidecars)
        {
            sb.Append("\\voice ");
            var voiceSlots = BuildSlots(arrangement, voice.Notes, voice.Chords, measures,
                // Chord names annotate the harmony once — repeating them per
                // voice would double every label on the staff.
                resolveChordNames: false);
            // Fresh emit/carry state per voice: durations, tie trails and the
            // bar carry never leak between voices. The beat map records ONLY
            // voice 0 — it is the click→domain bridge, and the renderer
            // filters its bounds to voice 0 to match (sidecar glyphs are not
            // click targets).
            EmitVoiceBars(sb, project, measures, voiceSlots, staff, isBass, new EmitState(),
                [], measureSpans: null, emitBarMeta: false);
        }

        if (!state.HasContent)
        {
            // Only metadata and no BARS AT ALL — the importer needs at least one
            // beat somewhere to produce a renderable score.
            //
            // This is reachable only when the measure walk emitted nothing, which
            // is why `EmitRests` marks content too: a level with beats but no
            // sounds emits whole-bar RESTS, and appending a quarter rest after
            // them landed it INSIDE the last bar — 5 beats of content in a 4/4
            // bar (measured: 4800 ticks for a 3840-tick bar), the exact overfill
            // the tick grid exists to prevent, on every chart whose active
            // difficulty level is still empty.
            sb.Append(":4 r");
        }

        return new ExportResult(sb.ToString(), measureSpans, beatMap);
    }

    /// <summary>
    /// Emits one voice's complete bar sequence. The first (chart) voice owns
    /// the bar metadata — clef, key signature, time signature and tempo are
    /// master-bar state and must appear exactly once; sidecar voices suppress
    /// them. Every voice walks ALL measures (empty ones emit whole-bar rests),
    /// so multi-voice output always has equal bar counts per voice.
    /// </summary>
    private static void EmitVoiceBars(
        StringBuilder sb,
        ChartProject? project,
        List<Measure> measures,
        List<Slot> slots,
        StaffMap staff,
        bool isBass,
        EmitState state,
        List<SheetBeat> beatMap,
        List<MeasureSpan>? measureSpans,
        bool emitBarMeta)
    {
        var lastBpm = double.NaN;
        var lastNumerator = -1;
        var lastDenominator = -1;
        // Carries a note/chord that is still sounding at a barline so the next
        // bar renders it as a tie instead of opening with rests.
        var carry = new TieState();

        for (var mi = 0; mi < measures.Count; mi++)
        {
            var m = measures[mi];
            measureSpans?.Add(new MeasureSpan(m.StartMs, m.EndMs, m.FirstBeatIndex, m.Numerator, m.TotalTicks));

            if (mi > 0) sb.Append("| ");

            if (emitBarMeta)
            {
                if (mi == 0)
                {
                    sb.Append("\\clef ").Append(isBass ? "f4" : "g2").Append(' ');
                    var ks = KeySignatureName(project?.Chart.KeyAccidentals ?? 0);
                    if (ks != null) sb.Append("\\ks ").Append(ks).Append(' ');
                }

                if (m.Numerator != lastNumerator || m.Denominator != lastDenominator)
                {
                    sb.Append("\\ts (").Append(m.Numerator).Append(' ').Append(m.Denominator).Append(") ");
                    lastNumerator = m.Numerator;
                    lastDenominator = m.Denominator;
                }
                if (SheetTempoMarking.IsNew(lastBpm, m.Bpm))
                {
                    sb.Append("\\tempo (")
                      .Append(SheetTempoMarking.Engraved(m.Bpm).ToString(CultureInfo.InvariantCulture))
                      .Append(") ");
                    lastBpm = m.Bpm;
                }
            }

            EmitMeasure(sb, measures, mi, slots, staff, carry, state, beatMap);
        }
    }

    /// <summary>Measure span + grid identity, in the order the bars are emitted.</summary>
    public readonly record struct MeasureSpan(
        double StartMs, double EndMs, int FirstBeatIndex = 0, int BeatCount = 4, int TotalTicks = 192);

    public enum SheetBeatKind { Head, TieContinuation, Rest }

    /// <summary>
    /// One emitted alphaTex beat of the CHART voice (voice 0), in emission
    /// order. AlphaTab preserves that order per bar/voice, so <c>(bar index,
    /// beat index within bar)</c> from its BoundsLookup indexes straight into
    /// this list — the bridge that lets the sheet strip map a clicked glyph
    /// back to the exact domain note/chord. Sidecar notation voices are NOT
    /// recorded here; <see cref="SheetMusicRenderer"/> filters its beat bounds
    /// to voice 0 so the two stay 1:1.
    /// </summary>
    public sealed record SheetBeat(
        int MeasureIndex,
        SheetBeatKind Kind,
        int StartTick,
        int DurationTicks,
        IReadOnlyList<GuitarNote> Notes,
        IReadOnlyList<Chord> Chords);

    public sealed record ExportResult(
        string AlphaTex,
        IReadOnlyList<MeasureSpan> MeasureSpans,
        IReadOnlyList<SheetBeat> BeatMap);

    private static bool IsBassArrangement(Arrangement arrangement, int stringCount) =>
        ArrangementInstrument.IsBassPart(arrangement) || stringCount <= 5;

    private static void AppendStaffMetadata(StringBuilder sb, Arrangement arrangement, int stringCount)
    {
        var standard = StandardMidiPitches(stringCount, arrangement.ArrangementType);
        // Through TuningOffsetResolution, not TuningPitches directly. A chart
        // states its tuning TWICE — per-string OFFSETS and note LETTERS — and an
        // arrangement carrying only the letters has an EMPTY offsets vector, so
        // reading the offsets with a 0 default engraved it as E STANDARD. That is
        // the exact loss the resolution was introduced for ("silently, in four
        // places at once"); RsXmlSongWriter, RscpWriter, EofExporter and
        // GpExporter all went through it and the sheet was the straggler. A
        // Drop-D chart then engraved every low-string pitch a tone sharp while
        // the PDF's own tuning legend — which reads the LETTERS — printed
        // "Tuning: D A D G B E" on the same page.
        var offsets = TuningOffsetResolution.Resolve(arrangement);
        sb.Append("\\tuning");
        // AlphaTex lists strings HIGHEST first (string 1); octaves are scientific
        // (g2 = MIDI 43 — verified against the bundled parser).
        for (var domainString = stringCount - 1; domainString >= 0; domainString--)
        {
            var midi = standard[Math.Min(domainString, standard.Length - 1)];
            if (domainString < offsets.Count)
                midi += offsets[domainString];
            // A220 (a 5/6-string bass on a guitar path) sounds an octave down.
            midi += ArrangementInstrument.ReferenceOctaveSemitones(arrangement.CentOffset);
            sb.Append(' ').Append(MidiToTexNote(midi));
        }
        sb.Append(' ');

        // \instrument sets the MIDI program AND makes AlphaTab apply the standard
        // −12 display transposition for guitar/bass staves (notation reads an
        // octave above sounding pitch, as in GP and every tab book).
        sb.Append("\\instrument ").Append(IsBassArrangement(arrangement, stringCount) ? 33 : 27).Append(' ');

        if (arrangement.Capo > 0)
            sb.Append("\\capo ").Append(arrangement.Capo.ToString(CultureInfo.InvariantCulture)).Append(' ');
    }

    private static string MidiToTexNote(int midi)
    {
        if (midi < 0) midi = 0;
        var name = SharpNames[midi % 12];
        var octave = midi / 12 - 1; // scientific: C4 = 60
        return name + octave.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Maps <see cref="ChartData.KeyAccidentals"/> (−7 flats … +7 sharps) to the
    /// alphaTex major key name with that signature (the glyphs are identical for
    /// the relative minor). 0 → null: C major is AlphaTab's default, don't emit.
    /// </summary>
    private static string? KeySignatureName(int accidentals)
    {
        return Math.Clamp(accidentals, -7, 7) switch
        {
            1 => "g",
            2 => "d",
            3 => "a",
            4 => "e",
            5 => "b",
            6 => "f#",
            7 => "c#",
            -1 => "f",
            -2 => "bb",
            -3 => "eb",
            -4 => "ab",
            -5 => "db",
            -6 => "gb",
            -7 => "cb",
            _ => null
        };
    }

    // ---------------------------------------------------------------------
    // Slot building — exact fractional positions → measure-local ticks.
    // ---------------------------------------------------------------------

    /// <summary>One emitted onset: every note/chord that struck on this tick.</summary>
    private sealed class Slot
    {
        public int MeasureIndex;
        public int Tick;
        public readonly List<GuitarNote> Notes = [];
        public readonly List<Chord> Chords = [];
        public string? ChordName;

        /// <summary>String index → left-hand finger (domain 0..4 = T,1,2,3,4) from
        /// the chord template, null when the slot has no templated chord.</summary>
        public Dictionary<int, int>? Fingers;

        /// <summary>Authored notation duration in ticks (score-import hint),
        /// 0 = none. When present it WINS over both the sustain-derived and the
        /// beat-fill duration — the imported tab renders as authored.</summary>
        public int HintTicks;

        /// <summary>Sustain end (position + max member sustain); equals the onset
        /// position for zero-sustain sounds.</summary>
        public FractionalPosition EndPosition;
        public FractionalPosition Position;
    }

    private static List<Slot> BuildSlots(Arrangement arrangement, IList<GuitarNote> notes, IList<Chord> chords,
        List<Measure> measures, bool resolveChordNames)
    {
        var templatesById = BuildTemplateIndex(arrangement);
        var nameCache = new Dictionary<int, string?>();

        var events = new List<(FractionalPosition Pos, GuitarNote? Note, Chord? Chord)>(
            notes.Count + chords.Count);
        foreach (var n in notes) events.Add((n.Position, n, null));
        foreach (var c in chords) events.Add((c.Position, null, c));
        events.Sort(static (a, b) => a.Pos.CompareTo(b.Pos));

        var slots = new List<Slot>(events.Count);
        foreach (var (pos, note, chord) in events)
        {
            var mi = MeasureIndexOf(measures, pos);
            if (mi < 0) continue; // before the grid — nothing sensible to render
            var m = measures[mi];
            var tick = SnapTick(TickOf(m, pos), m);
            if (tick >= m.TotalTicks)
            {
                // Rounded onto the next barline — belongs to the next bar.
                if (mi + 1 < measures.Count) { mi++; m = measures[mi]; tick = 0; }
                else
                {
                    tick = m.TotalTicks - MinTicks(m);
                }
            }

            // Merge onsets that land on (or within a hair of) an existing slot —
            // two single notes struck together read as one beat (an interval /
            // chord), exactly like GP. The old exporter silently DROPPED all but
            // the first event on a tick. The 3-tick threshold (a 64th) also
            // guarantees emitted gaps stay expressible, so bars never overfill.
            Slot? slot = null;
            if (slots.Count > 0)
            {
                var last = slots[^1];
                if (last.MeasureIndex == mi && Math.Abs(last.Tick - tick) < 3) slot = last;
            }
            if (slot == null)
            {
                slot = new Slot { MeasureIndex = mi, Tick = tick, Position = pos, EndPosition = pos };
                slots.Add(slot);
            }

            if (note != null)
            {
                slot.Notes.Add(note);
                var end = note.Position + note.Sustain;
                if (end > slot.EndPosition) slot.EndPosition = end;
                if (note.NotationDuration is { } nd)
                    slot.HintTicks = Math.Max(slot.HintTicks, HintToTicks(nd));
            }
            if (chord != null)
            {
                slot.Chords.Add(chord);
                if (resolveChordNames)
                    slot.ChordName ??= ResolveChordName(chord, arrangement, templatesById, nameCache);
                // Left-hand fingering lives on the TEMPLATE, not the chord note,
                // so it has to be resolved here where the template index is in
                // scope. Emitted per note as `lf N` (see AppendNoteProps).
                if (slot.Fingers == null && chord.TemplateId >= 0 &&
                    templatesById.TryGetValue(chord.TemplateId, out var fingerTpl) &&
                    fingerTpl.Fingers.Count > 0)
                {
                    slot.Fingers = fingerTpl.Fingers;
                }

                var end = chord.Position + chord.GetMaxSustain();
                if (end > slot.EndPosition) slot.EndPosition = end;
                if (chord.NotationDuration is { } nd)
                    slot.HintTicks = Math.Max(slot.HintTicks, HintToTicks(nd));
            }
        }

        return slots;
    }

    private static int MeasureIndexOf(List<Measure> measures, FractionalPosition pos)
    {
        // Binary search over FirstBeatIndex ranges; positions past the last
        // measure clamp into it.
        int lo = 0, hi = measures.Count - 1;
        if (pos.BeatId < measures[0].FirstBeatIndex) return pos.BeatId >= 0 ? 0 : -1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (pos.BeatId >= measures[mid].FirstBeatIndex) lo = mid;
            else hi = mid - 1;
        }
        return lo;
    }

    /// <summary>
    /// Measure-local tick from the EXACT fractional position — pure beat-grid
    /// arithmetic, no milliseconds involved, so a tempo-mapped grid cannot
    /// introduce rounding noise for grid-aligned notes.
    /// </summary>
    private static int TickOf(Measure m, FractionalPosition pos)
    {
        var beatsIn = pos.BeatId - m.FirstBeatIndex + pos.Fraction.DoubleValue;
        var tick = (int)Math.Round(beatsIn * m.TicksPerBeat);
        return Math.Clamp(tick, 0, m.TotalTicks);
    }

    /// <summary>Smallest emittable chunk in this measure (a 64th).</summary>
    private static int MinTicks(Measure m) => Math.Max(2, TicksPerQuarter / 16);

    /// <summary>Rounds a tick to the nearest multiple of <paramref name="step"/>, clamped into the bar.</summary>
    private static int QuantizeToGrid(int tick, int step, int totalTicks)
    {
        var q = (int)Math.Round(tick / (double)step) * step;
        return Math.Clamp(q, 0, totalTicks);
    }

    /// <summary>Authored notation duration (quarter-note units) → ticks.</summary>
    private static int HintToTicks(Fraction hint)
    {
        if (hint.Numerator <= 0) return 0;
        return (int)Math.Round(hint.Numerator * (double)TicksPerQuarter / hint.Denominator);
    }

    /// <summary>
    /// Absorbs ±1-tick rounding noise (ms-imported charts store positions on a
    /// 1/192-of-a-beat grid that doesn't always divide the tick grid) by snapping
    /// onsets onto the nearest 16th / 16th-triplet / 32nd line when they sit
    /// within one tick of it. True 32nd/64th/triplet positions are farther from
    /// the neighbouring lines than that and survive untouched.
    /// </summary>
    private static int SnapTick(int tick, Measure m)
    {
        var tpb = m.TicksPerBeat;
        foreach (var div in stackalloc[] { 4, 6, 8 })
        {
            var step = tpb / div;
            if (step < 2) break;
            var nearest = (int)Math.Round(tick / (double)step) * step;
            if (Math.Abs(nearest - tick) <= 1)
                return Math.Clamp(nearest, 0, m.TotalTicks);
        }
        return tick;
    }

    // ---------------------------------------------------------------------
    // Emission
    // ---------------------------------------------------------------------

    private sealed class EmitState
    {
        public bool HasContent;
        public int LastDuration;
        public string? LastChordName;
        // Running index into the global slot list — slots are ordered, so each
        // measure resumes where the previous one stopped.
        public int SlotCursor;
        // Per-string trail of the last emitted sound, for LinkNext → tie rendering.
        public readonly Dictionary<int, (int Fret, bool LinkNext, bool Slide)> LastByString = [];
    }

    private static void EmitMeasure(
        StringBuilder sb,
        List<Measure> measures,
        int measureIndex,
        List<Slot> slots,
        StaffMap staff,
        TieState carry,
        EmitState state,
        List<SheetBeat> beatMap)
    {
        var m = measures[measureIndex];
        var totalTicks = m.TotalTicks;
        var cursor = 0;

        // (1) A note still sounding at the previous barline continues here as a
        // tie, up to where its sustain really ends (never merely to the next
        // onset — the old gap-fill rendered a short bass hit before a 30-bar
        // break as 30 tied whole notes).
        if (carry.Active)
        {
            // Ring-out ends quantize to the 16th grid — a tie tail ending at an
            // arbitrary 1/192 position renders as a dotted+tied glyph chain no
            // engraver would ever write.
            var holdTick = QuantizeToGrid(
                Math.Clamp(TickOf(m, carry.HeldUntil), MinTicks(m), totalTicks),
                Math.Max(2, m.TicksPerBeat / 4), totalTicks);
            if (holdTick < MinTicks(m)) holdTick = MinTicks(m);
            EmitTiedContinuation(sb, carry.Slot!, staff, holdTick, state, measureIndex, 0, beatMap);
            cursor = holdTick;
            state.HasContent = true;

            if (holdTick >= totalTicks && MeasureIndexOf(measures, carry.HeldUntil) > measureIndex)
                return; // still holding through the entire bar — keep the carry alive
            carry.Active = false;
        }

        for (var s = state.SlotCursor; s < slots.Count; s++)
        {
            var slot = slots[s];
            if (slot.MeasureIndex > measureIndex) break;

            // No expressible room left in this bar (2 ticks is the shortest
            // duration that exists). Hand the onset to the NEXT bar instead of
            // squeezing it in: the duration floor below is MinTicks, so forcing
            // it here wrote past the barline. Leaving state.SlotCursor pointing
            // AT this slot is what re-offers it to the next measure.
            if (slot.MeasureIndex == measureIndex && totalTicks - cursor < 2 &&
                measureIndex + 1 < measures.Count)
            {
                slot.MeasureIndex = measureIndex + 1;
                slot.Tick = 0;
                break;
            }

            state.SlotCursor = s + 1;
            if (slot.MeasureIndex < measureIndex) continue; // swallowed by an overfilled predecessor

            // A gap shorter than the shortest duration that exists (2 ticks =
            // 1/96 of a beat) is import noise, not a rest. Emitting one made
            // EmitRests round UP to a 2-tick chunk and overfill the bar; absorb
            // it instead and let the head start at the cursor.
            if (slot.Tick - cursor >= 2)
            {
                EmitRests(sb, m, measureIndex, cursor, slot.Tick - cursor, state, beatMap);
                cursor = slot.Tick;
            }
            // slot.Tick < cursor: overlapped by the previous sound's sustain —
            // the head simply starts where the previous one ended.

            // Gap to the next onset, capped at the barline.
            var next = s + 1 < slots.Count ? slots[s + 1] : null;
            var gapEnd = next != null && next.MeasureIndex == measureIndex ? next.Tick : totalTicks;
            var gapTicks = Math.Max(MinTicks(m), gapEnd - cursor);

            // Head duration, in priority order:
            // (1) An AUTHORED notation duration (score-import hint) wins — the
            //     imported tab renders exactly as written (staccato + rests,
            //     tuplets, dotted values), still capped by the gap so edited/
            //     moved notes can't overlap the next onset.
            // (2) Otherwise the LONGER of the sustain (quantized to the 16th
            //     grid — sub-16th ring-out precision is chart noise, and
            //     honoring e.g. a 40 ms EOF sustain literally turned every
            //     eighth run into 64th-note + micro-rest confetti) and a fill
            //     to the next beat line — dense runs read as clean uniform
            //     durations while a lone hit before a long gap becomes
            //     note-then-RESTS instead of a fake multi-bar hold.
            // Only a duration that REALLY runs past the barline may tie across.
            var tpb = m.TicksPerBeat;
            var fillTicks = tpb - cursor % tpb; // to the next beat line (a full beat when on one)
            var sustained = slot.EndPosition > slot.Position;
            var authoredTicks = 0;
            if (sustained)
            {
                var sustainEndsBeyondBar = MeasureIndexOf(measures, slot.EndPosition) > measureIndex ||
                                           TickOf(m, slot.EndPosition) >= totalTicks;
                var endTick = sustainEndsBeyondBar
                    ? totalTicks
                    : QuantizeToGrid(TickOf(m, slot.EndPosition), Math.Max(2, tpb / 4), totalTicks);
                authoredTicks = endTick - cursor;
            }

            int durTicks;
            if (slot.HintTicks > 0)
            {
                // A hint longer than the remaining bar renders to the barline
                // here; the sustain-based carry below owns any cross-bar tie
                // (merged tied GP beats carry their summed length as sustain).
                durTicks = Math.Min(slot.HintTicks, totalTicks - cursor);
            }
            else
            {
                durTicks = Math.Max(authoredTicks, Math.Min(fillTicks, gapTicks));
            }
            durTicks = Math.Clamp(durTicks, MinTicks(m), gapTicks);
            // A duration ending a hair before the next onset is a release, not
            // a rest — absorb sub-32nd remainders into the head.
            if (gapTicks - durTicks is > 0 and < (TicksPerQuarter / 8))
                durTicks = gapTicks;
            // LAST word, after every widening rule above: a head may never write
            // past the barline. gapTicks floors at MinTicks, so a slot starting
            // within a 64th of the barline would otherwise emit a 3-tick head into a
            // 2-tick bar remainder and push the bar over its time signature.
            durTicks = Math.Min(durTicks, totalTicks - cursor);

            EmitTimedEvent(sb, slot, next, staff, durTicks, state, measureIndex, cursor, beatMap);
            cursor += durTicks;
            state.HasContent = true;

            // (2) Carry across the barline only when the sustain REALLY runs on
            // (by at least a 64th — a hair over the barline is a release, not a
            // tie) and no other onset interrupts it before the barline.
            if (sustained && gapEnd == totalTicks && cursor >= totalTicks &&
                (next == null || next.MeasureIndex > measureIndex) &&
                measureIndex + 1 < measures.Count)
            {
                var heldUntil = slot.EndPosition;
                if (next != null && next.Position < heldUntil) heldUntil = next.Position;
                var nextMeasure = measures[measureIndex + 1];
                if (MeasureIndexOf(measures, heldUntil) > measureIndex &&
                    TickOf(nextMeasure, heldUntil) >= MinTicks(nextMeasure))
                {
                    carry.Active = true;
                    carry.Slot = slot;
                    carry.HeldUntil = heldUntil;
                }
            }
        }

        if (cursor < totalTicks)
            EmitRests(sb, m, measureIndex, cursor, totalTicks - cursor, state, beatMap);
    }

    /// <summary>
    /// Emits a run of tied continuation heads (<c>-.string</c>) totalling
    /// <paramref name="ticks"/> ticks — the "downstream" half of a note that was
    /// struck in an earlier bar and ties across the barline.
    /// </summary>
    private static void EmitTiedContinuation(
        StringBuilder sb, Slot slot, StaffMap staff, int ticks, EmitState state,
        int measureIndex, int startTick, List<SheetBeat> beatMap)
    {
        var cursor = startTick;
        var remaining = ticks;
        while (remaining > 0)
        {
            var (chunk, denom, dotted, tuplet) = ChooseChunk(remaining);
            EmitDuration(sb, denom, state);
            AppendTiedHead(sb, slot, staff);
            AppendBeatProps(sb, dotted, tuplet, tremolo: false, chordName: null);
            sb.Append(' ');
            // One beat-map entry per emitted alphaTex beat — AlphaTab's bounds
            // lookup indexes beats per bar, and this list must stay 1:1 with it.
            beatMap.Add(new SheetBeat(measureIndex, SheetBeatKind.TieContinuation, cursor, chunk,
                slot.Notes, slot.Chords));
            cursor += chunk;
            remaining -= chunk;
        }
    }

    /// <summary>
    /// Rests are split on beat boundaries (pad to the next beat line first, then
    /// whole-beat chunks merged into halves/wholes on aligned boundaries) — a
    /// 3-beat gap starting on beat 2 must not render as one dotted-half rest
    /// obscuring the meter.
    /// </summary>
    private static void EmitRests(
        StringBuilder sb, Measure m, int measureIndex, int startTick, int ticks, EmitState state,
        List<SheetBeat> beatMap)
    {
        var cursor = startTick;
        var remaining = ticks;
        var tpb = m.TicksPerBeat;

        // A bar of rests IS content: the document has a beat in it. Without this
        // the Export-level "nothing was emitted" fallback fired for a level with
        // no sounds and appended a quarter rest AFTER the last bar's whole rest,
        // overfilling that bar.
        if (remaining > 0) state.HasContent = true;

        while (remaining > 0)
        {
            int chunkLimit;
            var beatOffset = cursor % tpb;
            if (beatOffset != 0)
            {
                chunkLimit = Math.Min(remaining, tpb - beatOffset);
            }
            else
            {
                var beatIndex = cursor / tpb;
                var wholeBeatsLeft = remaining / tpb;
                if (cursor == 0 && remaining == m.TotalTicks)
                    chunkLimit = remaining; // empty bar → single biggest rest(s)
                else if (beatIndex % 2 == 0 && wholeBeatsLeft >= 2)
                    chunkLimit = 2 * tpb;   // aligned half-bar rest
                else if (wholeBeatsLeft >= 1)
                    chunkLimit = tpb;
                else
                    chunkLimit = remaining;
            }

            // Same rule ChooseChunk enforces one level down: never leave a tail
            // that no duration expresses. A beat-line pad of 1 tick, or a split
            // that would leave 1 behind, rounded up and overfilled the bar.
            if (chunkLimit < 2 || remaining - chunkLimit == 1) chunkLimit = remaining;

            var emitted = 0;
            while (emitted < chunkLimit)
            {
                var (chunk, denom, dotted, tuplet) = ChooseChunk(chunkLimit - emitted);
                EmitDuration(sb, denom, state);
                sb.Append('r');
                AppendBeatProps(sb, dotted, tuplet, tremolo: false, chordName: null);
                sb.Append(' ');
                beatMap.Add(new SheetBeat(measureIndex, SheetBeatKind.Rest, cursor + emitted, chunk,
                    Array.Empty<GuitarNote>(), Array.Empty<Chord>()));
                emitted += chunk;
            }

            cursor += chunkLimit;
            remaining -= chunkLimit;
        }
    }

    private static void EmitTimedEvent(
        StringBuilder sb,
        Slot slot,
        Slot? next,
        StaffMap staff,
        int durationTicks,
        EmitState state,
        int measureIndex,
        int startTick,
        List<SheetBeat> beatMap)
    {
        // A before-beat grace ornament renders as its own beat AHEAD of the
        // head. Grace beats consume NO bar time in AlphaTab (parser-verified:
        // a full 4/4 bar takes a grace beat without spilling), so the tick
        // cursor is untouched — and they are deliberately NOT recorded in the
        // beat map (the renderer filters GraceType out of its bounds, keeping
        // the per-bar click zip 1:1).
        AppendGraceBeat(sb, slot, staff, state);

        // Split into a chain of standard durations tied together so a long
        // sustain renders as e.g. dotted-half + tied eighth instead of a
        // rounded glyph.
        var first = true;
        var cursor = startTick;
        var remaining = durationTicks;
        while (remaining > 0)
        {
            var (chunk, denom, dotted, tuplet) = ChooseChunk(remaining);
            EmitDuration(sb, denom, state);
            string? chordName = null;
            var tremolo = false;
            if (first)
            {
                AppendBeatHead(sb, slot, next, staff, state);
                // Chord names annotate the staff only when the harmony CHANGES —
                // one label per beat drowned the notation in repeated names.
                if (slot.ChordName != null && slot.ChordName != state.LastChordName)
                {
                    chordName = slot.ChordName;
                    state.LastChordName = slot.ChordName;
                }
                tremolo = HasTremolo(slot);
            }
            else
            {
                AppendTiedHead(sb, slot, staff);
            }

            AppendBeatProps(sb, dotted, tuplet, tremolo, chordName);
            sb.Append(' ');

            beatMap.Add(new SheetBeat(measureIndex,
                first ? SheetBeatKind.Head : SheetBeatKind.TieContinuation,
                cursor, chunk, slot.Notes, slot.Chords));
            cursor += chunk;
            remaining -= chunk;
            first = false;
        }
    }

    /// <summary>
    /// Emits the slot's grace ornament as ONE 16th before-beat grace beat, if
    /// anything in the slot carries a grace hint.
    /// <para>Single notes contribute <see cref="GuitarNote.GraceFret"/> and keep
    /// their long-standing "one ornament per beat, first hint wins" rule.
    /// Chords contribute <see cref="ChordNote.GraceFret"/> for EVERY graced
    /// string: Guitar Pro graces a strum by putting one grace note per string on
    /// the grace beat, and alphaTex renders several notes in a single <c>{gr}</c>
    /// beat exactly like any other multi-note beat — parenthesised content with
    /// <c>{gr}</c> on each note (verified against the bundled AlphaTab 1.8.3
    /// parser; a grace beat still consumes no bar time). A string may appear only
    /// once per beat, so the first hint on a string wins.</para>
    /// <para>A slot with no chord grace therefore emits exactly what it always
    /// did — a single hint renders unparenthesised as <c>fret.string{gr}</c>.</para>
    /// </summary>
    /// <param name="sb">Output buffer, positioned where the grace beat may start.</param>
    /// <param name="slot">The onset whose grace hints are collected.</param>
    /// <param name="staff">Domain → alphaTex string/fret coordinate map.</param>
    /// <param name="state">Emit state carrying the running <c>:duration</c> token.</param>
    private static void AppendGraceBeat(StringBuilder sb, Slot slot, StaffMap staff, EmitState state)
    {
        List<(int StringIndex, int Fret)>? graces = null;

        foreach (var n in slot.Notes)
        {
            if (n.GraceFret is not { } graceFret) continue;
            graces = [(n.StringIndex, graceFret)];
            break; // single notes keep first-hint-wins — unchanged behaviour
        }

        foreach (var c in slot.Chords)
        {
            foreach (var cn in c.ChordNotes)
            {
                if (cn.GraceFret is not { } graceFret) continue;
                graces ??= new List<(int StringIndex, int Fret)>(c.ChordNotes.Count);
                var alreadyGraced = false;
                foreach (var g in graces)
                {
                    if (g.StringIndex == cn.StringIndex)
                    {
                        alreadyGraced = true;
                        break;
                    }
                }

                if (!alreadyGraced) graces.Add((cn.StringIndex, graceFret));
            }
        }

        if (graces == null || graces.Count == 0) return;

        EmitDuration(sb, 16, state);
        if (graces.Count > 1) sb.Append('(');
        for (var i = 0; i < graces.Count; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(staff.Fret(graces[i].Fret).ToString(CultureInfo.InvariantCulture))
              .Append('.')
              .Append(staff.String(graces[i].StringIndex).ToString(CultureInfo.InvariantCulture))
              .Append("{gr}");
        }
        if (graces.Count > 1) sb.Append(')');
        sb.Append(' ');
    }

    private static bool HasTremolo(Slot slot)
    {
        foreach (var n in slot.Notes)
            if (n.Tremolo) return true;
        foreach (var c in slot.Chords)
        {
            foreach (var cn in c.ChordNotes)
                if (cn.Tremolo) return true;
        }

        return false;
    }

    private static void AppendBeatProps(StringBuilder sb, bool dotted, int tuplet, bool tremolo, string? chordName)
    {
        if (!dotted && tuplet == 0 && !tremolo && chordName == null) return;
        sb.Append(" {");
        var first = true;
        // `ch "Name"` prints the chord name above the staff (AlphaTab
        // NotationElement.EffectChordNames). Emit first so it reads clearly.
        if (chordName != null) Append(sb, ref first, "ch \"" + Escape(chordName) + "\"");
        if (dotted) Append(sb, ref first, "d");
        if (tuplet == 3) Append(sb, ref first, "tu 3");
        if (tremolo) Append(sb, ref first, "tp 2");
        sb.Append('}');
        return;

        static void Append(StringBuilder sb, ref bool first, string token)
        {
            if (!first) sb.Append(' ');
            sb.Append(token);
            first = false;
        }
    }

    private static void EmitDuration(StringBuilder sb, int denom, EmitState state)
    {
        if (denom == state.LastDuration) return;
        sb.Append(':').Append(denom.ToString(CultureInfo.InvariantCulture)).Append(' ');
        state.LastDuration = denom;
    }

    /// <summary>
    /// Picks the next duration chunk: an exact match wins (that is what makes a
    /// triplet gap render as a true triplet), otherwise the largest BINARY value
    /// that fits (triplet chunks as mere filler would scatter stray "3" brackets).
    /// </summary>
    private static (int Ticks, int Denominator, bool Dotted, int Tuplet) ChooseChunk(int ticks)
    {
        foreach (var d in StandardDurations)
            if (d.Ticks == ticks) return d;
        // NEVER leave a remainder of 1. One tick (1/192 of a beat) is the only
        // value no standard duration expresses, and the old code "absorbed" it
        // by emitting the 2-tick chunk anyway — which OVERFILLED the bar. A
        // 46-tick head split 36 + 9 + 2 = 47, so AlphaTab received 193 ticks of
        // content for a 192-tick bar and engraved the surplus as a stray
        // 64th-triplet sliver, breaking the beam of the run it sat in: exactly
        // the failure the whole exact-tick-grid model exists to prevent.
        // Skipping a candidate that would leave 1 keeps every remainder at 0 or
        // >= 2, so the loop always closes on a real duration.
        foreach (var d in StandardDurations)
            if (d.Tuplet == 0 && d.Ticks <= ticks && ticks - d.Ticks != 1) return d;
        // Only an off-grid tail (an ms-imported onset landing a tick or two past
        // a barline) reaches here. A tuplet value is normally never used as
        // filler — it scatters stray brackets — but closing the bar EXACTLY
        // beats leaving it short, and the alternative is the overfill above.
        foreach (var d in StandardDurations)
            if (d.Ticks <= ticks && ticks - d.Ticks != 1) return d;
        // ticks == 1 only, and unreachable from the emit paths now that no
        // remainder of 1 is ever produced (heads and ties are floored at
        // MinTicks, rest runs at 2). AlphaTexDurationSumTests pins that.
        return StandardDurations[^1];
    }

    // ---------------------------------------------------------------------
    // Beat content
    // ---------------------------------------------------------------------

    /// <summary>Playable entry of a merged slot: one string, one fret, one source.
    /// <c>Bend</c> is the pre-rendered alphaTex bend-value sequence (quarter-step
    /// units, evenly distributed over the beat), null = no bend.</summary>
    private readonly record struct PlayedNote(
        int StringIndex, int Fret, MuteType Mute, HopoType Hopo, HarmonicType Harmonic,
        bool Slide, bool UnpitchedSlide, bool Vibrato, int VibratoStrength, bool Accent,
        bool LinkNext, int[]? Bend, int Finger = -1);

    /// <summary>
    ///     The slot's playable entries, one per string.
    ///     <para>
    ///         <b>A sound off the neck is dropped, not clamped.</b>
    ///         <see cref="StaffMap.String" /> CLAMPS an out-of-range domain string
    ///         into 1..StringCount, so a note left behind on string 7 after an
    ///         arrangement was narrowed to six (the settings dialog changes
    ///         `StringCount` and nothing rewrites the notes) mapped onto the same
    ///         alphaTex string as the real high-E note beside it — the beat emitted
    ///         `(3.1 4.1)`, two notes on one string, and the tab stacked two digits
    ///         on one line. There is no honest place to draw a string the staff has
    ///         not got; the slot still carries the sound, so it stays clickable.
    ///     </para>
    /// </summary>
    private static List<PlayedNote> CollectPlayed(Slot slot, StaffMap staff)
    {
        var list = new List<PlayedNote>(4);
        foreach (var n in slot.Notes)
        {
            if (!staff.IsOnTheNeck(n.StringIndex)) continue;
            AddUnique(list, new PlayedNote(n.StringIndex, n.Fret, n.Mute, n.Hopo, n.Harmonic,
                n.SlideTo.HasValue, n.UnpitchedSlide, n.Vibrato, n.VibratoStrength, n.Accent,
                n.LinkNext, BuildBendSequence(n.BendValues, n.Position)));
        }
        foreach (var c in slot.Chords)
        {
            foreach (var cn in c.ChordNotes)
            {
                if (cn.Fret < 0 || !staff.IsOnTheNeck(cn.StringIndex)) continue;
                AddUnique(list, new PlayedNote(cn.StringIndex, cn.Fret, cn.Mute, cn.Hopo, cn.Harmonic,
                    cn.SlideTo.HasValue, cn.UnpitchedSlide, cn.Vibrato, cn.VibratoStrength, c.Accent,
                    cn.LinkNext, BuildBendSequence(cn.BendValues, c.Position),
                    slot.Fingers != null && slot.Fingers.TryGetValue(cn.StringIndex, out var f) ? f : -1));
            }
        }

        return list;

        static void AddUnique(List<PlayedNote> list, PlayedNote p)
        {
            foreach (var existing in list)
            {
                if (existing.StringIndex == p.StringIndex)
                    return; // one note per string per beat — keep the first
            }

            list.Add(p);
        }
    }

    /// <summary>
    /// Maps the chart's bend curve onto alphaTex's <c>b (v0 v1 …)</c> form:
    /// values in QUARTER-steps (4 = whole tone — parser-verified), evenly
    /// distributed over the beat. Domain <see cref="BendValue.Step"/> is in
    /// semitones, so value = step × 2. A bend whose first point sits after the
    /// onset starts unbent (leading 0); a lone at-onset point is a held
    /// prebend (v v). Returns null when there is nothing to draw (no points,
    /// or an all-zero curve).
    /// </summary>
    private static int[]? BuildBendSequence(IList<BendValue> bends, FractionalPosition onset)
    {
        if (bends.Count == 0) return null;

        var seq = new List<int>(bends.Count + 1);
        var any = false;
        for (var i = 0; i < bends.Count; i++)
        {
            var b = bends[i];
            if (i == 0 && b.Position > onset)
                seq.Add(0); // struck unbent, bends later
            var v = Math.Clamp((int)Math.Round(b.Step * 2), 0, 12);
            if (v > 0) any = true;
            if (seq.Count == 0 || seq[^1] != v) seq.Add(v); // collapse plateaus
        }

        if (!any) return null;
        if (seq.Count == 1) seq.Add(seq[0]); // held prebend: (v v)
        return seq.ToArray();
    }

    private static void AppendBeatHead(StringBuilder sb, Slot slot, Slot? next, StaffMap staff, EmitState state)
    {
        var played = CollectPlayed(slot, staff);
        if (played.Count == 0)
        {
            sb.Append('r');
            return;
        }

        if (played.Count > 1) sb.Append('(');
        for (var i = 0; i < played.Count; i++)
        {
            if (i > 0) sb.Append(' ');
            AppendPlayedNote(sb, played[i], next, staff, state);
        }
        if (played.Count > 1) sb.Append(')');

        // Update the per-string trail AFTER emitting (tie detection reads the
        // previous state).
        foreach (var p in played)
            state.LastByString[p.StringIndex] = (p.Fret, p.LinkNext, p.Slide || p.UnpitchedSlide);
    }

    private static void AppendPlayedNote(StringBuilder sb, PlayedNote p, Slot? next, StaffMap staff, EmitState state)
    {
        // Rocksmith "link next" chains render as real ties: the destination of a
        // pure link (same string, same fret, no slide in between, no fresh
        // articulation) is a held continuation, not a re-attack.
        var isTieDestination = p.Hopo == HopoType.None && p.Bend == null &&
                               state.LastByString.TryGetValue(p.StringIndex, out var prev) &&
                               prev.LinkNext && !prev.Slide && prev.Fret == p.Fret;
        if (isTieDestination)
        {
            sb.Append("-.")
              .Append(staff.String(p.StringIndex).ToString(CultureInfo.InvariantCulture));
            // Ornaments that still matter on a held note stay: vibrato, and a
            // slide OUT of the held note (dropping it detached the slide line).
            var anyProp = false;
            if (p.Slide) Open(sb, ref anyProp).Append("ss");
            else if (p.UnpitchedSlide) Open(sb, ref anyProp).Append("sou");
            if (p.Vibrato) Open(sb, ref anyProp).Append(p.VibratoStrength > 1 ? "vw" : "v");
            if (anyProp) sb.Append('}');
            return;
        }

        AppendFretAndString(sb, p.Fret, p.StringIndex, p.Mute, staff);
        AppendNoteProps(
            sb,
            NextIsHopoOnString(next, p.StringIndex),
            p.Harmonic,
            p.Slide,
            p.UnpitchedSlide,
            p.Vibrato,
            p.VibratoStrength,
            p.Mute == MuteType.Palm,
            p.Accent,
            isLetRing: false,
            p.Bend,
            isTap: p.Hopo == HopoType.Tap,
            finger: p.Finger);
    }

    /// <summary>
    ///     AlphaTex's <c>h</c> marks the ORIGIN of a hammer/pull slur (the
    ///     arc runs from it to the following note), while the Rocksmith
    ///     domain flags the DESTINATION. Marking the flagged note itself
    ///     shifted every slur one note to the right — the "broken hammer-ons"
    ///     in the notation view.
    /// </summary>
    private static bool NextIsHopoOnString(Slot? next, int stringIndex)
    {
        if (next == null) return false;
        foreach (var n in next.Notes)
        {
            if (n.StringIndex == stringIndex && n.Hopo is HopoType.HammerOn or HopoType.PullOff)
                return true;
        }

        foreach (var c in next.Chords)
        {
            foreach (var cn in c.ChordNotes)
            {
                if (cn.StringIndex == stringIndex && cn.Hopo is HopoType.HammerOn or HopoType.PullOff)
                    return true;
            }
        }

        return false;
    }

    private static void AppendTiedHead(StringBuilder sb, Slot slot, StaffMap staff)
    {
        var played = CollectPlayed(slot, staff);
        if (played.Count == 0)
        {
            sb.Append('r');
            return;
        }

        if (played.Count > 1) sb.Append('(');
        for (var i = 0; i < played.Count; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append("-.")
              .Append(staff.String(played[i].StringIndex).ToString(CultureInfo.InvariantCulture));
        }
        if (played.Count > 1) sb.Append(')');
    }

    private static void AppendFretAndString(
        StringBuilder sb, int fret, int domainString, MuteType mute, StaffMap staff)
    {
        if (mute == MuteType.Full)
            sb.Append('x');
        else
            sb.Append(staff.Fret(fret).ToString(CultureInfo.InvariantCulture));
        sb.Append('.').Append(staff.String(domainString).ToString(CultureInfo.InvariantCulture));
    }

    private static void AppendNoteProps(
        StringBuilder sb,
        bool hopoOriginToNext,
        HarmonicType harmonic,
        bool slide,
        bool unpitchedSlide,
        bool vibrato,
        int vibratoStrength,
        bool palmMute,
        bool accent,
        bool isLetRing,
        int[]? bend = null,
        bool isTap = false,
        int finger = -1)
    {
        var any = false;
        if (hopoOriginToNext) Open(sb, ref any).Append('h');
        // `ss` = SHIFT slide: the clean diagonal connector line, exactly how
        // GP renders a picked slide. `sl` (legato) additionally draws a slur
        // arc + "sl." label per slide — on a run of slides those arcs stack
        // into the tangled overlapping curves the notation view shipped with.
        // Rocksmith slides are picked slides, so shift is the honest mapping.
        if (slide) { Open(sb, ref any).Append("ss"); }
        else if (unpitchedSlide) { Open(sb, ref any).Append("sou"); }
        switch (harmonic)
        {
            case HarmonicType.Normal: Open(sb, ref any).Append("nh"); break;
            case HarmonicType.Pinch: Open(sb, ref any).Append("ph"); break;
        }
        if (vibrato) Open(sb, ref any).Append(vibratoStrength > 1 ? "vw" : "v");
        if (palmMute) Open(sb, ref any).Append("pm");
        if (accent) Open(sb, ref any).Append("ac");
        if (isLetRing) Open(sb, ref any).Append("lr");
        // HopoType.Tap is a real domain value that would otherwise engrave as an ORDINARY
        // note — the reader would be told to pick a note the charter marked as tapped.
        // `tt` is parser-verified (it sets Beat.Tap on the bundled 1.8.3 model).
        if (isTap) Open(sb, ref any).Append("tt");
        // Left-hand fingering from the chord template. AlphaTex numbers fingers
        // 1..5 = thumb..little; the domain numbers them 0..4 (T,1,2,3,4), so the
        // mapping is +1. Verified: `lf 2` imports as IndexFinger.
        if (finger is >= 0 and <= 4)
            Open(sb, ref any).Append("lf ").Append((finger + 1).ToString(CultureInfo.InvariantCulture));
        if (bend != null)
        {
            // Parser-verified shapes: (0 4) bend, (0 4 0) bend-release,
            // (4 0) prebend-release, (4 4) held prebend — the curve draws
            // above the note/tab exactly like GP.
            Open(sb, ref any).Append("b (");
            for (var i = 0; i < bend.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(bend[i].ToString(CultureInfo.InvariantCulture));
            }
            sb.Append(')');
        }
        if (any) sb.Append('}');
    }

    private static StringBuilder Open(StringBuilder sb, ref bool any)
    {
        if (!any) { sb.Append('{'); any = true; }
        else
        {
            sb.Append(' ');
        }

        return sb;
    }

    /// <summary>
    /// The staff's string/fret coordinate map — everything that converts a
    /// domain (string, fret) pair into the alphaTex one.
    /// <para><b>Capo.</b> AlphaTab ADDS <c>\capo</c> to every fret when it derives
    /// pitch (probe-verified against the bundled 1.8.3 importer: fret 5 under
    /// <c>\capo 3</c> reports <c>RealValue</c> 48, i.e. fret 8). DotCharter frets
    /// are ABSOLUTE on the un-capoed neck, with fret 0 meaning "open" — which a
    /// capo stops AT the capo fret. The sounding offset is therefore
    /// <c>max(fret, capo)</c>, the same derivation
    /// <see cref="ChordNameSuggester"/>, <c>RefretPlanner.SoundingMidi</c> and
    /// <c>LivePitchAcceptance.SoundingMidi</c> use. So every emitted fret is
    /// CAPO-RELATIVE: <c>max(fret − capo, 0)</c>. That makes AlphaTab's
    /// <c>+capo</c> land back on the absolute fret (correct notation pitch) and
    /// is also how tab under a capo is written everywhere — 0 is the capo.
    /// Emitting the absolute fret engraved every fretted note <i>capo</i>
    /// semitones sharp while open strings stayed right.</para>
    /// <para>Clamping at 0 also keeps a malformed negative fret from emitting
    /// <c>-1.6</c>, which alphaTex would read as the <c>-</c> tie marker.</para>
    /// </summary>
    private readonly record struct StaffMap(int StringCount, int Capo)
    {
        /// <summary>
        ///     Whether this staff HAS the given domain string. <see cref="String" />
        ///     clamps, which is right for the emitted token and wrong as a filter:
        ///     an out-of-range index clamps onto a string that already has a note,
        ///     and the beat then carries two notes on one line.
        /// </summary>
        public bool IsOnTheNeck(int domainStringIndex) =>
            domainStringIndex >= 0 && domainStringIndex < StringCount;

        /// <summary>Domain: 0 = lowest pitch (low E). AlphaTex: 1 = highest pitch (high E).</summary>
        public int String(int domainStringIndex)
        {
            var s = StringCount - domainStringIndex;
            if (s < 1) s = 1;
            if (s > StringCount) s = StringCount;
            return s;
        }

        /// <summary>Absolute domain fret → the capo-relative fret alphaTex expects.</summary>
        public int Fret(int absoluteFret) => Math.Max(absoluteFret - Math.Max(0, Capo), 0);
    }

    private static Dictionary<int, ChordTemplate> BuildTemplateIndex(Arrangement arrangement)
    {
        var d = new Dictionary<int, ChordTemplate>();
        for (var i = 0; i < arrangement.ChordTemplates.Count; i++)
        {
            var t = arrangement.ChordTemplates[i];
            if (t.Id >= 0) d[t.Id] = t;
        }
        return d;
    }

    /// <summary>
    /// Resolves a display chord name for the staff annotation: the template's own
    /// name when set, otherwise a name derived from the chord's fret shape via
    /// <see cref="ChordNameSuggester"/>. Returns null when nothing sensible can be
    /// derived (so no <c>ch</c> effect is emitted). Results are cached per
    /// template id — a chart repeats a handful of shapes hundreds of times.
    /// </summary>
    private static string? ResolveChordName(
        Chord chord,
        Arrangement arrangement,
        Dictionary<int, ChordTemplate> templatesById,
        Dictionary<int, string?> cache)
    {
        if (chord.TemplateId >= 0)
        {
            if (cache.TryGetValue(chord.TemplateId, out var cached)) return cached;

            string? resolved = null;
            if (templatesById.TryGetValue(chord.TemplateId, out var tpl))
            {
                if (!string.IsNullOrWhiteSpace(tpl.Name)) resolved = tpl.Name.Trim();
                else if (tpl.Frets.Count > 0) resolved = SuggestChordName(arrangement, tpl.Frets);
            }

            cache[chord.TemplateId] = resolved;
            return resolved;
        }

        var frets = BuildFretsFromChord(chord);
        return frets.Count > 0 ? SuggestChordName(arrangement, frets) : null;
    }

    private static string? SuggestChordName(Arrangement arrangement, IReadOnlyDictionary<int, int> frets)
    {
        var name = ChordNameSuggester.SuggestFirstChordName(arrangement, frets);
        return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }

    private static Dictionary<int, int> BuildFretsFromChord(Chord chord)
    {
        var d = new Dictionary<int, int>();
        for (var i = 0; i < chord.ChordNotes.Count; i++)
        {
            var cn = chord.ChordNotes[i];
            if (cn.Fret >= 0 && cn.Mute != MuteType.Full)
                d[cn.StringIndex] = cn.Fret;
        }
        return d;
    }

    // ---------------------------------------------------------------------
    // Measures
    // ---------------------------------------------------------------------

    /// <summary>
    ///     A time-signature denominator alphaTex — and notation — can express.
    ///     <para>
    ///         <see cref="BeatMarker.NoteDenominator" /> is a plain int with no
    ///         validation anywhere, and the beat dialog's <c>NumericUpDown</c> takes
    ///         any value from 1 to 64 in steps of 1. A non-power-of-two lands
    ///         <c>\ts (4 3)</c> in the document, which the bundled 1.8.3 parser
    ///         rejects with <c>AlphaTexErrorWithDiagnostics</c> — so the WHOLE sheet
    ///         pane fell to its error state and the PDF export threw, from one
    ///         keystroke in one beat's meter. It also poisons the tick math: the
    ///         bar length is <c>num × 192 / den</c>, so a third of a bar is not on
    ///         any duration ladder and the bar filled with tuplet slivers.
    ///     </para>
    ///     <para>
    ///         Same set and same fallback as
    ///         <c>Services/Export/GuitarPro/GpExporter.NormalizeDenominator</c>, so
    ///         the notation and the <c>.gp</c> export of a malformed meter agree
    ///         rather than differ. Normalizing HERE (not just at the <c>\ts</c>
    ///         token) is what keeps the emitted ticks and the declared meter the
    ///         same bar.
    ///     </para>
    /// </summary>
    private static int NormalizeDenominator(int denominator) =>
        denominator is 1 or 2 or 4 or 8 or 16 or 32 ? denominator : 4;

    /// <summary>
    ///     Whether <see cref="BeatMarker.IsMeasure" /> can be trusted as THE
    ///     barline set for this grid.
    ///     <para>
    ///         That flag is what every other consumer counts bars by — the bar
    ///         numbers the user reads (<c>ChartPositionLabel</c>), <c>LiveBarNumbers</c>,
    ///         <c>SnapService</c>'s measure snap, <c>ChartSpacingEnforcer</c>,
    ///         <c>PhraseSectionGenerator</c> and <c>SngFromDomainMapper</c> — and
    ///         every importer sets it (the RS-XML <c>measure=</c> attribute, the SNG
    ///         mask bit, EOF's <c>IsMeasureStart</c>, GP's <c>beatInBar == 0</c>).
    ///         For a well-formed grid it agrees exactly with counting
    ///         <c>BeatsInMeasure</c> beats at a time, so honouring it is a no-op —
    ///         it only differs where the grid is IRREGULAR, and there the count was
    ///         wrong: a chart with a two-beat PICKUP put its first barline two beats
    ///         early and every barline in the song after it, so the sheet's bar 3
    ///         was the timeline's bar 2 and a click landed in the wrong bar.
    ///     </para>
    ///     <para>
    ///         Some grids carry only ONE flag (<c>TempoService</c> marks beat 0 and
    ///         nothing else) or a half-finished set, and reading those as barlines
    ///         would produce one bar hundreds of beats long. So the flags are used
    ///         only when they look like a real set: at least two of them, and no gap
    ///         longer than twice the largest declared <c>BeatsInMeasure</c>.
    ///         Otherwise the count-based grouping stands, exactly as before.
    ///     </para>
    /// </summary>
    /// <summary>Beats from <paramref name="start" /> up to (not including) the next
    /// flagged downbeat — i.e. how many beats this bar really holds.</summary>
    private static int SpanToNextMeasureFlag(IList<BeatMarker> beats, int start)
    {
        var span = 1;
        while (start + span < beats.Count && !beats[start + span].IsMeasure) span++;
        return span;
    }

    private static bool MeasureFlagsAreUsable(IList<BeatMarker> beats)
    {
        var flagged = 0;
        var maxNumerator = 1;
        var lastFlag = -1;
        var longestGap = 0;
        for (var i = 0; i < beats.Count; i++)
        {
            if (beats[i].BeatsInMeasure > maxNumerator) maxNumerator = beats[i].BeatsInMeasure;
            if (!beats[i].IsMeasure) continue;
            flagged++;
            if (lastFlag >= 0 && i - lastFlag > longestGap) longestGap = i - lastFlag;
            lastFlag = i;
        }

        if (flagged < 2) return false;
        // The tail after the last flag is a bar too, so it counts as a gap.
        if (lastFlag >= 0 && beats.Count - lastFlag > longestGap) longestGap = beats.Count - lastFlag;
        return longestGap <= 2 * maxNumerator;
    }

    private static List<Measure> BuildMeasures(IList<BeatMarker> beats)
    {
        var measures = new List<Measure>(beats.Count / 4 + 1);
        var useFlags = MeasureFlagsAreUsable(beats);
        var i = 0;
        while (i < beats.Count)
        {
            var first = beats[i];
            var den = NormalizeDenominator(first.NoteDenominator);
            // With usable flags the bar runs to the next flagged beat, and its
            // NUMERATOR is the span it really holds — a two-beat pickup engraves
            // as a two-beat bar, which is both how a pickup is written and what
            // keeps "every bar emits exactly its time signature" a real claim
            // rather than a declared-4/4 bar holding two beats.
            var num = useFlags
                ? SpanToNextMeasureFlag(beats, i)
                : Math.Max(1, first.BeatsInMeasure);
            var bpm = first.Bpm > 0 ? first.Bpm : 120.0;
            var startMs = first.Time;
            var lastIdx = Math.Min(beats.Count - 1, i + num - 1);
            var endMs = lastIdx + 1 < beats.Count
                ? beats[lastIdx + 1].Time
                : beats[lastIdx].Time + 60_000.0 / Math.Max(1.0, bpm);

            var totalTicks = Math.Max(1, num * TicksPerQuarter * 4 / den);
            var ticksPerBeat = Math.Max(1, totalTicks / num);
            measures.Add(new Measure(startMs, endMs, num, den, SheetTempoMarking.BarTempo(beats, i, lastIdx, bpm), i, totalTicks,
                ticksPerBeat));
            i = lastIdx + 1;
        }
        return measures;
    }

    /// <summary>
    /// Escapes a string destined for an alphaTex <c>"…"</c> literal (<c>\title</c>,
    /// <c>\subtitle</c>, <c>ch "…"</c>).
    /// <para><b>The backslash MUST be escaped first, and it must be escaped at
    /// all.</b> The bundled 1.8.3 lexer honours <c>\\</c> and <c>\"</c> inside a
    /// string literal but treats every other <c>\x</c> as a hard parse error, so
    /// a song title, artist or chord name carrying a lone backslash
    /// (<c>AC\DC</c>, a pasted Windows path) threw <c>UnsupportedFormatError</c>
    /// and took the WHOLE document down — no notation at all, for one character
    /// in the metadata. A TRAILING backslash was worse: it escaped the closing
    /// quote and ran the string on into the score. Doing the quote pass first
    /// would re-break it, because the <c>\</c> it inserts would then be doubled.</para>
    /// <para>Control characters are folded to spaces rather than escaped: the
    /// lexer accepts a raw newline inside a literal, but a multi-line title
    /// draws over the first system.</para>
    /// </summary>
    private static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (var ch in s)
        {
            if (ch is '\\' or '"') sb.Append('\\').Append(ch);
            else if (char.IsControl(ch)) sb.Append(' ');
            else sb.Append(ch);
        }
        return sb.ToString();
    }

    // Mutable carry threaded across measures for cross-barline ties.
    private sealed class TieState
    {
        public bool Active;
        public Slot? Slot;
        public FractionalPosition HeldUntil;
    }

    private readonly record struct Measure(
        double StartMs,
        double EndMs,
        int Numerator,
        int Denominator,
        double Bpm,
        int FirstBeatIndex,
        int TotalTicks,
        int TicksPerBeat);
}
