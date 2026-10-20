namespace DotCharter.Services.Editing.Validation;

/// <summary>
///     Prebuilt lookup data + helper queries passed to every
///     <see cref="IArrangementRule"/> during a single <c>Validate</c> call.
///     Building the noguitar / tone-change / phrase tables once avoids
///     repeated O(n) scans inside the hot per-note loops of each rule.
/// </summary>
public sealed class ArrangementValidationContext
{
    public Arrangement Arrangement { get; }
    public IList<BeatMarker> Beats { get; }
    public double? TotalAudioLengthMs { get; }

    /// <summary>
    ///     The project the arrangement belongs to, for the few facts that are
    ///     PROJECT-level — the authored tone bodies above all, which live on the
    ///     project while everything that references them lives on an arrangement.
    ///     <para>
    ///         <b>Null means UNKNOWN, never "empty".</b> A caller that does not
    ///         pass it must not make rules report project-level faults, or a
    ///         partial context turns into a screenful of false positives. Every
    ///         rule that reads this has to no-op on null.
    ///     </para>
    /// </summary>
    public ChartProject? Project { get; }

    public IReadOnlyList<(double startMs, double endMs)> NoguitarIntervals { get; }
    public IReadOnlySet<long> ToneChangeTimesMs { get; }
    public IReadOnlySet<long> PhraseTimesMs { get; }
    public IReadOnlySet<long> MoverPhraseTimesMs { get; }

    /// <summary>
    ///     Every phrase marker from BOTH stores (<see cref="PhraseMarkers.All" />),
    ///     built once per pass.
    ///     <para>
    ///         <b>A rule must never read <c>EventPoint.Phrase</c> on its own to
    ///         find phrases.</b> The RS-XML and psarc readers fill
    ///         <c>Arrangement.Phrases</c> + <c>PhraseIterations</c> instead, so a
    ///         rule keyed on the editor's store finds ZERO phrases in every
    ///         imported chart and reports "no problems found" — 0 markers via
    ///         <c>EventPoint</c> against 2,596 via <c>PhraseIterations</c> over
    ///         <c>TestData/EOF</c>. It caught <c>PhraseRule</c>, the empty-phrase
    ///         gate and the phrase-start anchor fixer.
    ///     </para>
    ///     <para>
    ///         Shared here because three rules want it and
    ///         <see cref="PhraseMarkers.All" /> allocates a list plus a hash set
    ///         every call — this runs after every history step.
    ///     </para>
    /// </summary>
    public IReadOnlyList<PhraseMarkers.PhraseMarker> Phrases { get; }

    /// <summary><see cref="Phrases" /> minus the structural ones (COUNT / END /
    /// noguitar) — what anything asking a phrase about the fret hand wants.</summary>
    public IReadOnlyList<PhraseMarkers.PhraseMarker> PlayablePhrases { get; }

    private readonly Dictionary<ChartLevel, LevelAnchorIndex> _anchorIndexes = [];
    private readonly Dictionary<int, ChordTemplate> _templatesById = [];

    /// <summary>Per-pass memo for <see cref="ChordTemplateFor" />'s shape
    /// fallback. Keyed by REFERENCE (<c>Chord</c> is a plain class) and built
    /// lazily — only a chord with no resolvable declared id ever enters it.</summary>
    private Dictionary<Chord, ChordTemplate?>? _shapeFallback;

    private ArrangementValidationContext(
        Arrangement arrangement,
        IList<BeatMarker> beats,
        double? totalAudioLengthMs,
        IReadOnlyList<(double, double)> noguitarIntervals,
        IReadOnlySet<long> toneChangeTimes,
        IReadOnlySet<long> phraseTimes,
        IReadOnlySet<long> moverPhraseTimes,
        ChartProject? project)
    {
        Arrangement = arrangement;
        Beats = beats;
        TotalAudioLengthMs = totalAudioLengthMs;
        Project = project;
        NoguitarIntervals = noguitarIntervals;
        ToneChangeTimesMs = toneChangeTimes;
        PhraseTimesMs = phraseTimes;
        MoverPhraseTimesMs = moverPhraseTimes;

        var phrases = PhraseMarkers.All(arrangement);
        Phrases = phrases;
        var playable = new List<PhraseMarkers.PhraseMarker>(phrases.Count);
        foreach (var phrase in phrases)
        {
            if (!PhraseMarkers.IsStructural(phrase.Name))
                playable.Add(phrase);
        }

        PlayablePhrases = playable;

        // First id wins, which is what `ChordTemplates.FirstOrDefault(t => t.Id
        // == …)` returned. Negative ids are indexed like any other: `-1` is the
        // DEFAULT of ChordTemplate.Id, so an unnumbered template really can sit
        // in the collection and the scan really did find it.
        foreach (var template in arrangement.ChordTemplates)
            _templatesById.TryAdd(template.Id, template);
    }

    /// <summary><paramref name="project" /> is optional and defaults to unknown —
    /// see <see cref="Project" /> for why that is not the same as empty.</summary>
    public static ArrangementValidationContext Build(
        Arrangement arrangement, IList<BeatMarker> beats, double? totalAudioLengthMs,
        ChartProject? project = null)
    {
        return new ArrangementValidationContext(
            arrangement,
            beats,
            totalAudioLengthMs,
            BuildNoguitarIntervals(arrangement, beats),
            BuildToneChangeTimes(arrangement, beats),
            BuildPhraseTimes(arrangement, beats, includeMover: false),
            BuildPhraseTimes(arrangement, beats, includeMover: true, moverOnly: true),
            project);
    }

    // ---- queries ---------------------------------------------------------

    public bool IsInsideNoguitar(double timeMs)
    {
        foreach (var (s, e) in NoguitarIntervals)
        {
            if (timeMs >= s && timeMs < e)
                return true;
        }

        return false;
    }

    public bool IsOnToneChange(double timeMs) => IsNear(ToneChangeTimesMs, timeMs);
    public bool IsOnPhraseBoundary(double timeMs) => IsNear(PhraseTimesMs, timeMs);
    public bool IsOnMoverPhraseBoundary(double timeMs) => IsNear(MoverPhraseTimesMs, timeMs);

    /// <summary>
    ///     The non-mover phrase-boundary times in ms, ascending — built once per
    ///     context, lazily, because only <see cref="HasPhraseChangeOnSustain" />
    ///     wants them and a level with no Link-Next sustains asks nothing.
    /// </summary>
    private double[]? _nonMoverPhraseTimesMs;

    private double[] NonMoverPhraseTimesMs
    {
        get
        {
            if (_nonMoverPhraseTimesMs is not null) return _nonMoverPhraseTimesMs;

            var times = new List<double>();
            foreach (var ep in Arrangement.EventPoints)
            {
                if (string.IsNullOrEmpty(ep.Phrase)) continue;
                if (ep.Phrase.StartsWith("mover", StringComparison.OrdinalIgnoreCase)) continue;
                times.Add(ep.Position.GetPositionMs(Beats));
            }

            times.Sort();
            return _nonMoverPhraseTimesMs = times.ToArray();
        }
    }

    /// <summary>
    ///     True when a non-mover phrase boundary falls inside
    ///     <c>(start, end]</c> of this note's sustain.
    ///     <para>
    ///         Walking EVERY <c>Arrangement.EventPoint</c> with a
    ///         <c>GetPositionMs</c> per point, per Link-Next note, with no early
    ///         break on the (commonest) negative answer would be
    ///         O(linkNextNotes x eventPoints) per level — Link-Next is
    ///         pervasive in RS charts — on the UI thread, twice per chart load. So it is a binary
    ///         search over a once-built sorted array; the predicate is an
    ///         existence test, so order does not change the answer.
    ///     </para>
    /// </summary>
    public bool HasPhraseChangeOnSustain(GuitarNote note)
    {
        if (note.Sustain == FractionalPosition.Zero) return false;
        var startMs = note.Position.GetPositionMs(Beats);
        var endMs = note.EndPosition.GetPositionMs(Beats);

        var times = NonMoverPhraseTimesMs;
        // Upper bound: the first time strictly greater than startMs. If that one
        // is not past endMs there is a boundary in the half-open window, and if
        // it is, no later one can be either.
        int lo = 0, hi = times.Length;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (times[mid] <= startMs) lo = mid + 1;
            else hi = mid;
        }

        return lo < times.Length && times[lo] <= endMs;
    }

    /// <summary>
    ///     The chord template with this id, or null — the indexed replacement for
    ///     the per-sound <c>ChordTemplates.FirstOrDefault(t =&gt; t.Id == …)</c>
    ///     scans in <see cref="Rules.HandShapeRule" /> and
    ///     <see cref="Rules.IndexFingerAnchorRule" />.
    ///     <para>
    ///         <b>It is the RAW index and does not answer "what shape does this
    ///         sound play".</b> It does NOT reproduce
    ///         <c>ChordTemplateResolver.FindById</c>'s "negative id means
    ///         untemplated" rule — <c>-1</c> is <c>ChordTemplate.Id</c>'s own
    ///         default, so an unnumbered template really can sit in the
    ///         collection and a negative lookup really does find it. Ask
    ///         <see cref="ChordTemplateFor(Chord)" /> for a chord, which is the
    ///         only sanctioned way for a rule to get one.
    ///     </para>
    /// </summary>
    public ChordTemplate? ChordTemplateById(int templateId) =>
        _templatesById.TryGetValue(templateId, out var template) ? template : null;

    /// <summary>
    ///     The grip a HAND SHAPE declares, or null when it declares none.
    ///     <b>Every rule that looks a hand shape's template up asks this</b>, so
    ///     the two that judge a shape cannot disagree about what it says.
    ///     <para>
    ///         They did. <c>HandShapeRule</c> (I46) called
    ///         <see cref="ChordTemplateById" /> with no negative guard while
    ///         <c>HandShapeContentRule</c> (E05) called
    ///         <c>ChordTemplateResolver.FindById</c>, which is null for any
    ///         negative id — so on an untemplated shape (<c>TemplateId == -1</c>,
    ///         the field's own default) E05 went silent while I46 judged the
    ///         shape against whichever UNNUMBERED template happened to sit first
    ///         in the collection. A false negative and a false positive on the
    ///         same input. <c>FindById</c>'s rule wins: a negative id declares
    ///         nothing, and the shape is reported as such (D07) rather than
    ///         skipped.
    ///     </para>
    /// </summary>
    public ChordTemplate? HandShapeTemplate(HandShape handShape)
    {
        ArgumentNullException.ThrowIfNull(handShape);
        return handShape.TemplateId < 0 ? null : ChordTemplateById(handShape.TemplateId);
    }

    /// <summary>
    ///     The template that decides what a CHORD's fingering will be — the one
    ///     it declares, or, failing that, the one the arrangement already
    ///     declares for the shape its notes actually play. Null means the
    ///     question CANNOT BE ANSWERED, not "there is nothing wrong".
    ///     <para>
    ///         <b>A <c>TemplateId</c> of <c>-1</c> is not a licence to skip the
    ///         chord.</b> Every rule that read <c>chord.TemplateId</c> guarded it
    ///         with <c>&gt;= 0</c> / <c>&lt; 0 ⇒ continue</c>, which turns a
    ///         missing input into a clean bill of health: I44/I45 in
    ///         <c>ChordRule</c> and L06/L07/L08 in <c>TemplateAndOverlapRule</c>
    ///         reported nothing at all for such a chord. Chords reach the
    ///         validator on the sentinel (the tab importers' idempotency skips
    ///         leave some, and hand-authored/partial states exist), and the shape
    ///         is still knowable: <c>ChartSpacingEnforcer</c> resolves exactly
    ///         this key on the next save, so the template returned here IS the
    ///         one the chord will end up carrying.
    ///     </para>
    ///     <para>
    ///         The fallback keys on <see cref="ArpeggioFlavour.Plain" />, matching
    ///         <c>ChordTemplateResolver.ResolveForChord</c>: a chord whose id is
    ///         the sentinel or dangles is not an arpeggio. It is memoized per
    ///         pass because the scan is O(templates) and validation re-runs after
    ///         every history step — only chords that actually hit the fallback
    ///         enter the memo, so a normal chart never allocates it.
    ///     </para>
    /// </summary>
    public ChordTemplate? ChordTemplateFor(Chord chord)
    {
        ArgumentNullException.ThrowIfNull(chord);

        if (chord.TemplateId >= 0)
        {
            var declared = ChordTemplateById(chord.TemplateId);
            if (declared != null) return declared;
        }

        // Nothing to derive a shape from: an empty chord plays nothing.
        if (chord.ChordNotes.Count == 0) return null;

        _shapeFallback ??= [];
        if (_shapeFallback.TryGetValue(chord, out var cached)) return cached;

        var resolved = ChordTemplateResolver.FindByShape(
            Arrangement, ChordTemplateResolver.FretsOf(chord), ArpeggioFlavour.Plain);
        _shapeFallback[chord] = resolved;
        return resolved;
    }

    // ---- anchor lookups --------------------------------------------------
    //
    // These three are the hot ones. Each was a full `foreach` over
    // `level.Anchors` — an ObservableCollection, so the enumerator is boxed and
    // dispatched through an interface on every call — and they are asked once
    // per fretted note, once per fretted CHORD NOTE, once per chord and several
    // times per hand shape, for EVERY level, on the same 150 ms debounce as the
    // rest of the inspector. Measured on TestData/EOF's 21-level Odorouze lead
    // that is ~4.1 M iterations for CapoAnchorRangeRule alone; on a flat
    // 5,000-note chart at the corpus anchor:note ratio it is ~10 M iterations
    // and ~15,000 boxed enumerators per pass.
    //
    // The index is built per level on first use and binary-searched. Ties are
    // resolved exactly as the linear scans did — see LevelAnchorIndex.

    /// <summary>The anchor in force at <paramref name="time" /> (the latest one
    /// at or before it), or null.</summary>
    public FretAnchor? ActiveAnchor(ChartLevel level, FractionalPosition time) =>
        AnchorIndexFor(level).Active(time);

    /// <summary>The latest anchor strictly BEFORE <paramref name="time" />, or
    /// null.</summary>
    public FretAnchor? PreviousAnchor(ChartLevel level, FractionalPosition time) =>
        AnchorIndexFor(level).Previous(time);

    /// <summary>The anchor starting exactly ON <paramref name="time" />, or
    /// null.</summary>
    public FretAnchor? AnchorAt(ChartLevel level, FractionalPosition time) =>
        AnchorIndexFor(level).At(time);

    private LevelAnchorIndex AnchorIndexFor(ChartLevel level)
    {
        if (_anchorIndexes.TryGetValue(level, out var index)) return index;
        index = new LevelAnchorIndex(level);
        _anchorIndexes[level] = index;
        return index;
    }

    /// <summary>
    ///     The REFERENCE linear scans. They are what
    ///     <see cref="LevelAnchorIndex" /> is tested against
    ///     (<c>AnchorIndexEquivalenceTests</c>), and the local-only charting
    ///     probes still call them. **Rules must use the instance methods above** —
    ///     a rule that calls these reintroduces the O(sounds × anchors) pass, and
    ///     <c>NoValidationRule_ScansTheAnchorListLinearly</c> fails if one does.
    /// </summary>
    public static FretAnchor? FindActiveAnchor(ChartLevel level, FractionalPosition time)
    {
        FretAnchor? best = null;
        foreach (var a in level.Anchors)
        {
            if (a.Position <= time && (best == null || a.Position > best.Position))
                best = a;
        }

        return best;
    }

    /// <inheritdoc cref="FindActiveAnchor" />
    public static FretAnchor? FindPreviousAnchor(ChartLevel level, FractionalPosition time)
    {
        FretAnchor? best = null;
        foreach (var a in level.Anchors)
        {
            if (a.Position < time && (best == null || a.Position > best.Position))
                best = a;
        }

        return best;
    }

    /// <inheritdoc cref="FindActiveAnchor" />
    public static FretAnchor? FindAnchorAt(ChartLevel level, FractionalPosition time)
    {
        foreach (var a in level.Anchors)
            if (a.Position == time) return a;
        return null;
    }

    /// <summary>
    ///     One level's anchors, sorted once and binary-searched.
    ///     <para>
    ///         <b>Anchors are NOT sorted mid-session.</b>
    ///         <c>HighwayEditingService</c> enforces the level with
    ///         <c>runCompatibilityCleanup: false</c>, which is precisely the
    ///         branch that skips <c>ChartSpacingEnforcer</c>'s
    ///         <c>SortByPosition</c> — so between a committed edit and the next
    ///         save the anchor list can be in any order at all. The sort here is
    ///         therefore load-bearing, not a formality.
    ///     </para>
    ///     <para>
    ///         <b>The sort has to be STABLE</b> (<c>OrderBy</c>, not
    ///         <c>Array.Sort</c>). Anchors at the same position are ordinary
    ///         chart state, and the scans this replaces resolved a tie by taking
    ///         the FIRST one in the collection's own order: the loop only replaces
    ///         its candidate on a STRICTLY greater position. So the index has to
    ///         land on the first element of the tied run, not any element of it —
    ///         which is why <see cref="Active" /> and <see cref="Previous" />
    ///         binary-search twice (once for the run, once for its start) and why
    ///         <see cref="At" /> is keyed from the ORIGINAL order.
    ///     </para>
    /// </summary>
    private sealed class LevelAnchorIndex
    {
        private readonly FretAnchor[] _sorted;
        private readonly Dictionary<FractionalPosition, FretAnchor> _byPosition;

        public LevelAnchorIndex(ChartLevel level)
        {
            _sorted = level.Anchors.OrderBy(a => a.Position).ToArray();
            _byPosition = new Dictionary<FractionalPosition, FretAnchor>(_sorted.Length);
            foreach (var anchor in level.Anchors)
                _byPosition.TryAdd(anchor.Position, anchor);
        }

        public FretAnchor? Active(FractionalPosition time) => FirstOfRunBefore(UpperBound(time));

        public FretAnchor? Previous(FractionalPosition time) => FirstOfRunBefore(LowerBound(time));

        public FretAnchor? At(FractionalPosition time) =>
            _byPosition.TryGetValue(time, out var anchor) ? anchor : null;

        /// <summary>The first anchor of the tied run ending at
        /// <paramref name="end" /> (exclusive), or null when nothing precedes it.</summary>
        private FretAnchor? FirstOfRunBefore(int end)
        {
            if (end <= 0) return null;
            return _sorted[LowerBound(_sorted[end - 1].Position)];
        }

        /// <summary>Index of the first anchor at or after <paramref name="time" />.</summary>
        private int LowerBound(FractionalPosition time)
        {
            int lo = 0, hi = _sorted.Length;
            while (lo < hi)
            {
                var mid = (lo + hi) >> 1;
                if (_sorted[mid].Position < time) lo = mid + 1;
                else hi = mid;
            }

            return lo;
        }

        /// <summary>Index of the first anchor strictly after <paramref name="time" />.</summary>
        private int UpperBound(FractionalPosition time)
        {
            int lo = 0, hi = _sorted.Length;
            while (lo < hi)
            {
                var mid = (lo + hi) >> 1;
                if (_sorted[mid].Position <= time) lo = mid + 1;
                else hi = mid;
            }

            return lo;
        }
    }

    public static Chord? FindChordLinkedTo(ChartLevel level, GuitarNote note) =>
        FindChordLinkedTo(level, note.EndPosition, note.StringIndex);

    /// <summary>
    ///     The chord a Link-Next sound would link INTO: one starting exactly
    ///     where the sound's sustain ends and claiming the same string.
    ///     Position-based rather than note-based because a CHORD NOTE has no
    ///     position of its own and needs the same check
    ///     (<see cref="Rules.LinkNextRule" />).
    ///     <para>
    ///         This is the one-off form — a full scan of the level's chords.
    ///         A pass that asks per link goes through the instance
    ///         <see cref="ChordLinkedTo" />, which answers from a per-level
    ///         index built once; the two are defined over the same predicate
    ///         (<see cref="ClaimsString" />) so they cannot disagree.
    ///     </para>
    /// </summary>
    public static Chord? FindChordLinkedTo(ChartLevel level, FractionalPosition endPosition, int stringIndex)
    {
        foreach (var c in level.Chords)
        {
            if (c.Position != endPosition) continue;
            if (ClaimsString(c, stringIndex)) return c;
        }
        return null;
    }

    private static bool ClaimsString(Chord chord, int stringIndex)
    {
        if (chord.ChordNotes.Count == 0) return false;
        foreach (var cn in chord.ChordNotes)
            if (cn.StringIndex == stringIndex) return true;
        return false;
    }

    // ---- per-level link indexes ---------------------------------------------
    //
    // Perf: LinkNextRule asked two questions
    // per linked sound per level per validation pass (the inspector's 150 ms
    // debounce after every history step, over EVERY level of a DD chart):
    // "which chord starts where my sustain ends" scanned every chord, and
    // "what is the next single note on my string after this chord" scanned the
    // note list from index 0. On a 5,000-note / 21-level chart that was tens of
    // millions of iterations per pass. Both are answered here from indexes
    // built lazily, once per level per context — the same shape as the anchor
    // index above and the template map.

    private readonly Dictionary<ChartLevel, Dictionary<FractionalPosition, List<Chord>>> _chordsByPosition = [];
    private readonly Dictionary<ChartLevel, LevelNoteIndex> _noteIndexes = [];

    /// <summary>Indexed <see cref="FindChordLinkedTo(ChartLevel, FractionalPosition, int)" />:
    /// the FIRST chord in <c>level.Chords</c> order at that position claiming
    /// that string, exactly as the scan answers.</summary>
    public Chord? ChordLinkedTo(ChartLevel level, FractionalPosition endPosition, int stringIndex)
    {
        if (!_chordsByPosition.TryGetValue(level, out var byPosition))
        {
            byPosition = [];
            foreach (var chord in level.Chords)
            {
                if (!byPosition.TryGetValue(chord.Position, out var at))
                    byPosition[chord.Position] = at = new List<Chord>(1);
                at.Add(chord);
            }

            _chordsByPosition[level] = byPosition;
        }

        if (!byPosition.TryGetValue(endPosition, out var candidates)) return null;
        foreach (var chord in candidates)
        {
            if (ClaimsString(chord, stringIndex))
                return chord;
        }

        return null;
    }

    private readonly Dictionary<ChartLevel, LevelChordStringIndex> _chordStringIndexes = [];

    /// <summary>
    ///     The LAST chord strictly before <paramref name="before" /> that claims
    ///     <paramref name="stringIndex" />, as (position, fret of its first chord
    ///     note on that string) — what <c>SlideTechniqueRule</c>'s pull-off check
    ///     wants, answered without a full scan of <c>level.Chords</c>
    ///     PER PULL-OFF NOTE with no early exit. Same shape as the note index
    ///     below; this is the case a per-note index
    ///     would miss, and it is the O(pullOffs × chords) one.
    ///     <para>
    ///         Tie-break preserved exactly: among chords sharing that greatest
    ///         position, the FIRST in <c>level.Chords</c> order wins, because the
    ///         scan skipped any chord at a position it had already recorded.
    ///     </para>
    /// </summary>
    public (FractionalPosition Position, int Fret)? PreviousChordOnStringBefore(
        ChartLevel level, FractionalPosition before, int stringIndex)
    {
        if (!_chordStringIndexes.TryGetValue(level, out var index))
            _chordStringIndexes[level] = index = new LevelChordStringIndex(level.Chords);
        return index.LastBefore(before, stringIndex);
    }

    /// <summary>
    ///     A level's chords split per claimed string, ordered by position with
    ///     list order preserved inside a position run (a stable sort), so the
    ///     "last strictly before" query is a binary search plus a walk back to
    ///     the head of that run.
    /// </summary>
    private sealed class LevelChordStringIndex
    {
        private readonly Dictionary<int, List<(FractionalPosition Position, int Fret)>> _byString = [];

        public LevelChordStringIndex(IList<Chord> chords)
        {
            var order = 0;
            var scratch = new Dictionary<int, List<(FractionalPosition Position, int Fret, int Order)>>();
            foreach (var chord in chords)
            {
                foreach (var cn in chord.ChordNotes)
                {
                    // The FIRST chord note on a string decides that chord's fret
                    // for it — the scan broke out of its inner loop there.
                    if (!scratch.TryGetValue(cn.StringIndex, out var list))
                        scratch[cn.StringIndex] = list = [];
                    if (list.Count > 0 && list[^1].Order == order) continue;
                    list.Add((chord.Position, cn.Fret, order));
                }

                order++;
            }

            foreach (var (stringIndex, list) in scratch)
            {
                _byString[stringIndex] = list
                    .OrderBy(e => e.Position)
                    .ThenBy(e => e.Order)
                    .Select(e => (e.Position, e.Fret))
                    .ToList();
            }
        }

        public (FractionalPosition Position, int Fret)? LastBefore(FractionalPosition before, int stringIndex)
        {
            if (!_byString.TryGetValue(stringIndex, out var list)) return null;

            // Lower bound: first index whose position is >= before; everything
            // to its left is strictly before.
            int lo = 0, hi = list.Count;
            while (lo < hi)
            {
                var mid = lo + (hi - lo) / 2;
                if (list[mid].Position < before) lo = mid + 1;
                else hi = mid;
            }

            if (lo == 0) return null;

            // Walk back to the head of the position run — the first in list
            // order at that position, which is what the scan settled on.
            var last = lo - 1;
            var position = list[last].Position;
            while (last > 0 && list[last - 1].Position == position) last--;
            return list[last];
        }
    }

    /// <summary>The first note in <c>level.Notes</c> order on
    /// <paramref name="stringIndex" /> whose position is strictly after
    /// <paramref name="after" /> — what the chord-note half of the Link-Next
    /// check wants. Answered from a per-string list built once per level.</summary>
    public GuitarNote? NextNoteOnStringAfter(ChartLevel level, FractionalPosition after, int stringIndex)
    {
        if (!_noteIndexes.TryGetValue(level, out var index))
            _noteIndexes[level] = index = new LevelNoteIndex(level.Notes);
        return index.NextOnStringAfter(after, stringIndex);
    }

    /// <summary>
    ///     A level's notes split per string, in list order. When the level is
    ///     position-sorted (every level an editing action has touched is) the
    ///     "first after" query is a binary search; when it is not — an import
    ///     nobody has re-sorted — it is a linear walk of that ONE string's list,
    ///     which still answers exactly what the whole-list scan did.
    /// </summary>
    private sealed class LevelNoteIndex
    {
        private readonly Dictionary<int, List<GuitarNote>> _byString = [];
        private readonly bool _sorted;

        public LevelNoteIndex(IList<GuitarNote> notes)
        {
            _sorted = true;
            for (var i = 0; i < notes.Count; i++)
            {
                var note = notes[i];
                if (i > 0 && note.Position < notes[i - 1].Position) _sorted = false;
                if (!_byString.TryGetValue(note.StringIndex, out var list))
                    _byString[note.StringIndex] = list = [];
                list.Add(note);
            }
        }

        public GuitarNote? NextOnStringAfter(FractionalPosition after, int stringIndex)
        {
            if (!_byString.TryGetValue(stringIndex, out var list)) return null;
            if (!_sorted)
            {
                foreach (var note in list)
                {
                    if (note.Position > after)
                        return note;
                }

                return null;
            }

            // Upper bound: first index whose position is > after.
            int lo = 0, hi = list.Count;
            while (lo < hi)
            {
                var mid = lo + (hi - lo) / 2;
                if (list[mid].Position <= after) lo = mid + 1;
                else hi = mid;
            }

            return lo < list.Count ? list[lo] : null;
        }
    }

    // ---- builders --------------------------------------------------------

    private static bool IsNear(IReadOnlySet<long> set, double timeMs)
    {
        // Allow 1 ms slop so floating-point round-trips don't generate false misses.
        var rounded = (long)Math.Round(timeMs);
        return set.Contains(rounded) || set.Contains(rounded - 1) || set.Contains(rounded + 1);
    }

    /// <summary>
    ///     Every stretch the arrangement marks as no-guitar: a <c>noguitar</c>
    ///     SECTION up to the next section — what Rocksmith shows as "No Guitar" and
    ///     what DLC Builder's I16 check reads (InstrumentalChecker.getNoguitarSections)
    ///     — and a noguitar-named PHRASE up to the next phrase. A section was the
    ///     one that mattered and the one missed: an EOF chart puts its noguitar on
    ///     the section and gives the phrase any name.
    /// </summary>
    private static List<(double startMs, double endMs)> BuildNoguitarIntervals(
        Arrangement arr, IList<BeatMarker> beats)
    {
        var result = new List<(double, double)>();
        AddNoguitarIntervals(arr, beats, e => e.Phrase, IsNoguitarPhrase, result);
        AddNoguitarIntervals(arr, beats, e => e.Section,
            s => s.StartsWith("noguitar", StringComparison.OrdinalIgnoreCase), result);
        return result;
    }

    private static void AddNoguitarIntervals(Arrangement arr, IList<BeatMarker> beats,
        Func<EventPoint, string> marker, Func<string, bool> isNoguitar, List<(double, double)> result)
    {
        var orderedEvents = arr.EventPoints
            .Where(e => !string.IsNullOrEmpty(marker(e)))
            .OrderBy(e => e.Position)
            .ToList();

        for (var i = 0; i < orderedEvents.Count; i++)
        {
            var ep = orderedEvents[i];
            if (!isNoguitar(marker(ep))) continue;
            var startMs = ep.Position.GetPositionMs(beats);
            var endMs = i + 1 < orderedEvents.Count
                ? orderedEvents[i + 1].Position.GetPositionMs(beats)
                : double.PositiveInfinity;
            result.Add((startMs, endMs));
        }
    }

    private static HashSet<long> BuildToneChangeTimes(Arrangement arr, IList<BeatMarker> beats)
    {
        var set = new HashSet<long>();
        foreach (var tc in arr.ToneChanges)
            set.Add((long)Math.Round(tc.Position.GetPositionMs(beats)));
        return set;
    }

    private static bool IsNoguitarPhrase(string phrase) =>
        phrase.StartsWith("noguitar", StringComparison.OrdinalIgnoreCase)
        || phrase.Equals("ng", StringComparison.OrdinalIgnoreCase);

    private static HashSet<long> BuildPhraseTimes(
        Arrangement arr, IList<BeatMarker> beats, bool includeMover, bool moverOnly = false)
    {
        var set = new HashSet<long>();
        foreach (var ep in arr.EventPoints)
        {
            if (string.IsNullOrEmpty(ep.Phrase)) continue;
            var isMover = ep.Phrase.StartsWith("mover", StringComparison.OrdinalIgnoreCase);
            if (moverOnly && !isMover) continue;
            if (!includeMover && isMover) continue;
            set.Add((long)Math.Round(ep.Position.GetPositionMs(beats)));
        }
        return set;
    }
}
