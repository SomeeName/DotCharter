using AlphaTab.Exporter;
using DotCharter.Services.Infrastructure;
using DotCharter.Services.Parsing.GuitarPro;
using AT = AlphaTab.Model;

namespace DotCharter.Services.Export.GuitarPro;

/// <summary>
///     Writes an arrangement level as a Guitar Pro 7+ <c>.gp</c> file through
///     the alphaTab score model (<see cref="AT.Score"/> →
///     <see cref="Gp7Exporter"/>), so charts round-trip to Guitar Pro and back
///     through <see cref="Parsing.GuitarPro.GpImporter"/>.
///     <para>
///     Mapping mirrors the import conventions: one position unit = one entry
///     of the beat list, a bar groups <c>BeatsInMeasure</c> consecutive beats,
///     and each beat carries a note value of <c>1/NoteDenominator</c>. Bars
///     are filled on a 48-ticks-per-beat grid (16ths, 32nds and triplets all
///     land exactly); a note's written rhythm runs to its sustain (or to the
///     next event when the chart carries no sustain, matching how the
///     importer trims short sustains), split into tied beats when it does not
///     fit a single note value. Domain string index 0 = lowest string maps to
///     alphaTab string 1 = lowest.
///     </para>
/// </summary>
public static class GpExporter
{
    private const int TicksPerBeat = 48;

    public static void Export(Arrangement arrangement, ChartLevel level, IList<BeatMarker> beats,
        string filePath, string? title = null, string? artist = null) =>
        // AtomicFile, not File.WriteAllBytes: a bare write TRUNCATES the target
        // first, so a crash, a kill or a power cut inside the export leaves the
        // charter with an empty .gp where their previous export was. The same
        // rule the save companions follow (Export/CompanionWritesAreAtomicTests).
        AtomicFile.WriteAllBytes(filePath, ExportToBytes(arrangement, level, beats, title, artist));

    public static byte[] ExportToBytes(Arrangement arrangement, ChartLevel level, IList<BeatMarker> beats,
        string? title = null, string? artist = null)
    {
        PlayOnlyExportGuard.ThrowIfPlayOnly(arrangement, "Guitar Pro export");
        // The .gp container is a WIRE FORMAT: floats inside the gpif must be
        // dot-decimal no matter the app language. alphaTab serializes through
        // the THREAD culture, so a de-DE app culture (every German user) — or
        // a concurrently culture-switching localization test — wrote comma
        // decimals that Guitar Pro and our own re-import reject. Pin the
        // export to the invariant culture for the whole build+serialize.
        //
        // ResetIds() below rewinds a process-global counter, so the whole
        // build+serialize also has to be EXCLUSIVE: a second exporter calling
        // ResetIds mid-flight renumbers the model this one is still building.
        // See AlphaTabRuntime.RunExclusive.
        return AlphaTabRuntime.RunExclusive(() =>
        {
            var previousCulture = System.Threading.Thread.CurrentThread.CurrentCulture;
            System.Threading.Thread.CurrentThread.CurrentCulture =
                System.Globalization.CultureInfo.InvariantCulture;
            try
            {
                var settings = AlphaTabRuntime.CreateSettings();
                // alphaTab ids come from process-global counters; reset them so the
                // same input always serializes to the same document.
                AT.Score.ResetIds();
                var score = BuildScore(arrangement, level, beats, title, artist);
                score.Finish(settings);
                var data = new Gp7Exporter().Export(score, settings);
                var bytes = new byte[(int)data.Length];
                for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)data[i];
                return bytes;
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = previousCulture;
            }
        });
    }

    // ------------------------------------------------------------ score

    internal static AT.Score BuildScore(Arrangement arrangement, ChartLevel level, IList<BeatMarker> beats,
        string? title, string? artist)
    {
        var score = new AT.Score
        {
            Title = title ?? "",
            Artist = artist ?? ""
        };

        var track = new AT.Track { Name = arrangement.Name, ShortName = arrangement.Name };
        var staff = new AT.Staff
        {
            ShowTablature = true,
            ShowStandardNotation = true,
            Capo = arrangement.Capo
        };
        track.AddStaff(staff);
        score.AddTrack(track);

        staff.StringTuning.Tunings = BuildTuningHighToLow(arrangement);

        // Guitar Pro reads every fret RELATIVE to the Staff.Capo written above,
        // while the chart model stores them ABSOLUTE. The capo therefore has to
        // reach both fret-writing sites (BuildNote and EmitGrace) so they can
        // apply GpCapoFrets.ToRelative — the exact inverse of what the importers
        // apply at their fret read. It is threaded as an explicit parameter, not
        // a field: two exports may run concurrently (the save companions build
        // detached), and shared mutable state would let one export stamp the
        // other's capo onto its frets.
        var capo = arrangement.Capo;

        var events = BuildEventList(level.Notes, level.Chords);
        // Notation-only sidecar voices (GP voices 2-4) export as additional GP
        // voices on every bar so the authored polyphony survives the round trip.
        // Empty ones are dropped; the rest fill their silent bars with rests.
        var sidecars = arrangement.NotationVoices
            .Where(v => v.Notes.Count > 0 || v.Chords.Count > 0)
            .OrderBy(v => v.VoiceIndex)
            .Select(v => BuildEventList(v.Notes, v.Chords))
            .ToList();

        // A sound written past its bar's end continues into the next bar as tied beats
        // (one carry per voice), as Guitar Pro itself writes a note across a bar line.
        var chartCarry = new BarCarry(Legato.Of(events));
        var sidecarCarries = sidecars.Select(e => new BarCarry(Legato.Of(e))).ToList();

        var startBeat = 0;
        while (startBeat < beats.Count)
        {
            var first = beats[startBeat];
            var beatsInBar = Math.Clamp(first.BeatsInMeasure, 1, Math.Max(1, beats.Count - startBeat));
            var denominator = NormalizeDenominator(first.NoteDenominator);

            var masterBar = new AT.MasterBar
            {
                TimeSignatureNumerator = beatsInBar,
                TimeSignatureDenominator = denominator
            };
            AddTempoChanges(masterBar, beats, startBeat, beatsInBar);
            score.AddMasterBar(masterBar);

            var bar = new AT.Bar();
            staff.AddBar(bar);

            var chartVoice = new AT.Voice();
            bar.AddVoice(chartVoice);
            FillBar(chartVoice, events, startBeat, beatsInBar, denominator, capo, chartCarry);

            for (var v = 0; v < sidecars.Count; v++)
            {
                var sidecarVoice = new AT.Voice();
                bar.AddVoice(sidecarVoice);
                FillBar(sidecarVoice, sidecars[v], startBeat, beatsInBar, denominator, capo, sidecarCarries[v]);
            }

            startBeat += beatsInBar;
        }

        return score;
    }

    internal static List<double> BuildTuningHighToLow(Arrangement arrangement)
    {
        // 7, 8 and 9 strings extend DOWN (B1, F#1, then C#1) — the one table every importer
        // measures offsets against. A 9-string must not fall to the six-string
        // row, which would climb a fourth per extra string, writing A4 and D5 for its two low
        // strings.
        var standard = arrangement.StringCount switch
        {
            4 => [28, 33, 38, 43],
            // 5 is E A D G B, an octave down for a bass: the importers subtract the same row.
            5 => TuningUtilities.GetStandardMidiPitchesFor(5, arrangement.ArrangementType),
            7 or 8 or 9 => TuningUtilities.GetStandardMidiPitches(arrangement.StringCount),
            _ => [40, 45, 50, 55, 59, 64]
        };

        var offsets = TuningOffsetResolution.Resolve(arrangement);
        var lowToHigh = new List<double>(arrangement.StringCount);
        for (var i = 0; i < arrangement.StringCount; i++)
        {
            var offset = i < offsets.Count ? offsets[i] : 0;
            var baseMidi = i < standard.Length ? standard[i] : standard[^1] + 5 * (i - standard.Length + 1);
            lowToHigh.Add(baseMidi + offset);
        }

        lowToHigh.Reverse(); // alphaTab staff tuning is highest string first
        return lowToHigh;
    }

    private static int NormalizeDenominator(int denominator) => denominator is 1 or 2 or 4 or 8 or 16 or 32 ? denominator : 4;

    private static void AddTempoChanges(AT.MasterBar masterBar, IList<BeatMarker> beats, int startBeat,
        int beatsInBar)
    {
        for (var b = 0; b < beatsInBar && startBeat + b < beats.Count; b++)
        {
            var index = startBeat + b;
            if (beats[index].Bpm <= 0) continue;
            // Score.Tempo is read-only in the model, so the initial tempo is
            // also written as an automation on the very first bar.
            if (index > 0 && Math.Abs(beats[index].Bpm - beats[index - 1].Bpm) < 0.005) continue;

            masterBar.TempoAutomations.Add(new AT.Automation
            {
                Type = AT.AutomationType.Tempo,
                Value = beats[index].Bpm,
                RatioPosition = (double)b / beatsInBar
            });
        }
    }

    // ------------------------------------------------------------ events

    private sealed record TimedEvent(FractionalPosition Position, GuitarNote? Note, Chord? Chord)
    {
        public FractionalPosition MaxSustain => Note?.Sustain ?? Chord!.GetMaxSustain();
    }

    private static List<TimedEvent> BuildEventList(IReadOnlyCollection<GuitarNote> notes,
        IReadOnlyCollection<Chord> chords)
    {
        var events = new List<TimedEvent>(notes.Count + chords.Count);
        foreach (var note in notes) events.Add(new TimedEvent(note.Position, note, null));
        foreach (var chord in chords) events.Add(new TimedEvent(chord.Position, null, chord));
        events.Sort(static (a, b) => a.Position.CompareTo(b.Position));
        return events;
    }

    /// <summary>
    ///     Every chart event that sounds at one tick — a Guitar Pro BEAT is a
    ///     chord of simultaneous notes, so they all have to ride one.
    ///     <para>
    ///         <b>This must not be a single event, dropping the rest.</b>
    ///         A loop that kept the first event at a tick and <c>continue</c>d past
    ///         the others would lose every string but one of any shape the chart
    ///         spells as loose single notes in the exported <c>.gp</c>. That is not
    ///         an edge case: it is how the highway authors a double stop or an
    ///         octave without a template, and it is exactly what
    ///         <c>LinkLandingBreakup</c> produces for every chord a link lands on
    ///         (in the hand-corrected EOF corpus, 492 of 493 such landings carry
    ///         2-4 simultaneous notes). The first event still decides the RHYTHM,
    ///         so a tick with one event exports byte-identically.
    ///     </para>
    /// </summary>
    private sealed record EventSlot(int Tick, List<TimedEvent> Events)
    {
        public TimedEvent Primary => Events[0];

        /// <summary>The longest sustain sounding at this tick — the fallback
        /// written rhythm, as before, but over the whole group.</summary>
        public FractionalPosition MaxSustain
        {
            get
            {
                var max = Events[0].MaxSustain;
                for (var i = 1; i < Events.Count; i++)
                {
                    if (Events[i].MaxSustain > max)
                        max = Events[i].MaxSustain;
                }

                return max;
            }
        }
    }

    /// <summary>
    ///     The part of a voice's last sound that runs past its bar's end, in QUARTER notes,
    ///     still to be written into the next bar as tie destinations. Cut at the bar line, a
    ///     note tied across a bar would come back as a shorter note followed by a rest — and
    ///     the re-import reads that rest as staccato and accents the note.
    /// </summary>
    private sealed class BarCarry(Legato legato)
    {
        public EventSlot? Slot { get; set; }
        public double Quarters { get; set; }

        /// <summary>The voice's legato marks (<see cref="Legato.Of" />).</summary>
        public Legato Legato { get; } = legato;
    }

    /// <summary>
    ///     How a voice's legato is spelled in Guitar Pro, which marks the ORIGIN of a
    ///     hammer-on or pull-off and finds the destination itself, on the same string.
    /// </summary>
    /// <param name="Origins">For every sound flagged <see cref="HopoType.HammerOn" /> /
    /// <see cref="HopoType.PullOff" /> (the chart flags the DESTINATION), the previous sound on
    /// its string. Marking only an origin whose destination is the very next event of the same
    /// bar would lose every legato note after another string's note, or at the start of a bar.</param>
    /// <param name="LeftHandTaps">Hammer-ons with NO previous sound on their string: a note the
    /// fretting hand sounds on its own, which is exactly Guitar Pro's left-hand tapping
    /// (<c>LeftHandTapped</c>). The importer reads that back as a hammer-on
    /// (<c>GpNoteParser.ApplyHopo</c>); with no origin to mark, a plain hammer-on came back
    /// as an ordinary picked note.</param>
    private sealed record Legato(IReadOnlySet<IGuitarSound> Origins, IReadOnlySet<IGuitarSound> LeftHandTaps)
    {
        public static readonly Legato None = new(new HashSet<IGuitarSound>(), new HashSet<IGuitarSound>());

        public static Legato Of(List<TimedEvent> events)
        {
            var origins = new HashSet<IGuitarSound>(ReferenceEqualityComparer.Instance);
            var leftHandTaps = new HashSet<IGuitarSound>(ReferenceEqualityComparer.Instance);
            var last = new Dictionary<int, IGuitarSound>();
            foreach (var ev in events)
            {
                IEnumerable<IGuitarSound> sounds = ev.Note is { } note ? [note] : ev.Chord!.ChordNotes;
                foreach (var sound in sounds)
                {
                    if (sound.Hopo is not (HopoType.HammerOn or HopoType.PullOff)) continue;
                    if (last.TryGetValue(sound.StringIndex, out var origin)) origins.Add(origin);
                    else if (sound.Hopo == HopoType.HammerOn) leftHandTaps.Add(sound);
                }

                foreach (var sound in sounds) last[sound.StringIndex] = sound;
            }

            return new Legato(origins, leftHandTaps);
        }
    }

    private static void FillBar(AT.Voice voice, List<TimedEvent> events, int startBeat, int beatsInBar,
        int denominator, int capo, BarCarry carry)
    {
        var totalTicks = beatsInBar * TicksPerBeat;
        var chunks = new Durations(BuildDurationChunks(denominator));

        var slots = new List<EventSlot>();
        foreach (var ev in events)
        {
            var beatOffset = ev.Position.BeatId - startBeat;
            if (beatOffset < 0) continue;
            if (beatOffset >= beatsInBar) break;
            var tick = beatOffset * TicksPerBeat +
                       (int)Math.Round(ev.Position.Fraction.DoubleValue * TicksPerBeat);
            if (tick >= totalTicks) tick = totalTicks - 1;
            // Simultaneous events JOIN the slot instead of being discarded.
            if (slots.Count > 0 && slots[^1].Tick == tick)
            {
                slots[^1].Events.Add(ev);
                continue;
            }

            slots.Add(new EventSlot(tick, [ev]));
        }

        var cursor = 0;
        if (carry.Slot is { } carried)
        {
            var carriedTicks = (int)Math.Round(carry.Quarters * TicksPerBeat * denominator / 4);
            var tied = Math.Min(carriedTicks, slots.Count > 0 ? slots[0].Tick : totalTicks);
            EmitTied(voice, carried, tied, chunks, capo);
            cursor = tied;
            // Only a bar with nothing else in it passes the rest of the carry on.
            carry.Quarters = slots.Count == 0 ? carry.Quarters - tied * 4.0 / (TicksPerBeat * denominator) : 0;
            if (carry.Quarters * TicksPerBeat * denominator / 4 < 1) carry.Slot = null;
        }

        // Whether anything sounds after this bar: only then does a carry make sense.
        var barEndBeat = startBeat + beatsInBar;
        var following = events.FirstOrDefault(e => e.Position.BeatId >= barEndBeat);

        for (var s = 0; s < slots.Count; s++)
        {
            var slot = slots[s];
            if (slot.Tick > cursor)
            {
                EmitRests(voice, slot.Tick - cursor, chunks);
                cursor = slot.Tick;
            }

            var gap = (s + 1 < slots.Count ? slots[s + 1].Tick : totalTicks) - cursor;

            // AUTHORED notation wins: a stored NotationDuration sets the written
            // rhythm (clamped so it never overruns the next onset). Otherwise fall
            // back to the sustain/onset-gap derivation the exporter always used.
            var authoredTicks = AuthoredTicks(slot.Primary, denominator);
            int wantedTicks;
            if (authoredTicks > 0)
            {
                wantedTicks = authoredTicks;
            }
            else
            {
                var sustainTicks = (int)Math.Round(slot.MaxSustain.DoubleValue * TicksPerBeat);
                wantedTicks = sustainTicks > 0 ? Math.Max(1, sustainTicks) : gap;
            }

            // The nearest length a tied chain of note values writes EXACTLY, with a writable
            // rest after it: a tick no value spells would otherwise be dropped and shorten the bar.
            var noteTicks = chunks.Chain.Fit(wantedTicks, gap);
            if (wantedTicks <= gap && ReadsBackAsStaccato(slot, noteTicks, gap, denominator))
                noteTicks = chunks.Chain.Fit(gap, gap);
            var isLast = s + 1 == slots.Count;
            if (isLast && wantedTicks > gap && following != null)
            {
                carry.Slot = slot;
                carry.Quarters = (wantedTicks - gap) * 4.0 / (TicksPerBeat * denominator);
            }


            EmitGrace(voice, slot, capo);
            EmitEvent(voice, slot, carry.Legato, noteTicks, chunks, denominator, capo);
            cursor += noteTicks;
        }

        if (cursor < totalTicks) EmitRests(voice, totalTicks - cursor, chunks);
    }

    /// <summary>
    ///     Whether the re-import would ACCENT this slot's note although the chart has no accent
    ///     on it. The importer's SHORT-REST rule (<c>GpBeatCarry.ObserveStaccatoBeat</c>)
    ///     accents a single note followed by a rest of at most its own value and at most a
    ///     quarter: the way a tab spells staccato. A note written at its authored value with the
    ///     next onset a little later (the shape two merged voices leave) is exactly that, so such
    ///     a note is written up to the next onset instead. Only a note whose chart sustain is
    ///     zero and whose new length stays under a beat qualifies: the re-import then trims the
    ///     sub-beat sustain back to zero, so the sounds are unchanged. A note the chart accents
    ///     keeps its rest — that IS staccato, and the rule hands the accent back.
    /// </summary>
    private static bool ReadsBackAsStaccato(EventSlot slot, int noteTicks, int gap, int denominator)
    {
        if (slot.Events.Count != 1 || slot.Primary.Note is not { Accent: false } note) return false;
        if (note.Sustain > FractionalPosition.Zero || gap >= TicksPerBeat) return false;
        var rest = gap - noteTicks;
        return rest > 0 && rest <= noteTicks && rest <= TicksPerBeat * denominator / 4;
    }

    // -------------------------------------------------------- durations

    private readonly record struct DurationChunk(int Ticks, AT.Duration Duration, int Dots, bool Triplet);

    /// <summary>All note values expressible on the 48-ticks-per-beat grid for
    /// the given denominator, longest first; ties in length prefer the
    /// simplest notation (plain, then dotted, then triplet).</summary>
    private static List<DurationChunk> BuildDurationChunks(int denominator)
    {
        var wholeTicks = TicksPerBeat * denominator;
        var chunks = new List<DurationChunk>();
        foreach (var duration in new[]
                 {
                     AT.Duration.Whole, AT.Duration.Half, AT.Duration.Quarter, AT.Duration.Eighth,
                     AT.Duration.Sixteenth, AT.Duration.ThirtySecond, AT.Duration.SixtyFourth,
                     AT.Duration.OneHundredTwentyEighth
                 })
        {
            if (wholeTicks % (int)duration != 0) continue;
            var baseTicks = wholeTicks / (int)duration;
            chunks.Add(new DurationChunk(baseTicks, duration, 0, false));
            if (baseTicks * 3 % 2 == 0) chunks.Add(new DurationChunk(baseTicks * 3 / 2, duration, 1, false));
            if (baseTicks * 2 % 3 == 0) chunks.Add(new DurationChunk(baseTicks * 2 / 3, duration, 0, true));
        }

        chunks.Sort(static (a, b) =>
        {
            var byTicks = b.Ticks.CompareTo(a.Ticks);
            if (byTicks != 0) return byTicks;
            return SimplicityRank(a).CompareTo(SimplicityRank(b));
        });

        // Same length twice (e.g. dotted quarter vs quarter-triplet clash is
        // impossible, but dotted vs plain of the next value is): keep the
        // simplest spelling only.
        for (var i = chunks.Count - 1; i > 0; i--)
        {
            if (chunks[i].Ticks == chunks[i - 1].Ticks)
                chunks.RemoveAt(i);
        }

        return chunks;

        static int SimplicityRank(DurationChunk chunk) => chunk.Triplet ? 2 : chunk.Dots;
    }

    /// <summary>A bar's note values and the chains (<see cref="GpTickChain" />) they spell.</summary>
    private sealed class Durations(List<DurationChunk> chunks)
    {
        public GpTickChain Chain { get; } = new([.. chunks.Select(c => c.Ticks)]);

        /// <summary>The tied values writing <paramref name="ticks" />, longest first: exact when
        /// any chain is, else the longest-first values that fit (the remainder is lost).</summary>
        public IEnumerable<DurationChunk> Spell(int ticks)
        {
            if (Chain.TrySpell(ticks, out var exact))
            {
                foreach (var i in exact) yield return chunks[i];
                yield break;
            }

            foreach (var chunk in chunks)
            {
                while (chunk.Ticks <= ticks)
                {
                    yield return chunk;
                    ticks -= chunk.Ticks;
                }
            }
        }
    }

    private static AT.Beat NewBeat(DurationChunk chunk)
    {
        var beat = new AT.Beat { Duration = chunk.Duration, Dots = chunk.Dots };
        if (chunk.Triplet)
        {
            beat.TupletNumerator = 3;
            beat.TupletDenominator = 2;
        }

        return beat;
    }

    private static void EmitRests(AT.Voice voice, int ticks, Durations chunks)
    {
        foreach (var chunk in chunks.Spell(ticks))
            voice.AddBeat(NewBeat(chunk)); // a beat without notes is a rest
    }

    // ------------------------------------------------------------ notes

    /// <summary>Writes <paramref name="ticks" /> of a carried sound as tie destinations.</summary>
    private static void EmitTied(AT.Voice voice, EventSlot slot, int ticks, Durations chunks, int capo)
    {
        foreach (var chunk in chunks.Spell(ticks))
        {
            var beat = NewBeat(chunk);
            AddSounds(beat, slot, Legato.None, ticks, first: false, capo);
            voice.AddBeat(beat);
            ticks -= chunk.Ticks;
        }
    }

    private static void EmitEvent(AT.Voice voice, EventSlot slot, Legato legato, int ticks,
        Durations chunks, int denominator, int capo)
    {
        // AUTHORED spelling wins: when the event carries a NotationDuration that
        // (a) has a clean base/dots/tuplet spelling and (b) exactly fills the
        // written length, emit ONE beat with that duration/dots/tuplet instead of
        // decomposing the tick count. Non-clean or clamped durations fall through
        // to the grid decomposition (the exporter's long-standing behavior).
        var primary = slot.Primary;
        var nd = primary.Note?.NotationDuration ?? primary.Chord?.NotationDuration;
        if (nd is { } d &&
            QuarterTicks(d, denominator) == ticks &&
            GpRhythmFraction.TryUnfold(d, out var spelling) &&
            TryMapDuration(spelling.BaseValue, out var authoredDuration))
        {
            var beat = new AT.Beat { Duration = authoredDuration, Dots = spelling.Dots };
            if (spelling.HasTuplet)
            {
                beat.TupletNumerator = spelling.TupletNum;
                beat.TupletDenominator = spelling.TupletDen;
            }

            AddSounds(beat, slot, legato, ticks, first: true, capo);
            voice.AddBeat(beat);
            return;
        }

        var first = true;
        foreach (var chunk in chunks.Spell(ticks))
        {
            var beat = NewBeat(chunk);
            AddSounds(beat, slot, legato, ticks, first, capo);
            voice.AddBeat(beat);
            ticks -= chunk.Ticks;
            first = false;
        }
    }

    /// <summary>
    ///     Adds every sound of the slot to one alphaTab beat. One note per STRING:
    ///     <c>Score.Finish</c> assumes that, and two chart events at one position
    ///     can legitimately overlap a string (a chord plus a loose note the
    ///     charter left on top of it) — the first one written wins, exactly as
    ///     <see cref="EmitGrace" /> already resolves the same collision.
    ///     <para>
    ///     Tremolo, right-hand tapping, slap and pop are BEAT techniques in Guitar Pro
    ///     (alphaTab writes <c>Tapped</c> on every note of the beat, <c>Slapped</c> /
    ///     <c>Popped</c> on the beat), so any sound of the slot carrying one marks the beat.
    ///     </para>
    /// </summary>
    private static void AddSounds(AT.Beat beat, EventSlot slot, Legato legato, int ticks, bool first,
        int capo)
    {
        var tremolo = false;
        var accent = false;
        var tap = false;
        var picking = BassPickingType.None;
        var written = new HashSet<int>();

        foreach (var ev in slot.Events)
        {
            if (ev.Note is { } note)
            {
                if (!written.Add(note.StringIndex)) continue;
                beat.AddNote(BuildNote(note, note, legato, ticks, isTieDestination: !first, capo));
                Observe(note);
            }
            else if (ev.Chord is { } chord)
            {
                foreach (var chordNote in chord.ChordNotes)
                {
                    if (!written.Add(chordNote.StringIndex)) continue;
                    beat.AddNote(BuildNote(chordNote, chord, legato, ticks, isTieDestination: !first, capo));
                    Observe(chordNote);
                }

                // A chord's accent rides on the notes that carry it (a Guitar Pro import keeps
                // it per note); only a chord accented as a whole accents every note.
                accent |= chord.Accent && !chord.ChordNotes.Any(cn => cn.Accent);
            }
        }

        if (first)
        {
            beat.Tap = tap;
            beat.Slap = picking == BassPickingType.Slap;
            beat.Pop = picking == BassPickingType.Pop;
        }

        if (tremolo && first) beat.TremoloSpeed = AT.Duration.Eighth;
        if (accent && first)
        {
            foreach (var n in beat.Notes)
                n.Accentuated = AT.AccentuationType.Normal;
        }

        void Observe(IGuitarSound sound)
        {
            // A charted pick scrape implies its own tap and tremolo (see BuildNote).
            var scrape = GpPickScrape.IsChartedScrape(sound);
            tremolo |= sound.Tremolo && !scrape;
            tap |= sound.Hopo == HopoType.Tap && !scrape;
            if (picking == BassPickingType.None) picking = sound.BassPicking;
        }
    }

    /// <summary>Written length of an event's authored notation duration in the
    /// 48-ticks-per-beat grid, or 0 when the event has no NotationDuration.</summary>
    private static int AuthoredTicks(TimedEvent ev, int denominator)
    {
        var nd = ev.Note?.NotationDuration ?? ev.Chord?.NotationDuration;
        return nd is { } d ? QuarterTicks(d, denominator) : 0;
    }

    /// <summary>
    ///     A length in QUARTER notes (a <c>NotationDuration</c>) on the bar's tick grid,
    ///     whose beat is one <paramref name="denominator" /> note: an eighth (1/2) is
    ///     48 ticks in 6/8, not 24. Counting the quarter as a beat wrote every authored
    ///     rhythm of an x/8 bar at half its length and filled the rest with rests.
    /// </summary>
    private static int QuarterTicks(Fraction quarters, int denominator) =>
        (int)Math.Round(quarters.DoubleValue * TicksPerBeat * denominator / 4);

    /// <summary>Maps an unfolded base note value (quarter-note units) to the
    /// alphaTab duration enum; false for anything outside whole..128th.</summary>
    private static bool TryMapDuration(Fraction baseValue, out AT.Duration duration)
    {
        duration = AT.Duration.Quarter;
        if (baseValue == new Fraction(4, 1)) duration = AT.Duration.Whole;
        else if (baseValue == new Fraction(2, 1)) duration = AT.Duration.Half;
        else if (baseValue == new Fraction(1, 1)) duration = AT.Duration.Quarter;
        else if (baseValue == new Fraction(1, 2)) duration = AT.Duration.Eighth;
        else if (baseValue == new Fraction(1, 4)) duration = AT.Duration.Sixteenth;
        else if (baseValue == new Fraction(1, 8)) duration = AT.Duration.ThirtySecond;
        else if (baseValue == new Fraction(1, 16)) duration = AT.Duration.SixtyFourth;
        else if (baseValue == new Fraction(1, 32)) duration = AT.Duration.OneHundredTwentyEighth;
        else return false;
        return true;
    }

    /// <summary>Emits a before-beat grace ornament ahead of the event's main beat
    /// (grace beats consume no bar time in alphaTab). A single note contributes
    /// its <c>GuitarNote.GraceFret</c>; a CHORD contributes one grace note per
    /// graced string — Guitar Pro graces a strum by putting one grace note per
    /// string on the same grace beat, which is exactly the shape the importer
    /// reads back into <c>ChordNote.GraceFret</c>. An event with no grace hint
    /// adds no beat at all, so grace-free charts export unchanged.
    /// <para>
    /// A grace hint is a FRET like any other, so it takes the same
    /// absolute → capo-relative conversion as the main note.
    /// </para></summary>
    /// <param name="voice">The alphaTab voice the grace beat is appended to.</param>
    /// <param name="slot">The sounds at this tick, whose grace hint(s) are being written.</param>
    /// <param name="capo">The arrangement's capo fret; see
    /// <see cref="GpCapoFrets.ToRelative"/>.</param>
    private static void EmitGrace(AT.Voice voice, EventSlot slot, int capo)
    {
        List<(int StringIndex, int Fret)>? graces = null;

        foreach (var ev in slot.Events)
        {
            if (ev.Note is { GraceFret: { } noteGrace } note)
            {
                graces ??= [];
                if (graces.Any(g => g.StringIndex == note.StringIndex)) continue;
                graces.Add((note.StringIndex, noteGrace));
            }
            else if (ev.Chord is { } chord)
            {
                foreach (var chordNote in chord.ChordNotes)
                {
                    if (chordNote.GraceFret is not { } chordGrace) continue;
                    graces ??= new List<(int StringIndex, int Fret)>(chord.ChordNotes.Count);
                    // One note per string per beat — a malformed chord that repeats
                    // a string must not produce two notes on it (alphaTab's
                    // Score.Finish assumes one).
                    if (graces.Any(g => g.StringIndex == chordNote.StringIndex)) continue;
                    graces.Add((chordNote.StringIndex, chordGrace));
                }
            }
        }

        if (graces == null || graces.Count == 0) return;

        var beat = new AT.Beat { Duration = AT.Duration.Sixteenth, GraceType = AT.GraceType.BeforeBeat };
        foreach (var (stringIndex, fret) in graces)
        {
            beat.AddNote(new AT.Note
            {
                Fret = GpCapoFrets.ToRelative(fret, capo),
                String = stringIndex + 1
            });
        }

        voice.AddBeat(beat);
    }

    private static AT.Note BuildNote(IGuitarSound sound, object owner, Legato legato,
        int remainingTicks,
        bool isTieDestination, int capo)
    {
        var note = new AT.Note
        {
            // ABSOLUTE (chart) -> RELATIVE (Guitar Pro), the inverse of what the
            // importers apply at their fret read. The Staff.Capo written in
            // BuildScore is what makes this mandatory.
            Fret = GpCapoFrets.ToRelative(sound.Fret, capo),
            String = sound.StringIndex + 1 // domain 0 = lowest; alphaTab 1 = lowest
        };

        if (isTieDestination)
        {
            // Continuation beat of a long rhythm — carry only the pitch.
            note.IsTieDestination = true;
            return note;
        }

        // A charted PICK SCRAPE is Guitar Pro's own technique: write the
        // pick slide and nothing it implies. The importer charts the tap,
        // tremolo, fret-hand mute, unpitched slide and ignore back from it
        // (GpPickScrape.Apply), and the split chord from the strum, so a dead
        // note, a slide-out or a tremolo mark here would only add notation the
        // tab never had. The same predicate the import keys on decides it.
        var scrape = GpPickScrape.IsChartedScrape(sound);
        if (!scrape)
        {
            switch (sound.Mute)
            {
                case MuteType.Full: note.IsDead = true; break;
                case MuteType.Palm: note.IsPalmMute = true; break;
            }
        }

        note.HarmonicType = sound.Harmonic switch
        {
            HarmonicType.Normal => AT.HarmonicType.Natural,
            HarmonicType.Pinch => AT.HarmonicType.Pinch,
            _ => AT.HarmonicType.None
        };

        if (sound.Vibrato) note.Vibrato = AT.VibratoType.Slight;
        if (owner is GuitarNote { Accent: true } || (owner is Chord && sound.Accent))
            note.Accentuated = AT.AccentuationType.Normal;

        if (scrape)
        {
            note.SlideOutType = GpPickScrape.IsTowardNut(sound)
                ? AT.SlideOutType.PickSlideDown
                : AT.SlideOutType.PickSlideUp;
        }
        else if (sound.UnpitchedSlide)
        {
            note.SlideOutType = sound.SlideTo.HasValue && sound.SlideTo.Value < sound.Fret
                ? AT.SlideOutType.OutDown
                : AT.SlideOutType.OutUp;
        }
        else if (sound.SlideTo.HasValue)
        {
            note.SlideOutType = AT.SlideOutType.Shift;
        }

        // Domain flags the hammer-on/pull-off DESTINATION; alphaTab marks the
        // origin and resolves the destination during Score.Finish.
        if (legato.Origins.Contains(sound)) note.IsHammerPullOrigin = true;
        if (legato.LeftHandTaps.Contains(sound)) note.IsLeftHandTapped = true;

        AddBends(sound, owner, note, remainingTicks);
        return note;
    }

    private static void AddBends(IGuitarSound sound, object owner, AT.Note note, int remainingTicks)
    {
        if (sound.BendValues.Count == 0) return;

        var position = owner switch
        {
            GuitarNote gn => gn.Position,
            Chord ch => ch.Position,
            _ => FractionalPosition.Zero
        };

        // Bend offsets are 0..60 across the note's written rhythm.
        var spanBeats = Math.Max(sound.Sustain.DoubleValue, (double)remainingTicks / TicksPerBeat);
        if (spanBeats <= 0) spanBeats = 1;

        note.BendType = AT.BendType.Custom;
        note.BendPoints ??= [];
        var hasOrigin = false;
        foreach (var bend in sound.BendValues.OrderBy(b => b.Position))
        {
            var offsetBeats = (bend.Position - position).DoubleValue;
            var offset = Math.Clamp(offsetBeats / spanBeats * 60.0, 0.0, 60.0);
            if (offset < 0.001) hasOrigin = true;
            // Domain step 1.0 = half step; alphaTab bend values are quarter tones.
            note.BendPoints!.Add(new AT.BendPoint(offset, bend.Step * 2.0));
        }

        if (!hasOrigin) note.BendPoints!.Insert(0, new AT.BendPoint(0, 0));
    }
}
