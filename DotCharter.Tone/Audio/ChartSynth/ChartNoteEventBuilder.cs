using DotCharter.Core.Domain.Primitives;
using JuceSharp;

namespace DotCharter.Tone.Audio.ChartSynth;

/// <summary>
///     One chart, flattened into the compact arrays the native chart source
///     renders from (<c>dca_chart_note</c> / <c>dca_chart_bend_point</c>): sorted by
///     start, every time in chart seconds, every pitch what the string SOUNDS.
/// </summary>
public sealed class ChartNoteUpload
{
    /// <summary>The notes, sorted by start. Single notes and chord notes alike.</summary>
    public required JuceChartNote[] Notes { get; init; }

    /// <summary>Every bend point; each note names its own range.</summary>
    public required JuceChartBendPoint[] Bends { get; init; }

    /// <summary>The <see cref="ChartNoteEventBuilder.Fingerprint" /> of the chart it was built from.</summary>
    public long Fingerprint { get; init; }
}

/// <summary>
///     Builds a <see cref="ChartNoteUpload" /> from an arrangement's notes and chords.
///     Pure: no engine, no clock, so every rule here is pinned by the default gate.
///     <list type="bullet">
///         <item><b>Pitch</b> is <see cref="SoundingPitch.MidiFor" /> — standard
///         open strings, the arrangement's tuning offsets, the capo (fret 0 sounds
///         at the capo) and the bass octave — plus <c>CentOffset</c> / 100. A note
///         <c>MidiFor</c> refuses (a fret behind the capo, a string past the sixth)
///         is left out rather than guessed.</item>
///         <item><b>Release</b> is <see cref="ReleaseFor" />: only a note inside a hand
///         shape rings across later notes (to the shape's end, its own longer sustain
///         winning, damped by the next note on its own string, held on to the next
///         note when that comes within a quarter note unless accented); every other note
///         ends no later than the next note on ANY string — held legato into it when
///         the gap is under a quarter note at the local tempo — and accented or muted
/// notes stay short. The song's <see cref="ChartSynthPlayStyle" /> can
///         turn each hold off and set its gap per guitar/bass and per note/chord;
///         the quarter note is its default. HOW it ends is
///         <see cref="JuceChartNoteFlags.EndDamped" /> / <see cref="JuceChartNoteFlags.EndShape" />,
///         and a hold past the authored sustain is <see cref="JuceChartNoteFlags.LetRing" />.</item>
///         <item><b>Articulations</b> map one flag each. Strum direction is not in
///         the chart model, so a chord's comes from the BEAT GRID the way a
///         strumming hand moves (<see cref="StrumFor" />); a single note goes out as
///         <see cref="JuceChartStrum.Unknown" />.</item>
///     </list>
/// </summary>
public static class ChartNoteEventBuilder
{
    private const double LegatoEndPadMs = 8.0;
    // Past the 10 ms cross-fade of the voice's legato takeover.
    private const double LegatoOverlapMs = 30.0;
    private const double StaccatoMs = 90.0;
    private const double LetRingEpsilonMs = 1.0;

    /// <summary>Builds the upload. <paramref name="bpm" /> sizes the minimum hold
    /// (an eighth note); 120 is used when it is not positive. <paramref name="style" />
    /// is the song's playing style; null plays the defaults.</summary>
    public static ChartNoteUpload Build(Arrangement arrangement, IList<GuitarNote> notes, IList<Chord> chords,
        IList<BeatMarker> beats, IList<HandShape>? handShapes, double bpm, ChartSynthPlayStyle? style = null)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        ArgumentNullException.ThrowIfNull(notes);
        ArgumentNullException.ThrowIfNull(chords);
        ArgumentNullException.ThrowIfNull(beats);

        var fingerprint = Fingerprint(arrangement, notes, chords, beats, handShapes, bpm, style);
        if (beats.Count == 0)
            return new ChartNoteUpload { Notes = [], Bends = [], Fingerprint = fingerprint };

        // A bass PART, not just the Bass path: a 5/6-string bass charted on a guitar
        // path at A220 plays the bass voice and rules too. Its pitch is already
        // in the bass register (MidiFor + the -1200 cents below), so nothing is shifted.
        var isBass = ArrangementInstrument.IsBassPart(arrangement);
        var ctx = new ReleaseContext(NoteLegatoIndex.Build(notes, chords, beats), AllStarts(notes, chords, beats),
            beats, handShapes, bpm > 0 ? 60_000.0 / bpm : 500.0, RulesFor(style, isBass));
        var cents = (float)(arrangement.CentOffset / 100.0);

        var pending = new List<(JuceChartNote Note, List<JuceChartBendPoint>? Bends)>(notes.Count + chords.Count * 4);

        foreach (var n in notes)
        {
            var built = Make(arrangement, n, n.Position, n.Accent, -1, ctx, cents, isBass);
            if (built is { } b) pending.Add(b);
        }

        // Chord positions in beats, sorted, for each chord's neighbours (strum).
        var chordBeats = new double[chords.Count];
        for (var i = 0; i < chords.Count; i++) chordBeats[i] = chords[i].Position.DoubleValue;
        Array.Sort(chordBeats);

        var chordId = 0;
        foreach (var c in chords)
        {
            var id = chordId++;
            var at = c.Position.DoubleValue;
            var k = Array.BinarySearch(chordBeats, at);
            if (k < 0) k = ~k;
            var prev = k > 0 ? at - chordBeats[k - 1] : double.MaxValue;
            var next = k + 1 < chordBeats.Length ? chordBeats[k + 1] - at : double.MaxValue;
            var palmOrAccent = c.Accent || c.ChordNotes.Any(n => n.Accent || n.Mute == MuteType.Palm);
            var strum = StrumFor(at, prev, next, palmOrAccent);
            foreach (var cn in c.ChordNotes)
            {
                var built = Make(arrangement, cn, c.Position, c.Accent || cn.Accent, id, ctx, cents, isBass, strum);
                if (built is { } b) pending.Add(b);
            }
        }

        // Sorted by start (then string, for a stable chord order); bend ranges are
        // assigned AFTER the sort so each note's indices name its own points.
        pending.Sort((a, b) =>
        {
            var byStart = a.Note.StartSeconds.CompareTo(b.Note.StartSeconds);
            return byStart != 0 ? byStart : a.Note.StringIndex.CompareTo(b.Note.StringIndex);
        });

        var outNotes = new JuceChartNote[pending.Count];
        var outBends = new List<JuceChartBendPoint>();
        for (var i = 0; i < pending.Count; i++)
        {
            var note = pending[i].Note;
            var bends = pending[i].Bends;
            if (bends is { Count: > 0 })
            {
                note.BendFirst = outBends.Count;
                note.BendCount = bends.Count;
                outBends.AddRange(bends);
            }
            outNotes[i] = note;
        }

        return new ChartNoteUpload { Notes = outNotes, Bends = [.. outBends], Fingerprint = fingerprint };
    }

    /// <summary>
    /// A chord's strum direction from where it sits in its beat, the way a
    ///     strumming hand keeps moving: on a beat, and on the eighth- (or, among
    ///     sixteenths, the sixteenth-) grid's EVEN subdivisions, it is DOWN; on the
    ///     odd ones UP; triplet and sextuplet grids alternate the same way. A
    ///     palm-muted or accented chord is a down-stroke, and so is anything off
    ///     every grid. Mirrors <c>dcvoice::detail::strumIsUp</c> (the native planner),
    ///     in beats rather than seconds.
    /// </summary>
    /// <param name="beats">The chord's position in beats (beat index + fraction).</param>
    /// <param name="prevGapBeats">Beats since the previous chord (<see cref="double.MaxValue" /> = none).</param>
    /// <param name="nextGapBeats">Beats to the next chord (<see cref="double.MaxValue" /> = none).</param>
    /// <param name="palmOrAccent">A palm-muted or accented chord.</param>
    /// <returns><see cref="JuceChartStrum.Down" /> or <see cref="JuceChartStrum.Up" />.</returns>
    internal static JuceChartStrum StrumFor(double beats, double prevGapBeats, double nextGapBeats, bool palmOrAccent)
    {
        if (palmOrAccent) return JuceChartStrum.Down;
        var phase = beats - Math.Floor(beats);
        var sixteenths = Math.Min(prevGapBeats, nextGapBeats) <= 0.30;
        const double tol = 0.06;
        foreach (var g in (ReadOnlySpan<int>)[sixteenths ? 4 : 2, 3, 6])
        {
            var x = phase * g;
            var r = Math.Round(x);
            if (Math.Abs(x - r) <= tol * g)
                return ((int)r % g) % 2 == 1 ? JuceChartStrum.Up : JuceChartStrum.Down;
        }

        return JuceChartStrum.Down;
    }

    private static (JuceChartNote Note, List<JuceChartBendPoint>? Bends)? Make(Arrangement arrangement,
        IGuitarSound s, FractionalPosition position, bool accent, int chordId, ReleaseContext ctx, float cents,
        bool isBass, JuceChartStrum strum = JuceChartStrum.Unknown)
    {
        var beats = ctx.Beats;
        if (SoundingPitch.MidiFor(arrangement, s.StringIndex, s.Fret) is not { } midi) return null;

        var startMs = position.GetPositionMs(beats);
        var sustainEndMs = (position + s.Sustain).GetPositionMs(beats);
        var flags = FlagsFor(s, accent, isBass && arrangement.PickedBass, isBass);
        var (releaseMs, end) = ReleaseFor(s, startMs, sustainEndMs, accent, chordId >= 0, ctx);
        flags |= end;

        var note = new JuceChartNote
        {
            StartSeconds = startMs / 1000.0,
            ReleaseSeconds = releaseMs / 1000.0,
            SustainSeconds = Math.Max(0.0, sustainEndMs - startMs) / 1000.0,
            Pitch = Math.Clamp(midi + cents, 0f, 127f),
            Velocity = accent ? 0.95f : 0.8f,
            StringIndex = s.StringIndex,
            Fret = s.Fret,
            Capo = Math.Clamp(arrangement.Capo, 0, 24),
            SlideToFret = s.SlideTo is { } to and >= 0 ? to : -1f,
            Flags = flags,
            ChordId = chordId,
            BendFirst = -1,
            BendCount = 0,
            Strum = strum,
        };

        List<JuceChartBendPoint>? bends = null;
        if (s.BendValues.Count > 0)
        {
            bends = new List<JuceChartBendPoint>(s.BendValues.Count);
            foreach (var b in s.BendValues.OrderBy(b => b.Position))
            {
                bends.Add(new JuceChartBendPoint
                {
                    TimeSeconds = b.Position.GetPositionMs(beats) / 1000.0,
                    // BendValue.Step: 1.0 = a half step, i.e. it already IS semitones.
                    Semitones = (float)Math.Clamp(b.Step, -24.0, 24.0),
                });
            }
        }

        return (note, bends);
    }

    private static readonly ChartSynthPlayRules DefaultRules = new();

    /// <summary>The rule set this arrangement plays by: the bass rules for a bass
    /// arrangement, the guitar rules otherwise; the defaults without a style.</summary>
    private static ChartSynthPlayRules RulesFor(ChartSynthPlayStyle? style, bool isBass) =>
        style?.For(isBass) ?? DefaultRules;

    /// <summary>What <see cref="ReleaseFor" /> reads besides the note itself.</summary>
    internal sealed record ReleaseContext(NoteLegatoIndex Legato, double[] AllStarts, IList<BeatMarker> Beats,
        IList<HandShape>? HandShapes, double FallbackQuarterMs, ChartSynthPlayRules Rules);

    /// <summary>
    /// When a note is released, and how (owner rules):
    ///     <list type="number">
    ///         <item><b>Inside a hand shape</b> (chord or arpeggio, the note STARTS in
    ///         <c>[start, end)</c>) and not muted, a note may ring ACROSS later notes:
    ///         to the shape's end, or its own sustain when that is longer (Rocksmith
    ///         semantics) — cut only by the next note on its OWN string
    ///         (<see cref="JuceChartNoteFlags.EndDamped" />). Reaching the shape's end
    ///         is <see cref="JuceChartNoteFlags.EndShape" />; a hold past its own
    ///         sustain is <see cref="JuceChartNoteFlags.LetRing" />. When the next note
    ///         (any string) starts less than a quarter note after that end, a note that
    ///         is not accented holds up to it and is damped just before its attack, so
    /// back-to-back shapes do not stutter. A next note ON the end is left
    ///         alone: the two already join.</item>
    ///         <item><b>Everywhere else</b> a note NEVER overlaps the next note on any
    ///         string. Its own end is its sustain — at least an eighth note at the
    ///         local tempo, a 90 ms staccato when accented, an eighth when palm-muted,
    ///         a 90 ms thump when fret-hand-muted. If the next note (any string)
    ///         starts within a QUARTER NOTE of its authored end (its sustain, or its
    ///         attack when it has none) (<see cref="QuarterNoteMsAt" />,
    ///         from the beat map), a plain note rings up to it — legato — and is
    ///         damped just before its attack; an accented or muted one keeps its short
    ///         end. With a gap of a quarter or more it ends at its own end with a
    ///         natural release. Every cut by a following note is
    ///         <see cref="JuceChartNoteFlags.EndDamped" />, so the voice damps rather
    ///         than decays it.</item>
    ///         <item><b>Before a hammer-on, pull-off or tap on its own string</b>, or
    ///         when it links into the next note, a note (not fret-hand-muted) rings
    ///         <c>LegatoOverlapMs</c> PAST that note's start instead of being damped
    ///         before it — inside a hand shape always, elsewhere when nothing on
    ///         another string comes first and the gap is under a quarter note — so the
    /// voice continues the string rather than picking it again.</item>
    ///     </list>
    ///     The chart model has no let-ring flag of its own, so the hand shape is the
    ///     only thing that lets a note ring across others. The song's playing style
    /// (<see cref="ReleaseContext.Rules" />) can switch rule 1 off, switch
    ///     the hold over a gap off, and replace "a quarter note" in rules 1 and 2 by
    ///     another note value — separately for single notes and chord notes; rule 3
    ///     stays as it is (it is how a hammer-on SOUNDS, not a taste).
    /// </summary>
    internal static (double ReleaseMs, JuceChartNoteFlags End) ReleaseFor(IGuitarSound s, double startMs,
        double sustainEndMs, bool accent, bool chordNote, ReleaseContext ctx)
    {
        const double minimumMs = 30.0;
        var quarterMs = QuarterNoteMsAt(startMs, ctx.Beats, ctx.FallbackQuarterMs);
        var eighthMs = quarterMs / 2.0;
        // The playing style: whether a gap to the next note is held over, and
        // how short a gap counts (a note value: 4 = a quarter, 16 = a sixteenth).
        var rules = ctx.Rules;
        var holdGaps = chordNote ? rules.HoldChordGaps : rules.HoldSingleNoteGaps;
        var gapMs = 4.0 * quarterMs / ChartSynthPlayRules.Sanitize(
            chordNote ? rules.ChordGapDivisor : rules.SingleNoteGapDivisor);
        var held = Math.Max(sustainEndMs, startMs);
        var muted = s.Mute is MuteType.Palm or MuteType.Full;
        double ringTo;
        var flags = JuceChartNoteFlags.None;

        // A hammer-on, pull-off or tap on this string, or a note this one links
        // into, is sounded by the fretting hand and continues THIS vibration.
        // The note rings INTO it so the voice takes its legato path instead of a
        // fresh pick; the late release is ignored, because releases match by note id.
        if (s.Mute != MuteType.Full && ctx.Legato.NextOnString(s.StringIndex, startMs) is { } onString
            && (onString.Hopo || s.LinkNext))
        {
            var inShape = !muted && FindShape(startMs, ctx.HandShapes, ctx.Beats) is not null;
            var nothingBetween = NextStartAfter(ctx.AllStarts, startMs) is not { } first
                || first >= onString.Start - LetRingEpsilonMs;
            if (inShape || (nothingBetween && onString.Start - held < quarterMs))
            {
                var legatoTo = onString.Start + LegatoOverlapMs;
                var ring = legatoTo > Math.Max(held, startMs + eighthMs) + LetRingEpsilonMs
                    ? JuceChartNoteFlags.LetRing
                    : JuceChartNoteFlags.None;
                return (Math.Max(legatoTo, startMs + minimumMs), ring);
            }
        }

        if (!muted && rules.HandShapesRing && FindShape(startMs, ctx.HandShapes, ctx.Beats) is { } shapeEnd)
        {
            ringTo = Math.Max(shapeEnd, held);
            if (ctx.Legato.NextStartOnString(s.StringIndex, startMs) is { } nextOnString
                && nextOnString - LegatoEndPadMs < ringTo)
            {
                ringTo = nextOnString - LegatoEndPadMs;
                flags |= JuceChartNoteFlags.EndDamped;
            }
            else if (!accent && holdGaps && NextStartAfter(ctx.AllStarts, ringTo - LetRingEpsilonMs) is { } after
                     && after > ringTo + LetRingEpsilonMs && after - ringTo < gapMs)
            {
                ringTo = after - LegatoEndPadMs;
                flags |= JuceChartNoteFlags.EndDamped;
            }
            else if (ringTo <= shapeEnd + LetRingEpsilonMs && shapeEnd > held + LetRingEpsilonMs)
            {
                flags |= JuceChartNoteFlags.EndShape;
            }

            if (ringTo > Math.Max(held, startMs + eighthMs) + LetRingEpsilonMs)
            {
                flags |= JuceChartNoteFlags.LetRing;
            }
        }
        else
        {
            var shortNote = accent || muted;
            var ownEnd = s.Mute == MuteType.Full || (accent && s.Mute != MuteType.Palm)
                ? Math.Max(held, startMs + StaccatoMs)
                : Math.Max(held, startMs + eighthMs);
            ringTo = ownEnd;
            if (NextStartAfter(ctx.AllStarts, startMs) is { } next)
            {
                var damp = next - LegatoEndPadMs;
                if (damp < ownEnd || (!shortNote && holdGaps && next - held < gapMs))
                {
                    ringTo = damp;
                    flags |= JuceChartNoteFlags.EndDamped;
                }
            }
        }

        return (Math.Max(ringTo, startMs + minimumMs), flags);
    }

    /// <summary>
    ///     A quarter note's length (ms) at <paramref name="atMs" />, read off the beat
    ///     map: the span of the beat that contains it, scaled by that beat's
    ///     <see cref="BeatMarker.NoteDenominator" /> (a 6/8 beat is an eighth, so a
    ///     quarter is two of them). Past the last beat the last span holds; with fewer
    ///     than two beats the caller's tempo does.
    /// </summary>
    internal static double QuarterNoteMsAt(double atMs, IList<BeatMarker> beats, double fallbackQuarterMs)
    {
        if (beats.Count < 2) return fallbackQuarterMs;
        int lo = 0, hi = beats.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (beats[mid].Time <= atMs) lo = mid;
            else hi = mid - 1;
        }

        var i = Math.Min(lo, beats.Count - 2);
        var span = beats[i + 1].Time - beats[i].Time;
        var denominator = beats[i].NoteDenominator > 0 ? beats[i].NoteDenominator : 4;
        return span > 0 ? span * denominator / 4.0 : fallbackQuarterMs;
    }

    private static double[] AllStarts(IList<GuitarNote> notes, IList<Chord> chords, IList<BeatMarker> beats)
    {
        var starts = new double[notes.Count + chords.Count];
        var k = 0;
        foreach (var n in notes) starts[k++] = n.GetTimeMs(beats);
        foreach (var c in chords) starts[k++] = c.GetTimeMs(beats);
        Array.Sort(starts);
        return starts;
    }

    /// <summary>The first start strictly after <paramref name="afterMs" /> (1 µs
    /// tolerance, so the notes of one chord are not each other's "next").</summary>
    private static double? NextStartAfter(double[] starts, double afterMs)
    {
        int lo = 0, hi = starts.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (starts[mid] <= afterMs + 1e-3) lo = mid + 1;
            else hi = mid;
        }

        return lo < starts.Length ? starts[lo] : null;
    }

    /// <summary>The end (ms) of the hand shape <paramref name="startMs" /> starts
    /// inside, or null. Half-open: a note ON a shape's end belongs to the next one.</summary>
    private static double? FindShape(double startMs, IList<HandShape>? handShapes, IList<BeatMarker> beats)
    {
        if (handShapes == null || beats.Count == 0) return null;
        for (var i = 0; i < handShapes.Count; i++)
        {
            var hs = handShapes[i];
            var from = hs.Position.GetPositionMs(beats);
            var to = hs.EndPosition.GetPositionMs(beats);
            if (startMs + 1e-3 >= from && startMs < to - 1e-3) return to;
        }

        return null;
    }

    /// <summary>The articulation bits of one note or chord note.</summary>
    internal static JuceChartNoteFlags FlagsFor(IGuitarSound s, bool accent, bool pickedBass, bool isBass)
    {
        var f = JuceChartNoteFlags.None;
        f |= s.Mute switch
        {
            MuteType.Palm => JuceChartNoteFlags.PalmMute,
            MuteType.Full => JuceChartNoteFlags.FretHandMute,
            _ => JuceChartNoteFlags.None,
        };
        f |= s.Harmonic switch
        {
            HarmonicType.Normal => JuceChartNoteFlags.Harmonic,
            HarmonicType.Pinch => JuceChartNoteFlags.PinchHarmonic,
            _ => JuceChartNoteFlags.None,
        };
        f |= s.Hopo switch
        {
            HopoType.HammerOn => JuceChartNoteFlags.HammerOn,
            HopoType.PullOff => JuceChartNoteFlags.PullOff,
            HopoType.Tap => JuceChartNoteFlags.Tap,
            _ => JuceChartNoteFlags.None,
        };
        f |= s.BassPicking switch
        {
            BassPickingType.Slap => JuceChartNoteFlags.Slap,
            BassPickingType.Pop => JuceChartNoteFlags.Pop,
            _ => JuceChartNoteFlags.None,
        };
        if (s.Vibrato) f |= JuceChartNoteFlags.Vibrato;
        if (s.Tremolo) f |= JuceChartNoteFlags.Tremolo;
        if (accent) f |= JuceChartNoteFlags.Accent;
        if (s.UnpitchedSlide && s.SlideTo.HasValue) f |= JuceChartNoteFlags.UnpitchedSlide;
        if (s.LinkNext) f |= JuceChartNoteFlags.LinkNext;
        if (isBass) f |= JuceChartNoteFlags.Bass;
        if (pickedBass) f |= JuceChartNoteFlags.Picked;
        return f;
    }

    /// <summary>
    ///     A cheap, allocation-free summary of everything <see cref="Build" /> reads,
    ///     so a caller can poll for chart edits and re-upload only on a change.
    ///     Covers every note and chord field the upload depends on, the hand shapes,
    ///     the whole tempo map and the arrangement's tuning, capo, cent offset and
    ///     type. Not a content hash in the cryptographic sense: equal input gives an
    ///     equal value, and a realistic edit changes it. Positions hash through
    ///     <c>HashCode.Combine</c>, which is seeded per process — compare values from
    ///     ONE process only, never persist one.
    /// </summary>
    public static long Fingerprint(Arrangement arrangement, IList<GuitarNote> notes, IList<Chord> chords,
        IList<BeatMarker> beats, IList<HandShape>? handShapes, double bpm, ChartSynthPlayStyle? style = null)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        var h = new Fnv();
        h.Add((int)arrangement.ArrangementType);
        // The rules this arrangement plays by: a changed playing style re-uploads.
        var rules = RulesFor(style, ArrangementInstrument.IsBassPart(arrangement));
        h.Add(rules.HandShapesRing ? 1 : 0);
        h.Add(rules.HoldSingleNoteGaps ? 1 : 0);
        h.Add(rules.SingleNoteGapDivisor);
        h.Add(rules.HoldChordGaps ? 1 : 0);
        h.Add(rules.ChordGapDivisor);
        h.Add(arrangement.Capo);
        h.Add(arrangement.CentOffset);
        h.Add(arrangement.PickedBass ? 1 : 0);
        h.Add(arrangement.TuningPitches.Count);
        foreach (var t in arrangement.TuningPitches) h.Add(t.Step);
        h.Add(bpm);
        // Every beat: a moved beat in the middle of the map re-times every note after it.
        h.Add(beats.Count);
        for (var i = 0; i < beats.Count; i++) h.Add(beats[i].Time);

        h.Add(notes.Count);
        for (var i = 0; i < notes.Count; i++)
        {
            var n = notes[i];
            Add(ref h, n, n.Position);
        }

        h.Add(chords.Count);
        for (var i = 0; i < chords.Count; i++)
        {
            var c = chords[i];
            h.Add(c.Accent ? 1 : 0);
            h.Add(c.ChordNotes.Count);
            for (var j = 0; j < c.ChordNotes.Count; j++) Add(ref h, c.ChordNotes[j], c.Position);
        }

        h.Add(handShapes?.Count ?? 0);
        if (handShapes != null)
        {
            for (var i = 0; i < handShapes.Count; i++)
            {
                AddPosition(ref h, handShapes[i].Position);
                AddPosition(ref h, handShapes[i].EndPosition);
            }
        }

        return h.Value;
    }

    private static void Add(ref Fnv h, IGuitarSound s, FractionalPosition position)
    {
        AddPosition(ref h, position);
        AddPosition(ref h, s.Sustain);
        h.Add(s.StringIndex);
        h.Add(s.Fret);
        h.Add((int)s.Mute);
        h.Add((int)s.Harmonic);
        h.Add((int)s.Hopo);
        h.Add((int)s.BassPicking);
        h.Add((s.Vibrato ? 1 : 0) | (s.Tremolo ? 2 : 0) | (s.Accent ? 4 : 0) | (s.UnpitchedSlide ? 8 : 0)
              | (s.LinkNext ? 16 : 0));
        h.Add(s.SlideTo ?? int.MinValue);
        h.Add(s.BendValues.Count);
        for (var k = 0; k < s.BendValues.Count; k++)
        {
            AddPosition(ref h, s.BendValues[k].Position);
            h.Add(s.BendValues[k].Step);
        }
    }

    private static void AddPosition(ref Fnv h, FractionalPosition p) => h.Add(p.GetHashCode());

    /// <summary>64-bit FNV-1a over a stream of values.</summary>
    private struct Fnv
    {
        private ulong _h;
        public Fnv() => _h = 14695981039346656037UL;
        public readonly long Value => unchecked((long)_h);

        public void Add(int v)
        {
            ulong bits = unchecked((uint)v);
            Add(bits);
        }

        public void Add(double v) => Add(unchecked((ulong)BitConverter.DoubleToInt64Bits(v)));

        private void Add(ulong v)
        {
            for (var i = 0; i < 8; i++)
            {
                _h ^= (v >> (i * 8)) & 0xFF;
                _h *= 1099511628211UL;
            }
        }
    }
}
