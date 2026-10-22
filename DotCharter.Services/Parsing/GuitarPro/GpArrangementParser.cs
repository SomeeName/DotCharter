using System.Globalization;
using System.Xml.Linq;
using Arrangement = DotCharter.Core.Domain.Project.Arrangement;
using ChartLevel = DotCharter.Core.Domain.Project.ChartLevel;
using Chord = DotCharter.Core.Domain.Guitar.Chord;
using TuningOffset = DotCharter.Core.Domain.Project.TuningOffset;

namespace DotCharter.Services.Parsing.GuitarPro;

/// <summary>
///     Walks a Guitar Pro score document and emits one
///     <see cref="Arrangement"/> per requested track. Owns the <see cref="NoteParser"/>
///     so left-hand-finger hints captured during note parsing are available
///     afterwards to <see cref="DotCharter.Services.Editing.FretAnchors.FretAnchorGenerationService"/>.
/// </summary>
public sealed class GpArrangementParser
{
    public GpNoteParser NoteParser { get; } = new();

    /// <summary>
    ///     The AUTHORED left-hand fingering of the track currently being parsed,
    ///     by (string, position). Filled from the PLAYABLE voice only and handed
    ///     to <see cref="GpPostProcessor.Run" /> so a new chord template prefers
    ///     the file's finger over <c>ChordTemplateFingerSetter</c>'s guess. Reset
    ///     per track, like <see cref="GpNoteParser.Capo" />.
    /// </summary>
    private readonly GpAuthoredFingering _authoredFingers = new();

    /// <summary>
    ///     The AUTHORED chord name of each strum of the track being parsed, by
    ///     position, from the track's chord diagrams (<see cref="GpChordDiagrams" />).
    ///     Playable voice only, reset per track, handed to
    ///     <see cref="GpPostProcessor.Run" /> beside <see cref="_authoredFingers" />.
    /// </summary>
    private readonly GpAuthoredChordNames _authoredChordNames = new();
    private readonly HashSet<IGuitarSound> _graceTargets = [];

    /// <summary>The chord diagrams of the track being parsed (re-read per track).</summary>
    private GpChordDiagrams _diagrams = GpChordDiagrams.Empty;

    /// <summary>Per parsed arrangement, the slide-in ornaments its import materialized
    /// (<see cref="GpPostProcessor.MaterializeSlideIns"/>).</summary>
    public Dictionary<Arrangement, int> SlideInsMaterialized { get; } = [];

    /// <summary>Most notes plus chords one voice of a track may emit; a test lowers it.</summary>
    internal int MaxNotesPerTrack { get; init; } = GpFormatBounds.MaxNotesPerTrack;

    public List<Arrangement> Parse(XDocument doc, List<int> trackIds, GpImportSettings? settings = null)
    {
        settings ??= GpImportSettings.Default;
        var arrangements = new List<Arrangement>();
        var root = doc.Root;
        if (root == null) return arrangements;

        var tracksNode = root.Element("Tracks");
        if (tracksNode == null) return arrangements;

        var masterTrackNode = root.Element("MasterTrack");
        var masterTrackIdsStr = masterTrackNode?.Element("Tracks")?.Value ?? "";
        var globalTrackIds = masterTrackIdsStr.Split([' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.TryParse(s, CultureInfo.InvariantCulture, out var i) ? i : -1)
            .ToList();

        var rhythms = ParseRhythms(root);

        var masterBarsNode = root.Element("MasterBars");
        var barsNode = root.Element("Bars");
        var voicesNode = root.Element("Voices");
        var beatsNode = root.Element("Beats");
        var notesNode = root.Element("Notes");

        var noteMap = BuildIdMap(notesNode, "Note");
        var beatMap = BuildIdMap(beatsNode, "Beat");
        var voiceMap = BuildIdMap(voicesNode, "Voice");
        var barMap = BuildIdMap(barsNode, "Bar");

        foreach (var trackNode in tracksNode.Elements("Track"))
        {
            if (!int.TryParse(trackNode.Attribute("id")?.Value, CultureInfo.InvariantCulture, out var trackId) || !trackIds.Contains(trackId))
                continue;

            var trackIndexInMasterBars = globalTrackIds.IndexOf(trackId);
            if (trackIndexInMasterBars == -1) continue;

            // Normalized (whitespace runs collapse, ends trim) — real GP files
            // carry doubled spaces in their bytes ("Lead (6  String Drop D)"),
            // and this name becomes Arrangement.Name, the export dialog row
            // and the import dialog's name-matching key.
            var name = GpTrackNames.Normalize(trackNode.Element("Name")?.Value);
            if (name.Length == 0) name = "Guitar Pro Track";

            var arr = new Arrangement { Name = name };

            ApplyTrackProperties(trackNode, arr, name);
            // The NAME decides the type (bass/rhythm/combo/vocal/lead — the
            // shared tab-import heuristic); the string count is only the
            // fallback for a keyword-less name, mirroring the GP3-5 binary
            // path so the two importers agree on the one field the Rocksmith
            // route is derived from. Must run AFTER ApplyTrackProperties,
            // which is what fills StringCount from the gpif tuning.
            arr.ArrangementType = ArrangementTypeHeuristics.FromTrackName(name,
                arr.StringCount <= 5 ? ArrangementType.Bass : ArrangementType.Lead);
            // GP frets are capo-RELATIVE, ours are absolute (see GpCapoFrets).
            // Each track carries its own capo, so this is re-set per track — the
            // note parser instance is shared across the whole file.
            NoteParser.Capo = arr.Capo;
            NoteParser.ResetLeftHandTappedNotes();
            _authoredFingers.Clear();
            _authoredChordNames.Clear();
            _graceTargets.Clear();
            _diagrams = GpChordDiagrams.Read(trackNode, arr.StringCount, arr.Capo);

            var level = new ChartLevel { DifficultyIndex = 0 };
            var letRingSet = new HashSet<FractionalPosition>();
            // Tie-roll runs are collected SEPARATELY from authored let-ring runs
            // even though both become hand shapes the same way: a roll's ring-out
            // can end exactly where a following let-ring run begins, and sharing
            // one set would fuse them into a single bar that swallows the run.
            var tieRollSet = new HashSet<FractionalPosition>();

            if (masterBarsNode != null)
            {
                ParseTrackBars(masterBarsNode, trackIndexInMasterBars, barMap, voiceMap, beatMap, noteMap, rhythms,
                    arr, level, arr.StringCount, letRingSet, tieRollSet, settings);
            }

            // LeftHandTappedNotes holds this track's LEFT-hand taps (normalised to
            // hammer-ons) — hand them to Run so AdjustSustains never re-derives a
            // descending one into a pull-off. Right-hand taps stay HopoType.Tap and
            // are deliberately NOT here.
            SlideInsMaterialized[arr] = GpPostProcessor.Run(level, arr, settings,
                tapOriginated: NoteParser.LeftHandTappedNotes,
                authoredFingers: _authoredFingers, authoredChordNames: _authoredChordNames,
                graceTargets: _graceTargets);
            // Sidecar voices get the NOTATION-relevant passes only: tied beats
            // merge (summing their authored NotationDuration) and slide targets
            // resolve. AdjustSustains is deliberately skipped — it is the
            // Rocksmith gameplay normalization (zeroing short sustains), and
            // the sidecar wants the authored data verbatim.
            foreach (var voice in arr.NotationVoices)
            {
                var shim = new ChartLevel();
                foreach (var n in voice.Notes) shim.Notes.Add(n);
                foreach (var c in voice.Chords) shim.Chords.Add(c);
                GpPostProcessor.MergeTiedNotes(shim, arr.StringCount);
                GpPostProcessor.LinkSlides(shim, arr.StringCount);
                voice.Notes.Clear();
                foreach (var n in shim.Notes) voice.Notes.Add(n);
                voice.Chords.Clear();
                foreach (var c in shim.Chords) voice.Chords.Add(c);
            }

            GpHandShapeBuilder.BuildLetRingHandShapes(level, arr, tieRollSet, _diagrams, _authoredFingers);
            GpHandShapeBuilder.BuildLetRingHandShapes(level, arr, letRingSet, _diagrams, _authoredFingers);
            // Let-ring shapes join the chord shapes after Run — re-clamp so the
            // combined set stays overlap-free.
            GpPostProcessor.ClampHandShapes(level);
            // arr.Capo is load-bearing (the pass passes it): without it the
            // generator uses a floor of fret 1 and treats only fret 0 as open, so
            // a capo'd import lays anchors BELOW the capo (validation rule L02).
            // Suggested chord fingerings follow the anchors; a pick scrape
            // sits on the anchor in force at its time.
            GpPickScrape.RunImportFhpPass(arr, level, _authoredFingers.IsAuthoredTemplate,
                GpPickScrape.LastFret(ReadFretCount(trackNode)));

            arr.Levels.Add(level);
            arrangements.Add(arr);
        }

        return arrangements;
    }

    private static Dictionary<int, XElement> BuildIdMap(XElement? container, string elementName)
    {
        var map = new Dictionary<int, XElement>();
        if (container == null) return map;
        foreach (var e in container.Elements(elementName))
        {
            if (int.TryParse(e.Attribute("id")?.Value, CultureInfo.InvariantCulture, out var id))
                map[id] = e;
        }

        return map;
    }

    /// <summary>A rhythm's folded duration in quarter-note units, plus whether it
    /// is a tuplet — tuplet beats are exempt from triplet-feel swing (an explicit
    /// tuplet already spells the swung subdivision; double-swinging it collides
    /// with its own group members).</summary>
    private readonly record struct RhythmInfo(Fraction Duration, bool IsTuplet);

    private static Dictionary<int, RhythmInfo> ParseRhythms(XElement root)
    {
        var rhythms = new Dictionary<int, RhythmInfo>();
        var rhythmsNode = root.Element("Rhythms");
        if (rhythmsNode == null) return rhythms;

        foreach (var rn in rhythmsNode.Elements("Rhythm"))
        {
            if (int.TryParse(rn.Attribute("id")?.Value, CultureInfo.InvariantCulture, out var rId))
            {
                var nv = rn.Element("NoteValue")?.Value ?? "Quarter";
                var frac = nv switch
                {
                    "Whole" => new Fraction(4, 1),
                    "Half" => new Fraction(2, 1),
                    "Quarter" => new Fraction(1, 1),
                    "Eighth" => new Fraction(1, 2),
                    "16th" => new Fraction(1, 4),
                    "32nd" => new Fraction(1, 8),
                    "64th" => new Fraction(1, 16),
                    _ => new Fraction(1, 1)
                };

                var dots = 0;
                var augDot = rn.Element("AugmentationDot");
                if (augDot != null && int.TryParse(augDot.Attribute("count")?.Value, CultureInfo.InvariantCulture, out var d))
                    // Each dot DOUBLES the denominator, so an unbounded count
                    // overflows int and lands on a zero/negative denominator.
                    // Guitar Pro itself authors at most two.
                    dots = Math.Clamp(d, 0, MaxAugmentationDots);

                var addFrac = frac;
                for (var i = 0; i < dots; i++)
                {
                    addFrac = new Fraction(addFrac.Numerator, addFrac.Denominator * 2);
                    frac += addFrac;
                }

                var isTuplet = false;
                var tuplet = rn.Element("PrimaryTuplet");
                // A zero numerator is `new Fraction(den, 0)`; a non-positive
                // denominator inverts the ratio's sign. Neither is a tuplet —
                // ignore the element rather than crash the whole import. The
                // upper bound matters just as much: this ratio is the ONLY
                // unbounded factor in a rhythm's duration (NoteValue is a fixed
                // table and the dots are clamped), so `den="1000000"` produced a
                // note four million quarters long, which ExpandTrill then turned
                // into millions of emitted GuitarNotes.
                if (tuplet != null && int.TryParse(tuplet.Attribute("num")?.Value, CultureInfo.InvariantCulture, out var num) &&
                    int.TryParse(tuplet.Attribute("den")?.Value, CultureInfo.InvariantCulture, out var den) &&
                    num is > 0 and <= GpFormatBounds.MaxTupletValue &&
                    den is > 0 and <= GpFormatBounds.MaxTupletValue)
                {
                    frac *= new Fraction(den, num);
                    isTuplet = true;
                }

                rhythms[rId] = new RhythmInfo(frac, isTuplet);
            }
        }

        return rhythms;
    }

    /// <summary>The staff's <c>&lt;Property name="FretCount"&gt;&lt;Number&gt;</c>, or null.</summary>
    private static int? ReadFretCount(XElement trackNode)
    {
        var value = StaffProperties(trackNode)?.Elements("Property")
            .FirstOrDefault(p => p.Attribute("name")?.Value == "FretCount")?.Element("Number")?.Value;
        return int.TryParse(value, CultureInfo.InvariantCulture, out var count) ? count : null;
    }

    /// <summary>
    ///     The <c>&lt;Properties&gt;</c> holding a track's tuning, capo, fret count and
    ///     chord diagrams: under <c>Staves/Staff</c> in GP7+, directly under the
    ///     <c>&lt;Track&gt;</c> in GP6 (<c>.gpx</c>).
    /// </summary>
    internal static XElement? StaffProperties(XElement trackNode) =>
        trackNode.Element("Staves")?.Element("Staff")?.Element("Properties") ?? trackNode.Element("Properties");

    private static void ApplyTrackProperties(XElement trackNode, Arrangement arr, string name)
    {
        var props = StaffProperties(trackNode);
        if (props == null) return;

        var tuningProp = props.Elements("Property").FirstOrDefault(p => p.Attribute("name")?.Value == "Tuning");
        var pitchesNode = tuningProp?.Element("Pitches");
        if (pitchesNode != null)
        {
            // The gpif <Pitches> list is unbounded, and StringCount drives the
            // per-string allocation loops in every post-pass plus the
            // note-on-a-missing-string guard. The EOF parser rejects > 8 and the
            // GP binary reader clamps to 1..7; match them rather than let a
            // crafted score size the model. An EMPTY list leaves the
            // arrangement's own string count alone, as it always has.
            var pitchValues = pitchesNode.Value.Split([' '], StringSplitOptions.RemoveEmptyEntries);
            if (pitchValues.Length > MaxStringCount) pitchValues = pitchValues[..MaxStringCount];
            arr.StringCount = pitchValues.Length;
            arr.TuningPitches.Clear();

            // The ONE reference table (see TuningUtilities.GetStandardMidiPitches):
            // subtracting a different standard than the namer adds produces a
            // tuning whose offsets and letters disagree, which is what a local
            // 4/5/7-and-fall-back-to-6 table did to every 8-string tab.
            // 5 strings included: the app reads them as E A D G B, so a B E A D G
            // bass imports as -5 -5 -5 -5 -4 and sounds right.
            // The octave follows the type the caller derives right after this (name first,
            // string count as the fallback), so a bass reads one octave down.
            var standard = TuningUtilities.GetStandardMidiPitchesFor(pitchValues.Length,
                ArrangementTypeHeuristics.FromTrackName(name,
                    pitchValues.Length <= 5 ? ArrangementType.Bass : ArrangementType.Lead));

            var offsets = new int[pitchValues.Length];
            for (var i = 0; i < pitchValues.Length; i++)
            {
                var offset = 0;
                if (int.TryParse(pitchValues[i], CultureInfo.InvariantCulture, out var midiPitch) && i < standard.Length)
                    offset = midiPitch - standard[i];
                offsets[i] = offset;
                arr.TuningPitches.Add(new TuningOffset(offset));
            }

            // Keep the display/naming tuning in sync with the offsets — chord
            // naming and the tuning chips read Arrangement.Tuning, which
            // otherwise stays at its E-standard default for GP imports.
            arr.Tuning = TuningUtilities.GetTuningNames(pitchValues.Length, offsets);
        }

        var capoProp = props.Elements("Property").FirstOrDefault(p => p.Attribute("name")?.Value == "CapoFret");
        // Sanitized like the other two tab paths: a negative capo is malformed
        // and would make the fret conversion SUBTRACT, and one past the neck
        // overflowed the conversion's sum into a negative fret (see
        // GpCapoFrets.Sanitize). All three importers fold through the one helper.
        if (capoProp != null && int.TryParse(capoProp.Element("Fret")?.Value, CultureInfo.InvariantCulture, out var capo))
            arr.Capo = GpCapoFrets.Sanitize(capo);
    }

    private void ParseTrackBars(XElement masterBarsNode, int trackIndexInMasterBars,
        Dictionary<int, XElement> barMap, Dictionary<int, XElement> voiceMap, Dictionary<int, XElement> beatMap,
        Dictionary<int, XElement> noteMap, Dictionary<int, RhythmInfo> rhythms, Arrangement arr, ChartLevel level,
        int stringCount, HashSet<FractionalPosition> letRingSet, HashSet<FractionalPosition> tieRollSet,
        GpImportSettings settings)
    {
        var currentPos = new FractionalPosition(0, Fraction.Zero);
        // GP bars carry up to four voice slots ("12 -1 -1 -1"). Slot 0 becomes
        // the playable chart level; slots 1..3 import as NOTATION-ONLY sidecar
        // voices on the arrangement — the sheet renders them, gameplay never
        // sees them (they would otherwise collide with slot 0 on string/time).
        var sidecars = new Dictionary<int, NotationVoice>();
        var slots = new VoiceSlotState[MaxVoiceSlots];
        for (var i = 0; i < slots.Length; i++) slots[i] = new VoiceSlotState();
        var source = new GpifBeatSource(beatMap, noteMap, rhythms, stringCount, settings);
        var playable = new VoiceTarget(level.Notes, level.Chords, letRingSet, tieRollSet,
            ExpandTrills: true, MarkerArrangement: arr, AuthoredFingers: _authoredFingers);

        foreach (var entry in GpMasterBarSequence.Unfold(masterBarsNode))
        {
            var masterBarNode = entry.Element;
            var beatsInMeasure = 4;
            var noteDenominator = 4;
            var timeSigNode = masterBarNode.Element("Time");
            if (timeSigNode != null)
            {
                var timeSigParts = timeSigNode.Value.Split('/');
                if (timeSigParts.Length == 2 && int.TryParse(timeSigParts[0], CultureInfo.InvariantCulture, out var n) &&
                    int.TryParse(timeSigParts[1], CultureInfo.InvariantCulture, out var d))
                {
                    // `d == 0` is the DivideByZeroException; both halves also
                    // size the bar span, hence the upper bounds.
                    beatsInMeasure = Math.Clamp(n, 1, GpFormatBounds.MaxBeatsInMeasure);
                    noteDenominator = Math.Clamp(d, 1, GpFormatBounds.MaxNoteDenominator);
                }
            }

            var barIdsStr = masterBarNode.Element("Bars")?.Value;
            // GP encodes swing as bar-level metadata. Rocksmith has no swing flag,
            // so we apply it at import time by shifting off-beat sub-positions
            // (e.g. 1/2 -> 2/3 for Triplet8th). We have 1/12, 1/24 etc. grids in
            // the editor so swung positions remain editable.
            var feel = masterBarNode.Element("TripletFeel")?.Value ?? "";
            var barEnd = currentPos +
                         new FractionalPosition(0, new Fraction(beatsInMeasure * 4, noteDenominator));

            // EOF markers-as-sections: a MasterBar <Section> (rehearsal mark)
            // becomes an RS phrase at the bar start and, when its name is a valid
            // RS section, a section EventPoint too.
            if (settings.MarkersToSections)
                AddMarker(arr, GpMarkerImport.CreateMarkerEvent(currentPos, ReadMasterBarSection(masterBarNode)));

            if (barIdsStr != null)
            {
                var barIds = barIdsStr.Split([' '], StringSplitOptions.RemoveEmptyEntries);
                if (trackIndexInMasterBars < barIds.Length &&
                    int.TryParse(barIds[trackIndexInMasterBars], CultureInfo.InvariantCulture, out var barId) &&
                    barMap.TryGetValue(barId, out var barNode))
                {
                    var voicesList = barNode.Element("Voices")?.Value
                        .Split([' '], StringSplitOptions.RemoveEmptyEntries);
                    if (voicesList != null)
                    {
                        for (var slot = 0; slot < voicesList.Length && slot < MaxVoiceSlots; slot++)
                        {
                            if (!int.TryParse(voicesList[slot], CultureInfo.InvariantCulture, out var voiceId) || voiceId < 0) continue;
                            if (!voiceMap.TryGetValue(voiceId, out var voiceNode)) continue;

                            var bar = new BarSpan(currentPos, feel, barEnd);
                            if (slot == 0)
                            {
                                ParseVoiceBeats(voiceNode, source, bar, playable, slots[slot]);
                            }
                            else
                            {
                                if (!sidecars.TryGetValue(slot, out var voice))
                                    sidecars[slot] = voice = new NotationVoice { VoiceIndex = slot };
                                // Notation-only: no let-ring hand shapes, no trill
                                // expansion, no beat-text techniques/markers (all
                                // chart/gameplay constructs on the playable voice).
                                ParseVoiceBeats(voiceNode, source, bar, VoiceTarget.NotationOnly(voice), slots[slot]);
                            }
                        }
                    }
                }
            }

            currentPos = barEnd;
        }

        foreach (var voice in sidecars.Values
                     .Where(v => v.Notes.Count > 0 || v.Chords.Count > 0)
                     .OrderBy(v => v.VoiceIndex))
        {
            arr.NotationVoices.Add(voice);
        }
    }

    private const int MaxVoiceSlots = 4;

    /// <summary>
    ///     <c>&lt;AugmentationDot count="40"&gt;</c> doubled the denominator 40
    ///     times: int overflow to a zero (or negative) one. Local rather than in
    ///     <see cref="GpFormatBounds" /> because this element exists only in gpif —
    ///     the binary formats fold the dot into the duration byte.
    /// </summary>
    private const int MaxAugmentationDots = 3;

    /// <summary>Upper bound on the alternating notes one trill may emit. A 32nd
    /// grid over a whole 32-beat bar is 256 steps; the cap only bites on a bar
    /// span no real time signature produces.</summary>
    private const int MaxTrillSteps = 512;

    /// <summary>Most strings an imported track may declare. Matches
    /// <c>EofProGuitarTrackParser</c>'s rejection above 8.</summary>
    private const int MaxStringCount = 8;

    private static FractionalPosition ApplyFeelToPosition(FractionalPosition pos, string feel)
    {
        if (string.IsNullOrEmpty(feel) || feel.Equals("None", StringComparison.OrdinalIgnoreCase)) return pos;

        var f = pos.Fraction;
        if (f.Numerator == 0) return pos;

        bool Eq(int n, int d) => f.Numerator * d == f.Denominator * n;

        Fraction? newFrac = feel switch
        {
            "Triplet8th" => Eq(1, 2) ? new Fraction(2, 3) : null,
            "Triplet16th" => Eq(1, 4) ? new Fraction(1, 3)
                            : Eq(3, 4) ? new Fraction(5, 6) : null,
            "Dotted8th" => Eq(1, 2) ? new Fraction(3, 4) : null,
            "Scottish8th" => Eq(1, 2) ? new Fraction(1, 4) : null,
            "Scottish16th" => Eq(1, 4) ? new Fraction(1, 8)
                            : Eq(3, 4) ? new Fraction(5, 8) : null,
            _ => null
        };

        return newFrac.HasValue ? new FractionalPosition(pos.BeatId, newFrac.Value) : pos;
    }

    /// <summary>One beat of a voice-bar on the notated timeline. Grace beats
    /// carry no bar time (<see cref="Rhythm"/> is zero) and only exist so the
    /// ornament-capture order is preserved.</summary>
    private readonly record struct TimedBeat(
        XElement Node, FractionalPosition Onset, Fraction Rhythm, bool IsTuplet, bool IsGrace);

    /// <summary>The gpif lookups and import settings every voice-bar of a track reads.</summary>
    private sealed record GpifBeatSource(
        Dictionary<int, XElement> BeatMap, Dictionary<int, XElement> NoteMap, Dictionary<int, RhythmInfo> Rhythms,
        int StringCount, GpImportSettings Settings)
    {
        public XElement? Note(string idStr)
            => int.TryParse(idStr, CultureInfo.InvariantCulture, out var id) && NoteMap.TryGetValue(id, out var node)
                ? node
                : null;
    }

    /// <summary>One master bar on the notated timeline: its start, its triplet feel and its end.</summary>
    private readonly record struct BarSpan(FractionalPosition Start, string Feel, FractionalPosition End);

    /// <summary>
    ///     Where a voice slot's beats go and which chart constructs it gets. The
    ///     playable voice gets let-ring and tie-roll sets, trill expansion, beat-text
    ///     techniques/markers and authored fingers; a notation-only sidecar gets none.
    /// </summary>
    private sealed record VoiceTarget(
        IList<GuitarNote> Notes, IList<Chord> Chords, HashSet<FractionalPosition>? LetRingSet,
        HashSet<FractionalPosition>? TieRollSet, bool ExpandTrills, Arrangement? MarkerArrangement,
        GpAuthoredFingering? AuthoredFingers)
    {
        public static VoiceTarget NotationOnly(NotationVoice voice)
            => new(voice.Notes, voice.Chords, LetRingSet: null, TieRollSet: null, ExpandTrills: false,
                MarkerArrangement: null, AuthoredFingers: null);
    }

    /// <summary>The carries of one voice slot. Each lives for the whole track walk,
    /// not one bar: what it carries can cross a barline.</summary>
    private sealed class VoiceSlotState
    {
        /// <summary>A grace beat can precede its target across a barline, so the
        /// pending ornament lives per voice slot for the whole track walk.</summary>
        public GpBeatCarry.PendingGrace? PendingGrace;

        /// <summary>Tie-roll state: a rolled chord's ring-out beat can sit in the next bar.</summary>
        public GpBeatCarry.TieRollState TieRoll { get; } = new();

        /// <summary>Same lifetime, same reason: the rest that articulates a bar's LAST
        /// note falls in the next bar, which is a different ParseVoiceBeats call.</summary>
        public GpBeatCarry.StaccatoCarry StaccatoCarry { get; } = new();
    }

    /// <summary>Where a timed beat sounds (its swung onset) and for how long.</summary>
    private readonly record struct BeatPlacement(FractionalPosition Position, FractionalPosition Sustain);

    private void ParseVoiceBeats(XElement voiceNode, GpifBeatSource source, BarSpan bar, VoiceTarget target,
        VoiceSlotState slot)
    {
        var beatIds = voiceNode.Element("Beats")?.Value.Split([' '],
            StringSplitOptions.RemoveEmptyEntries);
        if (beatIds == null) return;

        var timeline = BuildTimeline(beatIds, source, bar.Start);
        var swung = SwingOnsets(timeline, bar.Feel);

        // Pass 2 — emit notes. Each beat's EFFECTIVE duration is the gap to the
        // next beat's swung onset (identical to the notated rhythm when nothing
        // swings), so swung bars stay contiguous: ties merge, link-next sustains
        // land exactly on their targets, and chord handshapes don't overlap.
        // The authored rhythm is kept separately as the NotationDuration hint.
        for (var i = 0; i < timeline.Count; i++)
        {
            // GP grace beats (<GraceNotes>BeforeBeat|OnBeat</GraceNotes>)
            // carry NO bar time — GP happily adds them to an already-full
            // bar. Capture the ornament for the next struck note and do NOT
            // advance the cursor: advancing (the old behavior) shifted every
            // following onset in the bar late.
            if (timeline[i].IsGrace)
                CaptureGrace(timeline[i], source, slot);
            else
                ParseTimedBeat(timeline[i], PlaceBeat(timeline, swung, i), source, bar, target, slot);

            // Beats, repeats and trills multiply into notes the file never
            // spells out, so the bound is on what has been EMITTED.
            if (target.Notes.Count + target.Chords.Count > MaxNotesPerTrack)
                throw new InvalidDataException($"The track expands to more than {MaxNotesPerTrack} notes and chords.");
        }
    }

    /// <summary>
    ///     Pass 1 — the notated timeline. Swing (triplet feel) placement needs
    ///     the NEXT beat's swung onset to size each beat's effective duration,
    ///     so onsets are collected before any note is emitted.
    /// </summary>
    private static List<TimedBeat> BuildTimeline(string[] beatIds, GpifBeatSource source,
        FractionalPosition measurePos)
    {
        var timeline = new List<TimedBeat>();
        var cursor = measurePos;
        foreach (var beatIdStr in beatIds)
        {
            if (!int.TryParse(beatIdStr, CultureInfo.InvariantCulture, out var beatId) ||
                !source.BeatMap.TryGetValue(beatId, out var beatNode))
            {
                continue;
            }

            if (beatNode.Element("GraceNotes") != null)
            {
                timeline.Add(new TimedBeat(beatNode, cursor, Fraction.Zero, IsTuplet: false, IsGrace: true));
                continue;
            }

            var rhythmIdStr = beatNode.Element("Rhythm")?.Attribute("ref")?.Value;
            var rhythm = new RhythmInfo(new Fraction(1, 1), false);
            if (rhythmIdStr != null && int.TryParse(rhythmIdStr, CultureInfo.InvariantCulture, out var rId) &&
                source.Rhythms.TryGetValue(rId, out var r))
            {
                rhythm = r;
            }

            timeline.Add(new TimedBeat(beatNode, cursor, rhythm.Duration, rhythm.IsTuplet, IsGrace: false));
            cursor += new FractionalPosition(0, rhythm.Duration);
        }

        return timeline;
    }

    /// <summary>
    ///     Swung onset per beat. A beat that IS a tuplet never swings — the
    ///     explicit tuplet already spells the swung subdivision, and shifting it
    ///     lands it on top of its own group (the real-world case: a 16th-triplet
    ///     group starting at the off-beat eighth slot, whose first note Eq(1,2)
    ///     would otherwise shove onto the second).
    /// </summary>
    private static FractionalPosition[] SwingOnsets(List<TimedBeat> timeline, string feel)
    {
        var swung = new FractionalPosition[timeline.Count];
        for (var i = 0; i < timeline.Count; i++)
        {
            swung[i] = timeline[i].IsGrace || timeline[i].IsTuplet
                ? timeline[i].Onset
                : ApplyFeelToPosition(timeline[i].Onset, feel);
        }

        return swung;
    }

    /// <summary>A timed beat sounds at its swung onset and lasts until the next
    /// timed beat's swung onset (or its notated end when it is the last).</summary>
    private static BeatPlacement PlaceBeat(List<TimedBeat> timeline, FractionalPosition[] swung, int i)
    {
        var entry = timeline[i];
        var placementPos = swung[i];

        var nextTimed = -1;
        for (var j = i + 1; j < timeline.Count; j++)
        {
            if (!timeline[j].IsGrace)
            {
                nextTimed = j;
                break;
            }
        }

        var notatedEnd = entry.Onset + new FractionalPosition(0, entry.Rhythm);
        var effectiveEnd = nextTimed >= 0 ? swung[nextTimed] : notatedEnd;
        // Defensive: never let a swing shift invert a beat.
        var sustainPos = effectiveEnd.CompareTo(placementPos) > 0
            ? effectiveEnd - placementPos
            : new FractionalPosition(0, entry.Rhythm);
        return new BeatPlacement(placementPos, sustainPos);
    }

    private static string[]? ReadBeatNoteIds(XElement beatNode)
        => beatNode.Element("Notes")?.Value.Split([' '], StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Captures a grace beat's notes as the slot's pending ornament for
    /// the next struck note.</summary>
    private void CaptureGrace(TimedBeat entry, GpifBeatSource source, VoiceSlotState slot)
    {
        var notesList = ReadBeatNoteIds(entry.Node);
        if (notesList is not { Length: > 0 }) return;

        // GP writes "OnBeat" or "BeforeBeat"; anything else (and the
        // GP6 default) reads as the traditional before-beat grace.
        var beforeBeat = !string.Equals(entry.Node.Element("GraceNotes")?.Value, "OnBeat",
            StringComparison.OrdinalIgnoreCase);
        var grace = new GpBeatCarry.PendingGrace(beforeBeat);
        foreach (var graceIdStr in notesList)
        {
            if (source.Note(graceIdStr) is not { } graceNode) continue;
            var g = NoteParser.ParseNote(graceNode, entry.Onset, FractionalPosition.Zero,
                source.StringCount, false);
            if (!NoteParser.LastStringValid) continue;
            // A grace with a slide (shift or legato) parses onto
            // the grace's SlideTo (-1) / LinkNext — capture it so
            // the ornament becomes a real slide-into note, not
            // just a fret. A grace that hammers or pulls into the target
            // carries GP's HopoOrigin on the grace note itself (the target holds
            // the destination half), so it is read from the node: the target's
            // flag alone would leave the grace a bare fret hint.
            grace.Entries.Add(new GpBeatCarry.GraceEntry(g.StringIndex, g.Fret,
                g.SlideTo.HasValue || g.LinkNext, GpNoteParser.IsHopoOrigin(graceNode)));
        }

        if (grace.Entries.Count > 0) slot.PendingGrace = grace;
    }

    private void ParseTimedBeat(TimedBeat entry, BeatPlacement place, GpifBeatSource source, BarSpan bar,
        VoiceTarget target, VoiceSlotState slot)
    {
        var beatNode = entry.Node;
        var notesList = ReadBeatNoteIds(beatNode);
        var placementPos = place.Position;

        if (target.LetRingSet != null && BeatHasLetRing(beatNode, source.NoteMap)) target.LetRingSet.Add(placementPos);

        // EOF beat-text import (playable voice only): "T"/"S"/"P" → a note
        // technique; any other free text → a section/phrase marker.
        var settings = source.Settings;
        var beatText = target.MarkerArrangement != null && (settings.BeatTextTechniques || settings.MarkersToSections)
            ? ReadBeatText(beatNode)
            : null;
        GpMarkerImport.BeatTextTechnique? technique = settings.BeatTextTechniques &&
                                                      GpMarkerImport.TryParseTechnique(beatText, out var beatTech)
            ? beatTech
            : null;

        // Guitar Pro spells a ROLLED chord as growing TIED chords: each beat
        // repeats every string already ringing as a tie DESTINATION and adds
        // one new one. Imported literally that is a stack of ever-bigger
        // chords (plus a junk chord template per stack), when the player
        // strikes each string exactly once and lets it ring. Collapse it to
        // the struck notes and let it become ONE hand shape — the same shape
        // a properly-tabbed let-ring run gets. Playable voice only: the
        // notation sidecars want the authored spelling verbatim.
        if (target.TieRollSet != null && notesList is { Length: > 0 })
        {
            notesList = GpBeatCarry.FilterTieRoll(slot.TieRoll, notesList, source.NoteMap, placementPos, entry.Rhythm,
                target.TieRollSet);
            if (notesList == null) return; // the beat was pure ring-out
        }

        var emittedNotesFrom = target.Notes.Count;
        var emittedChordsFrom = target.Chords.Count;

        var hasTremolo = beatNode.Element("Tremolo") != null;
        if (notesList is not { Length: > 0 })
        {
            // A rest breaks the grace→target adjacency.
            slot.PendingGrace = null;
        }
        else if (notesList.Length == 1)
        {
            EmitSingleNote(notesList[0], entry, place, hasTremolo, technique, source, bar, target, slot);
        }
        else
        {
            EmitChord(notesList, entry, place, hasTremolo, technique, source, target, slot);
        }

        ApplyBeatBassPicking(beatNode, target, emittedNotesFrom, emittedChordsFrom);

        ObserveBeatCarries(entry, placementPos, notesList is { Length: > 0 }, emittedNotesFrom,
            target.Chords.Count > emittedChordsFrom, settings, target, slot);

        // A non-technique beat text imports as a phrase/section marker.
        if (target.MarkerArrangement != null && settings.MarkersToSections && technique == null &&
            !string.IsNullOrWhiteSpace(beatText))
        {
            AddMarker(target.MarkerArrangement, GpMarkerImport.CreateMarkerEvent(placementPos, beatText));
        }
    }

    private void EmitSingleNote(string noteIdStr, TimedBeat entry, BeatPlacement place, bool hasTremolo,
        GpMarkerImport.BeatTextTechnique? technique, GpifBeatSource source, BarSpan bar, VoiceTarget target,
        VoiceSlotState slot)
    {
        if (source.Note(noteIdStr) is not { } noteNode) return;

        var note = NoteParser.ParseNote(noteNode, place.Position, place.Sustain, source.StringCount, hasTremolo);
        var trill = NoteParser.LastTrill;
        RecordAuthoredFinger(target.AuthoredFingers, note, place.Position);
        // A note on a string the track does not have is inert —
        // every post-import pass bounds-checks it away, so it
        // would only ever be a phantom sound on the level. Drop
        // it at the source, as the binary reader always has.
        if (!NoteParser.LastStringValid)
        {
            slot.PendingGrace = null;
            return;
        }

        // Keep the AUTHORED rhythm as a notation hint before
        // GpPostProcessor.AdjustSustains discards short
        // sustains — the sheet view renders it verbatim.
        note.NotationDuration = entry.Rhythm;
        GpBeatCarry.ApplyPendingGrace(note, ref slot.PendingGrace, target.Notes, target.Chords, _graceTargets);
        if (technique is { } tech) GpMarkerImport.ApplyTechnique(note, tech);
        if (trill.HasValue && target.ExpandTrills)
        {
            ExpandTrill(target.Notes, note, trill.Value.UpperFret, bar.End);
        }
        else
        {
            // Notation-only voices keep the single note; the
            // vibrato flag stands in for the trill glyph.
            if (trill.HasValue) note.Vibrato = true;
            target.Notes.Add(note);
        }
    }

    private void EmitChord(string[] notesList, TimedBeat entry, BeatPlacement place, bool hasTremolo,
        GpMarkerImport.BeatTextTechnique? technique, GpifBeatSource source, VoiceTarget target, VoiceSlotState slot)
    {
        var chord = new Chord { Position = place.Position, NotationDuration = entry.Rhythm };
        foreach (var nStr in notesList)
        {
            if (source.Note(nStr) is not { } noteNode) continue;
            var chordNote = NoteParser.ParseChordNote(noteNode, place.Position, place.Sustain,
                source.StringCount, hasTremolo);
            RecordAuthoredFinger(target.AuthoredFingers, chordNote, place.Position);
            if (NoteParser.LastStringValid) chord.ChordNotes.Add(chordNote);
        }

        // An accent on any string accents the strum — mirror it on
        // the chord so RS XML / rscp chord-level accent survives.
        chord.Accent = chord.ChordNotes.Any(cn => cn.Accent);

        if (technique is { } tech)
        {
            foreach (var cn in chord.ChordNotes)
                GpMarkerImport.ApplyTechnique(cn, tech);
        }

        GpBeatCarry.ApplyPendingGrace(chord, ref slot.PendingGrace, target.Notes, target.Chords, _graceTargets);
        // The tab's chord diagram names and fingers the strum —
        // playable voice only, like the per-note fingers above. After
        // the grace: an on-beat grace moves the chord, and the side
        // channels are keyed by where the template builder finds it.
        if (target.AuthoredFingers != null)
            _diagrams.Record(entry.Node, chord, _authoredChordNames, target.AuthoredFingers);
        if (chord.ChordNotes.Count > 0) target.Chords.Add(chord);
    }

    /// <summary>Feeds the beat just emitted to the slot's tie-roll and staccato
    /// carries (playable voice only).</summary>
    private static void ObserveBeatCarries(TimedBeat entry, FractionalPosition placementPos, bool beatSounded,
        int emittedNotesFrom, bool beatEmittedChord, GpImportSettings settings, VoiceTarget target,
        VoiceSlotState slot)
    {
        if (target.TieRollSet == null) return;

        GpBeatCarry.ObserveTieRollBeat(slot.TieRoll, target.Notes, emittedNotesFrom, beatEmittedChord,
            placementPos, sounded: beatSounded);

        if (settings.PreserveShortRests)
        {
            GpBeatCarry.ObserveStaccatoBeat(slot.StaccatoCarry, target.Notes, emittedNotesFrom, placementPos,
                beatSounded, entry.Rhythm, beatEmittedChord);
        }
    }

    /// <summary>
    ///     Guitar Pro 7+ writes slap and pop as BEAT properties (<c>Slapped</c> / <c>Popped</c>
    ///     under the beat's <c>Properties</c>), as alphaTab's writer and parser do; the note
    ///     properties of the same name <c>GpNoteParser</c> reads are the older spelling. A
    ///     note's own property wins; the beat's fills every sound the beat emitted.
    /// </summary>
    private static void ApplyBeatBassPicking(XElement beatNode, VoiceTarget target, int notesFrom, int chordsFrom)
    {
        var picking = BassPickingType.None;
        foreach (var property in beatNode.Element("Properties")?.Elements("Property") ?? [])
        {
            var name = property.Attribute("name")?.Value;
            if (name == "Slapped") picking = BassPickingType.Slap;
            else if (name == "Popped") picking = BassPickingType.Pop;
            if (picking != BassPickingType.None) break;
        }

        if (picking == BassPickingType.None) return;
        for (var i = notesFrom; i < target.Notes.Count; i++)
        {
            if (target.Notes[i].BassPicking == BassPickingType.None) target.Notes[i].BassPicking = picking;
        }

        for (var i = chordsFrom; i < target.Chords.Count; i++)
        {
            foreach (var chordNote in target.Chords[i].ChordNotes)
            {
                if (chordNote.BassPicking == BassPickingType.None) chordNote.BassPicking = picking;
            }
        }
    }

    private static string? ReadBeatText(XElement beatNode)
        => beatNode.Element("FreeText")?.Value ?? beatNode.Attribute("text")?.Value;

    /// <summary>The rehearsal-mark name of a MasterBar: the <c>Section</c>'s Text
    /// (preferred) or Letter, or null when the bar carries no section.</summary>
    private static string? ReadMasterBarSection(XElement masterBarNode)
    {
        var section = masterBarNode.Element("Section");
        if (section == null) return null;
        var text = section.Element("Text")?.Value;
        return !string.IsNullOrWhiteSpace(text) ? text : section.Element("Letter")?.Value;
    }

    /// <summary>Adds a phrase/section marker to the arrangement, keeping the
    /// phrase list in sync and skipping an exact duplicate at the same position.</summary>
    /// <summary>Adds a marker event (phrase, plus a section when the name is an
    /// RS section) without duplicating one already at that position. Shared with
    /// the Songsterr importer, which reads the same markers out of its own
    /// payload.</summary>
    internal static void AddMarker(Arrangement arr, EventPoint? ev)
    {
        if (ev == null) return;
        if (arr.EventPoints.Any(e => e.Position.CompareTo(ev.Position) == 0 &&
                                     e.Phrase == ev.Phrase && e.Section == ev.Section))
        {
            return;
        }

        arr.EventPoints.Add(ev);
        if (!arr.Phrases.Contains(ev.Phrase)) arr.Phrases.Add(ev.Phrase);
    }

    /// <summary>
    ///     Records the just-parsed note's AUTHORED left-hand finger against its
    ///     (string, position), when the file supplied one and the string is real.
    ///     No-op for the notation-only sidecar voices, which pass null.
    /// </summary>
    private void RecordAuthoredFinger(GpAuthoredFingering? authoredFingers, IGuitarSound note,
        FractionalPosition position)
    {
        if (authoredFingers == null || !NoteParser.LastStringValid) return;
        if (NoteParser.LastLeftFingering is { } finger)
            authoredFingers.Set(note.StringIndex, position, finger);
    }

    /// <summary>
    ///     Renders a trill as the alternating HO/PO run RS needs. Shared with the
    ///     GP3-5 BINARY importer (<c>GpLegacyImporter</c>) so a trilled score
    ///     imports identically as .gp5 and as .gpif — the binary path must not skip
    ///     the trill bytes.
    /// </summary>
    internal static void ExpandTrill(IList<GuitarNote> notesOut, GuitarNote source, int upperFret,
        FractionalPosition barEnd)
    {
        // Rocksmith has no native trill, so render it as rapid alternation between
        // the base fret and the trill's upper fret with HO/PO markers. We use
        // 32nd-note subdivisions (GP's default trill rendering rate). Clamp to
        // the bar boundary so a too-long source rhythm doesn't bleed into the
        // following bar.
        var sourceEnd = source.Position + source.Sustain;
        var clampedEnd = sourceEnd > barEnd ? barEnd : sourceEnd;
        var availableBeats = (clampedEnd - source.Position).DoubleValue;
        // The bar end comes from the file's time signature, so an absurd one
        // would make this emit an unbounded number of GuitarNotes. The time
        // signature is clamped; this is the belt to that braces, and also
        // covers a NaN/huge span arriving from anywhere else.
        var rawSteps = Math.Round(availableBeats * 8.0);
        var stepCount = double.IsFinite(rawSteps) ? (int)Math.Clamp(rawSteps, 0.0, MaxTrillSteps) : 0;
        if (stepCount < 2)
        {
            // Sustain too short to alternate - fall back to base note + vibrato hint.
            source.Vibrato = true;
            notesOut.Add(source);
            return;
        }

        var baseFret = source.Fret;
        var goingUp = upperFret > baseFret;

        for (var i = 0; i < stepCount; i++)
        {
            var pos = source.Position + new FractionalPosition(0, new Fraction(i, 8));
            var isUpper = i % 2 == 1;
            var note = new GuitarNote
            {
                Position = pos,
                Sustain = new FractionalPosition(0, new Fraction(1, 8)),
                StringIndex = source.StringIndex,
                Fret = isUpper ? upperFret : baseFret,
                Mute = source.Mute,
                Harmonic = source.Harmonic,
                Accent = i == 0 && source.Accent,
                Hopo = i == 0
                    ? HopoType.None
                    : isUpper
                        ? (goingUp ? HopoType.HammerOn : HopoType.PullOff)
                        : (goingUp ? HopoType.PullOff : HopoType.HammerOn)
            };
            notesOut.Add(note);
        }
    }

    private static bool BeatHasLetRing(XElement beatNode, Dictionary<int, XElement> noteMap)
    {
        // Let-ring is spelled TWO ways across Guitar Pro versions and BOTH must
        // be detected: GP6/7 write a <Property name="LetRing"/> (on the beat or
        // the note), while GP8 writes a bare <LetRing/> CHILD ELEMENT on the
        // Note. Only the Property form was checked before, so let-ring authored
        // in GP8 was silently dropped — the arpeggio it was meant to bar never
        // became a hand shape. If ANY note on the beat rings, the whole beat is
        // let-ring (GpHandShapeBuilder groups consecutive let-ring beats).
        if (beatNode.Descendants("Property").Any(p => p.Attribute("name")?.Value == "LetRing")
            || beatNode.Elements("LetRing").Any())
        {
            return true;
        }

        var notesList = beatNode.Element("Notes")?.Value
            .Split([' '], StringSplitOptions.RemoveEmptyEntries);
        if (notesList == null) return false;

        foreach (var nStr in notesList)
        {
            if (int.TryParse(nStr, CultureInfo.InvariantCulture, out var nId) && noteMap.TryGetValue(nId, out var noteNode))
            {
                if (noteNode.Elements("LetRing").Any()
                    || noteNode.Descendants("Property").Any(p => p.Attribute("name")?.Value == "LetRing"))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
