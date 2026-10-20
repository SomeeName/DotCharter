namespace DotCharter.Services.Editing.FretAnchors;

// Priority-based FHP generator. Every anchor position is resolved through a
// single forward pass with a strict rule hierarchy — no post-passes that can
// overwrite each other.
//
// Resolution priority at each position (highest wins):
//
//   P1. Inside a HandShape              → locked to the shape's precomputed
//                                         root. Emitted exactly once, at the
//                                         shape's Start; every later
//                                         position in the shape inherits.
//   P2. Slide destination (pitched or   → anchor shifts by
//       unpitched)                        (slideTarget - sourceFret) from
//                                         the source anchor.
//   ——— and cutting across P2/P4/P5: an anchor's root is NOT fixed when it is
//       emitted. While its GROUP is still open, a later note that reaches below
//       it pulls that already-written anchor DOWN rather than planting a second
//       one (TryDescendGroup) — the rule the hand-corrected corpus follows.
//   P3. Tap / chained-pull-off run      → width widened to span the whole
//       crossing this position            physical run (rule 3).
//   P4. Finger-1 defined on any struck  → root is exactly that finger-1
//       note                              fret (no pinky-math min).
//   P5. Otherwise                       → barre fret, else lowest struck
//                                         fret. Width = max(4, span).
//   P6. Abandonment (struck notes)      → the hand re-plants UP onto a phrase
//                                         that has left the low fret for good.
//
// With a freeGrip resolver a FREE chord is read through its BASE grip
// (the suggestion for its frets, whatever variant an earlier pass left it on), a
// shape opening on a free double stop may keep the anchor already in force
// (CanHoldFreeShape), and ChordFingeringFollower then re-fingers it. After the walk, AnchorSmoothing
// drops A -> B -> A bounces and (bass) pulls forced up-shifts back to where the
// climb started. Neither post-pass moves a slide landing.
//
// BASS octave figures (BassOctaveFigures): the note that starts one plants
// its anchor on its own fret even when the hand in force reaches it, and that
// anchor is pinned like a slide landing.
//
// Handshape roots are computed once up front, from the first chord that
// falls inside the shape (fingering-aware via P4/P5). This prevents
// "handshape starvation" where a shape that begins on an empty beat never
// seeds its anchor.
//
// CAPO: frets are ABSOLUTE on the un-capoed neck (fret 0 = open, which a capo
// stops at the capo fret). So with a capo the whole generator's floor moves up:
// a sound at or below the capo is OPEN and never anchors, and the lowest legal
// anchor is capo + 1 (an anchor on or below the capo is inspector rule L02).
// That is what MinAnchorFret is — with capo 0 it is 1, exactly the constant it
// replaced, so an uncapoed chart generates byte-identical anchors.
public sealed partial class FretAnchorGenerationService : IFretAnchorGenerationService, IStruckSounds
{
    private const int MaxFret = 22;
    private const int BaseAnchorWidth = 4;

    private const int NotFree = SoundAtPosition.NotFree;

    // The free double-stop hold (CanHoldFreeShape): how far below the grip's
    // index fret the hand may stay, and the lowest string such a grip may sit
    // on. Measured on the owner's charts: a double stop G9+B9 under FHP 8
    // is played with the higher fingers 72-95% of the time when the hand is
    // already there, a power-chord-like grip on the two lowest strings only
    // 18-44% — holding those cost the holdout split 1.2 pp. Three-string and
    // bigger grips are re-planted on (75% hold on train, a loss on holdout).
    private const int HoldMaxShift = 2;
    private const int HoldLowestString = 2;

    // A double stop right after a chord fretted with the index ON the anchor,
    // followed by HoldLookAhead positions that all stay above it, is a new hand
    // position (the charter moves there 66% of the time): no hold.
    private const int HoldLookAhead = 4;

    // How far back a group's anchor may be pulled DOWN (see TryDescendGroup),
    // counted in STRUCK POSITIONS between the group's opening note and the note
    // that reaches below it. EOF's own group is unbounded; the charter's
    // corrections are not, and the corpus says so. Swept over the 12,279
    // comparable points of the 89 hand-corrected arrangements in TestData/EOF
    // (exact agreement / mean fret delta / anchors emitted vs the human's
    // 12,286), everything else held fixed:
    //
    //     0 (no descend, the pre-0.38 walk)  75.66%  +0.16  12,629
    //     1                                  78.18%  +0.05  11,567
    //     2                                  78.64%  +0.01  11,273
    //     3                                  78.84%  -0.01  11,107   ← plateau
    //     4                                  78.79%  -0.02  11,006
    //     5                                  78.65%  -0.03  10,955
    //     8                                  78.08%  -0.05  10,816
    //     unbounded                          77.60%  -0.06  10,714
    //
    // 3 sits on the flat 2..5 top rather than on a spike, and it is where the
    // over/under bias crosses zero. Bounding the MERGED reach is the other half
    // and is BaseAnchorWidth: EOF's eof_fret_range_tolerances defaults to a flat
    // 4 across the neck, and 3 vs 4 differed by 5 points in 12,279 — noise, so
    // the physically meaningful 4-fret box wins.
    private const int DescendReach = 3;

    // Bass: a descend may not leave the group's highest played fret more
    // than two frets above the new anchor. The charters' bass hand spans three
    // frets (an anchor + 3 governs 5% of their sounds, 14% of the walk's), and a
    // descend that put the opening note at +3 matched them 0-21% of the time.
    // +0.7 / +0.3 pp bass FHP (train / holdout) on top of HandAbandonment's rules.
    private const int BassDescendWidth = 3;

    private static readonly List<SoundAtPosition> EmptySoundList = [];
    private readonly IAppLogger? _logger;

    // Per-call state.
    private RootSource _lastRootSource;
    private int _capo;
    private bool _bassArrangement;
    private Dictionary<FractionalPosition, List<SoundAtPosition>> _byPosition = [];
    private List<(FractionalPosition Start, FractionalPosition End)> _handShapes = [];

    /// <summary>The ascending walk's cursor over <see cref="_handShapes" />;
    /// rebuilt with the list in <c>BuildIndexes</c>.</summary>
    private AscendingSpanCursor _handShapeCursor = new([]);
    private Dictionary<FractionalPosition, int> _handShapeStartRoot = [];

    /// <summary>For a shape that opens on a FREE double stop: the lowest anchor
    /// its grip can still be played from, and the position of that strum.</summary>
    private Dictionary<FractionalPosition, (int Low, FractionalPosition FirstStruck)> _handShapeSoftLow = [];
    private Dictionary<FractionalPosition, AnchorRuleTrace>? _handShapeRootTrace;
    private HashSet<FractionalPosition> _wideShapeStarts = [];
    private Dictionary<FractionalPosition, (int Min, int Max)> _runSpanByPosition = [];
    private List<SoundAtPosition> _sortedByStart = [];
    private List<(FractionalPosition Pos, int MinFret)> _struckOrder = [];
    private Dictionary<FractionalPosition, int> _struckOrdinal = [];

    /// <summary>Bass only: where an octave figure starts (<see cref="BassOctaveFigures" />).</summary>
    private HashSet<FractionalPosition> _octaveStarts = [];

    /// <summary>Bass only: where a figure's OCTAVE note sounds — the bass pull-back never moves an anchor onto
    /// one, which would split the root and its octave across two hand positions.</summary>
    private HashSet<FractionalPosition> _octaveNotes = [];

    public FretAnchorGenerationService()
    {
    }

    public FretAnchorGenerationService(IAppLogger logger) => _logger = logger;

    // Lowest fret an anchor may sit on. An anchor on or below the capo is
    // inspector rule L02, so the floor is capo + 1. Clamped into [1, MaxFret]
    // so an absurd capo can never invert a Math.Clamp(value, min, max) range
    // (which throws when min > max). Capo 0 ⇒ 1 ⇒ the pre-capo behaviour.
    private int MinAnchorFret => Math.Clamp(_capo + 1, 1, MaxFret);

    // ATTRIBUTION SIDE-CHANNEL (measurement only, null in production). When a
    // dictionary is assigned, every EMITTED anchor records which priority rule
    // chose its root. Null is the flag: nothing on the walk allocates or writes
    // unless a harness opted in. Used by LocalOnly_AnchorAttributionProbe to
    // answer "which rule is the disagreement coming from" — an aggregate
    // exact-% cannot.
    internal Dictionary<FractionalPosition, AnchorRuleTrace>? RuleTrace { get; set; }

    /// <summary>
    ///     Generates fret-hand-position anchors for a level.
    /// </summary>
    /// <param name="level">Level whose notes/chords/hand shapes drive the walk.</param>
    /// <param name="rangeStart">Inclusive first position to emit for; null = from the start.</param>
    /// <param name="rangeEnd">Inclusive last position to emit for; null = to the end.</param>
    /// <param name="chordTemplates">
    ///     Templates backing the level's chords (resolved by <c>Id</c>, never by
    ///     position), used for the fingering-aware root rules; null = no
    ///     fingering information.
    /// </param>
    /// <param name="capo">
    ///     Capo fret of the owning arrangement. No generated anchor sits at or
    ///     below it. <c>0</c> (the default) means no capo and is a strict no-op.
    /// </param>
    /// <param name="bassArrangement">
    ///     True when the level belongs to a bass arrangement: the "hand
    ///     abandonment" re-anchor looks <see cref="HandAbandonment.BassHold" /> positions
    ///     ahead instead of <see cref="HandAbandonment.GuitarHold" /> (and holds
    ///     within reach, <see cref="HandAbandonment" />), a group descends only
    ///     within <see cref="BassDescendWidth" /> frets, up-shifts are pulled
    ///     back (<see cref="AnchorSmoothing.PullBackUpshifts" />) and no double
    ///     stop is held. Each is measured per instrument.
    /// </param>
    /// <param name="freeGrip">
    ///     Per chord template: the BASE grip of a FREE fingering — a suggestion
    ///     nobody authored, possibly shifted up the hand by an earlier pass — or
    ///     null for an authored one (<see cref="ChordFingeringFollower.FreeGripResolver" />).
    ///     A free chord is read through its base grip, so a variant never pins its
    ///     own anchor, and the walk may keep the hand where it is and let a double
    ///     stop be played with higher fingers; pair it with
    ///     <see cref="ChordFingeringFollower" />, which re-fingers those chords.
    ///     Null (the default) treats every fingering as authored.
    /// </param>
    /// <param name="tuning">
    ///     The owning arrangement, read for its TUNING only: a bass octave figure
    ///     (<see cref="BassOctaveFigures" />) must SOUND an octave apart. Null
    ///     (the default) recognises it by fret shape alone, as in standard tuning.
    /// </param>
    /// <returns>The generated anchors, ordered by position.</returns>
    public IReadOnlyList<FretAnchor> GenerateAnchors(
        ChartLevel level,
        FractionalPosition? rangeStart = null,
        FractionalPosition? rangeEnd = null,
        IReadOnlyList<ChordTemplate>? chordTemplates = null,
        int capo = 0,
        bool bassArrangement = false,
        Func<ChordTemplate, ChordTemplate?>? freeGrip = null,
        Arrangement? tuning = null)
    {
        using var _ = _logger?.BeginTimedOperation(AppLogCategory.Editing, "Fret Anchor Generation",
            $"notes={level.Notes.Count}, chords={level.Chords.Count}, handShapes={level.HandShapes.Count}, capo={capo}");

        _capo = Math.Max(0, capo);
        _bassArrangement = bassArrangement;

        BuildIndexes(level, chordTemplates, freeGrip);
        (_octaveStarts, _octaveNotes) = _bassArrangement ? BassOctaveFigures.Find(level.Notes, _capo, tuning) : ([], []);
        PrecomputeHandShapeRoots();
        PrecomputeRunSpans();
        PrecomputeStruckOrder();

        var allPositions = CollectAnchorPositions(level, rangeStart, rangeEnd);
        if (allPositions.Count == 0)
        {
            _logger?.Trace(AppLogCategory.Editing, "No anchor candidate positions; returning empty.");
            return Array.Empty<FretAnchor>();
        }

        var walk = Walk(allPositions);

        Smooth(walk.Hints, walk.Pinned);
        var result = BuildAnchors(allPositions, walk.Hints);
        AnchorLeadingOpenSounds(result, level, rangeStart, rangeEnd);
        _logger?.Info(AppLogCategory.Editing,
            $"Generated {result.Count} fret anchors from {allPositions.Count} candidate positions.");
        return result;
    }

    // ─── Anchor resolution helpers ──────────────────────────────────────────

    // Finger-1 takes absolute precedence — its fret IS the root.
    // If f1 is not defined but other fingers are, compute the IMPLIED f1
    // position from the lowest-numbered defined finger (e.g. f3 on fret 13
    // implies f1 at 13 - (3-1) = 11). Finally fall back to barre / lowest.
    private int? ComputeRequiredRoot(IReadOnlyList<SoundAtPosition> struck)
    {
        _lastRootSource = RootSource.Unknown;
        if (struck.Count == 0) return null;

        for (var i = 0; i < struck.Count; i++)
        {
            if (struck[i].Finger == 1)
            {
                _lastRootSource = RootSource.Finger1;
                return Math.Max(MinAnchorFret, struck[i].Fret);
            }
        }

        // Implied f1 from the lowest-numbered other defined finger.
        SoundAtPosition? lowestFingered = null;
        for (var i = 0; i < struck.Count; i++)
        {
            var s = struck[i];
            if (s.Finger <= 0) continue;
            if (lowestFingered == null || s.Finger < lowestFingered.Value.Finger)
                lowestFingered = s;
        }

        if (lowestFingered.HasValue)
        {
            // A finger-implied root behind the capo is not reachable — fall
            // through to the barre / lowest-fret rules rather than anchoring
            // the hand somewhere the capo occupies.
            var implied = lowestFingered.Value.Fret - (lowestFingered.Value.Finger - 1);
            if (implied >= MinAnchorFret)
            {
                _lastRootSource = RootSource.ImpliedFinger;
                return implied;
            }
        }

        var barre = DetectBarreFret(struck);
        if (barre.HasValue)
        {
            _lastRootSource = RootSource.Barre;
            return barre.Value;
        }

        _lastRootSource = RootSource.LowestFret;
        return Math.Max(MinAnchorFret, struck.Min(s => s.Fret));
    }

    private AnchorHint ComputeAnchorForStruck(
        IReadOnlyList<SoundAtPosition> struck,
        FractionalPosition position)
    {
        var required = ComputeRequiredRoot(struck);
        if (required is null) _lastRootSource = RootSource.Floor;
        var root = required ?? MinAnchorFret;
        var maxF = struck.Max(s => s.Fret);

        // If this position belongs to a tap/pull-off run, the run's min
        // is the true anchor root (the hand is parked there across the
        // whole run, even on the seed note that reaches higher).
        var runWidth = BaseAnchorWidth;
        if (_runSpanByPosition.TryGetValue(position, out var run))
        {
            // Only let the run pull the root DOWN when no explicit
            // finger-1/barre is defined — those override physical reach.
            var hardRoot = ComputeHardRoot(struck);
            if (!hardRoot.HasValue && run.Min < root)
            {
                root = run.Min;
                _lastRootSource = RootSource.RunMin;
            }

            runWidth = Math.Max(runWidth, run.Max - root + 1);
            maxF = Math.Max(maxF, run.Max);
        }

        var width = Math.Max(runWidth, maxF - root + 1);
        width = Math.Max(BaseAnchorWidth, width);
        return new AnchorHint(Math.Clamp(root, MinAnchorFret, MaxFret), width);
    }

    // Highest fret the group has actually PLAYED so far. Not the anchor's
    // declared ceiling (root + width - 1): the declared width has a floor of 4
    // that no note need reach, and using it would refuse merges over frets
    // nobody plays.
    private static int GroupHighOf(AnchorHint hint, IReadOnlyList<SoundAtPosition> struck)
    {
        var high = hint.AnchorFret;
        for (var i = 0; i < struck.Count; i++)
        {
            if (struck[i].Fret > high)
                high = struck[i].Fret;
        }

        return high;
    }

    // Move the OPEN group's anchor DOWN to take this note in, instead of
    // planting a second anchor on top of it.
    //
    // This is the one structural difference between our forward walk and the
    // generator that produced the hand-corrected corpus. EOF
    // (eof_generate_efficient_hand_positions_logic, EOF_SRC/rs.c:3663) does not
    // fix an anchor's fret when it opens a group: it keeps a running
    // [current_low, current_high] over the notes the group has swallowed, lets
    // current_low DESCEND whenever a later note plays lower and the combined
    // reach still fits the hand (eof_note_can_be_played_within_fret_tolerance),
    // and only then writes the anchor — at `next_position`, the group's FIRST
    // note, carrying the DESCENDED low. We instead took the first note's fret as
    // the root and split the group the moment anything played below it, which
    // leaves the group's opening anchor sitting above the hand's real base.
    //
    // Measured over the 89 hand-corrected arrangements in TestData/EOF, that
    // split was the single largest error source: the struck walk agreed with the
    // charter on only 66.0% of its points with 13% of them exactly +2 frets HIGH,
    // and it is why the aggregate error piled up at +1/+2 and why we sat higher
    // (16.1%) twice as often as lower (8.2%).
    //
    // Refused when an explicit finger-1/barre root demands this exact fret (that
    // is a hand position, not a reach — EOF forces a change for a barre chord
    // whose lowest fret differs from the running low) and when the merged reach
    // exceeds a playable span at the new base.
    private bool TryDescendGroup(
        IDictionary<FractionalPosition, AnchorHint> hints,
        FractionalPosition? groupPos,
        FractionalPosition position,
        ref AnchorHint? current,
        ref int groupHigh,
        AnchorHint need,
        IReadOnlyList<SoundAtPosition> struck)
    {
        if (groupPos is null || current is null) return false;
        var cur = current.Value;

        // Bounded reach: a charter reads a low note SOON after the hand plants
        // as part of that same position, and one much later as the hand having
        // moved. An unbounded descend drags a whole phrase's opening anchor down
        // onto a note bars away.
        if (_struckOrdinal.TryGetValue(position, out var here) &&
            _struckOrdinal.TryGetValue(groupPos.Value, out var opened) &&
            here - opened > DescendReach)
        {
            return false;
        }

        // Only ever DOWN — climbing is the existing walk's job.
        var newLow = need.AnchorFret;
        if (newLow >= cur.AnchorFret) return false;

        // An authored finger-1 / barre position is where the hand IS.
        var hardRoot = ComputeHardRoot(struck);
        if (hardRoot.HasValue && hardRoot.Value != newLow) return false;

        var newHigh = Math.Max(groupHigh, GroupHighOf(need, struck));
        if (newHigh - newLow + 1 > (_bassArrangement ? BassDescendWidth : BaseAnchorWidth)) return false;

        var merged = new AnchorHint(newLow, Math.Max(BaseAnchorWidth, newHigh - newLow + 1));
        hints[groupPos.Value] = merged;
        RuleTrace?[groupPos.Value] = new AnchorRuleTrace(AnchorRule.P4P5StruckWalk, RootSource.GroupDescend);
        current = merged;
        groupHigh = newHigh;
        return true;
    }

    // ─── Free double stops ───────────────────────────────────────────

    // The anchors a FREE double stop can be played from: its index fret down to
    // as many frets lower as the grip has spare fingers (at most HoldMaxShift).
    // A charter plays G9+B9 under FHP 8 with the higher fingers rather than
    // moving the index onto fret 9 — but only when nobody stated the fingering,
    // only for two strings, and not on the two lowest strings (see the
    // constants). Null for anything else.
    private (int Low, int Root)? SoftRootRange(IReadOnlyList<SoundAtPosition> struck)
    {
        if (struck.Count != 2) return null;
        var slack = struck[0].FreeSlack;
        var root = int.MaxValue;
        for (var i = 0; i < struck.Count; i++)
        {
            if (struck[i].FreeSlack != slack || struck[i].StringIndex < HoldLowestString) return null;
            if (struck[i].Finger == 1 && struck[i].Fret < root) root = struck[i].Fret;
        }

        slack = Math.Min(slack, HoldMaxShift);
        if (slack < 1 || root == int.MaxValue || root < MinAnchorFret) return null;
        return (Math.Max(MinAnchorFret, root - slack), root);
    }

    // True when the hand may stay where it is for a shape opening on a free
    // double stop: the anchor in force sits below the grip's index fret but
    // within its spare fingers, every fret the shape plays is inside that
    // window, and the context does not say the hand is moving (HandStaysFor).
    private bool CanHoldFreeShape(
        (FractionalPosition Start, FractionalPosition End) shape,
        AnchorHint? current,
        AnchorHint shapeHint)
    {
        if (current is not { } cur) return false;
        if (!_handShapeSoftLow.TryGetValue(shape.Start, out var soft)) return false;
        if (cur.AnchorFret < soft.Low || cur.AnchorFret >= shapeHint.AnchorFret) return false;
        if (ShapeMaxFret(shape, cur.AnchorFret) > cur.AnchorFret + cur.Width - 1) return false;
        return HandStaysFor(soft.FirstStruck, cur.AnchorFret);
    }

    private bool HandStaysFor(FractionalPosition position, int anchor)
    {
        if (!_struckOrdinal.TryGetValue(position, out var ordinal) || ordinal == 0) return false;
        var previous = _struckOrder[ordinal - 1];
        if (GetFrettedSoundsAt(previous.Pos).Count < 2 || previous.MinFret != anchor) return true;

        var nextLow = int.MaxValue;
        for (var i = ordinal + 1; i < Math.Min(_struckOrder.Count, ordinal + 1 + HoldLookAhead); i++)
            nextLow = Math.Min(nextLow, _struckOrder[i].MinFret);
        return nextLow <= anchor;
    }

    // How many frets a free fingering can slide up the hand: 4 minus its
    // highest finger. NotFree when it names no index finger on a fretted
    // string, uses the thumb or leaves a fretted string unfingered.
    private int FreeFingeringSlack(ChordTemplate template)
    {
        var maxFinger = 0;
        var hasIndex = false;
        foreach (var (stringIndex, fret) in template.Frets)
        {
            if (fret <= _capo) continue;
            var finger = template.GetFinger(stringIndex);
            if (finger is < 1 or > 4) return NotFree;
            if (finger == 1) hasIndex = true;
            if (finger > maxFinger) maxFinger = finger;
        }

        return hasIndex ? 4 - maxFinger : NotFree;
    }

    // ─── Post-passes (AnchorSmoothing) ──────────────────────────────────────

    private void Smooth(Dictionary<FractionalPosition, AnchorHint> hints, HashSet<FractionalPosition> pinned)
    {
        var before = RuleTrace != null ? hints.Keys.ToList() : null;
        AnchorSmoothing.CollapseBounces(hints, pinned, this);
        if (_bassArrangement)
        {
            foreach (var (from, to) in AnchorSmoothing.PullBackUpshifts(hints, pinned, this, _octaveNotes))
            {
                RuleTrace?.Remove(from);
                RuleTrace?[to] = new AnchorRuleTrace(AnchorRule.P7PulledBack, RootSource.LowestFret);
            }
        }

        if (before == null) return;
        foreach (var key in before)
        {
            if (!hints.ContainsKey(key)) RuleTrace!.Remove(key);
        }
    }

    int IStruckSounds.Count => _struckOrder.Count;

    FractionalPosition IStruckSounds.PositionAt(int index) => _struckOrder[index].Pos;

    int IStruckSounds.MinFretAt(int index) => _struckOrder[index].MinFret;

    int IStruckSounds.MaxFretAt(int index)
    {
        var max = int.MinValue;
        foreach (var s in GetFrettedSoundsAt(_struckOrder[index].Pos))
            max = Math.Max(max, s.Fret);
        return max;
    }

    int? IStruckSounds.HardRootAt(int index) => ComputeHardRoot(GetFrettedSoundsAt(_struckOrder[index].Pos));

    bool IStruckSounds.PlayableFrom(int index, int fret, int width)
    {
        var struck = GetFrettedSoundsAt(_struckOrder[index].Pos);
        for (var i = 0; i < struck.Count; i++)
        {
            if (struck[i].Fret < fret || struck[i].Fret > fret + width - 1) return false;
        }

        if (SoftRootRange(struck) is { } soft) return fret >= soft.Low && fret <= soft.Root;
        var hard = ComputeHardRoot(struck);
        return hard is null || hard.Value == fret;
    }

    int IStruckSounds.LowerBound(FractionalPosition position)
    {
        int lo = 0, hi = _struckOrder.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (_struckOrder[mid].Pos < position) lo = mid + 1;
            else hi = mid;
        }

        return lo;
    }

    private int ComputeBaseWidth(int root, FractionalPosition position)
    {
        var width = BaseAnchorWidth;
        if (_runSpanByPosition.TryGetValue(position, out var run))
            width = Math.Max(width, run.Max - Math.Min(root, run.Min) + 1);
        return width;
    }

    private int ComputeHandShapeWidth(
        (FractionalPosition Start, FractionalPosition End) shape,
        int root)
    {
        // Widest reach of any fretted sound played inside the shape, capped at a
        // playable hand span (a wide shape is made non-locking up front, so a
        // locking shape always fits — the clamp is defensive).
        var maxFret = ShapeMaxFret(shape, root);
        return Math.Clamp(maxFret - root + 1, BaseAnchorWidth, MaxHandSpanWidth(root));
    }

    private int ShapeMaxFret((FractionalPosition Start, FractionalPosition End) shape, int root)
    {
        var maxFret = root;
        var idx = LowerBound(_sortedByStart, shape.Start);
        for (var i = idx; i < _sortedByStart.Count; i++)
        {
            var s = _sortedByStart[i];
            if (s.Position > shape.End) break;
            if (s.Position < shape.Start) continue;
            if (s.Fret > maxFret) maxFret = s.Fret;
        }

        return maxFret;
    }

    // Max playable hand span (window width) rooted at baseFret. Mirrors Log
    // Charter's canBeExtended: a wider window is only realistic higher up the
    // neck, where the frets are physically closer. A sequential run reaching
    // beyond this must MOVE the hand (a new anchor), not stretch one.
    private static int MaxHandSpanWidth(int baseFret) =>
        baseFret >= 18 ? 7 :
        baseFret >= 13 ? 6 :
        baseFret >= 7 ? 5 : 4;

    // True when `current` already satisfies `need` and nothing at this
    // position physically demands a fresh anchor.
    private bool AnchorCovers(
        AnchorHint? current,
        AnchorHint need,
        IReadOnlyList<SoundAtPosition> struck,
        FractionalPosition _)
    {
        if (!current.HasValue) return false;
        var cur = current.Value;

        // Finger-1 / barre position MUST match exactly.
        var hardRoot = ComputeHardRoot(struck);
        if (hardRoot.HasValue && hardRoot.Value != cur.AnchorFret) return false;

        // The need's root must not be below the current — the hand can't
        // reach lower frets than the current window's base.
        if (need.AnchorFret < cur.AnchorFret) return false;

        var lo = cur.AnchorFret;
        var hi = cur.AnchorFret + cur.Width - 1;
        foreach (var s in struck)
        {
            if (s.Fret < lo || s.Fret > hi)
                return false;
        }

        return true;
    }

    private int? ComputeHardRoot(IReadOnlyList<SoundAtPosition> struck)
    {
        for (var i = 0; i < struck.Count; i++)
        {
            if (struck[i].Finger == 1)
                return struck[i].Fret;
        }

        SoundAtPosition? lowestFingered = null;
        for (var i = 0; i < struck.Count; i++)
        {
            var s = struck[i];
            if (s.Finger <= 0) continue;
            if (lowestFingered == null || s.Finger < lowestFingered.Value.Finger)
                lowestFingered = s;
        }

        if (lowestFingered.HasValue)
        {
            var implied = lowestFingered.Value.Fret - (lowestFingered.Value.Finger - 1);
            if (implied >= MinAnchorFret) return implied;
        }

        return DetectBarreFret(struck);
    }

    // Re-anchor UP when the fret hand has clearly abandoned the low position
    // (HandAbandonment). Null holds the current anchor.
    private int? AbandonmentRoot(FractionalPosition position, int currentFret) =>
        _struckOrdinal.TryGetValue(position, out var ordinal)
            ? HandAbandonment.Root(this, ordinal, currentFret, _bassArrangement, MinAnchorFret, MaxFret)
            : null;

    // ─── Slide handling ─────────────────────────────────────────────────────

    // Unpitched slides physically move the hand, but in practice charts don't
    // emit an anchor at the landing — the next struck note resolves the
    // position. Only pitched slides force a destination anchor.
    private void SeedOutboundSlides(
        FractionalPosition position,
        AnchorHint sourceAnchor,
        IDictionary<FractionalPosition, int> pending)
    {
        var sounds = GetFrettedSoundsAt(position);
        foreach (var sound in sounds)
        {
            if (sound.SlideTo is not > 0) continue;
            if (sound.UnpitchedSlide) continue;
            var destPos = position + sound.Sustain;
            var shift = sound.SlideTo!.Value - sound.Fret;
            var destRoot = Math.Clamp(sourceAnchor.AnchorFret + shift, MinAnchorFret, MaxFret);
            if (!pending.TryGetValue(destPos, out var existing) || destRoot < existing)
                pending[destPos] = destRoot;
        }
    }

    // ─── HandShape root precomputation ──────────────────────────────────────

    private void PrecomputeHandShapeRoots()
    {
        _handShapeStartRoot = [];
        _handShapeSoftLow = [];
        _wideShapeStarts = [];
        _handShapeRootTrace = RuleTrace != null ? [] : null;

        foreach (var shape in _handShapes)
        {
            // First struck group at or after Start, but inside the shape.
            var idx = LowerBound(_sortedByStart, shape.Start);
            FractionalPosition? firstStruckPos = null;
            for (var i = idx; i < _sortedByStart.Count; i++)
            {
                var s = _sortedByStart[i];
                if (s.Position > shape.End) break;
                if (s.Position < shape.Start) continue;
                firstStruckPos = s.Position;
                break;
            }

            if (firstStruckPos.HasValue)
            {
                // Chord/held shapes: the first strum already carries every note,
                // so its min IS the anchor. A let-ring ARPEGGIO instead starts on
                // a SINGLE note and spells the shape over time, so the first note
                // is often not the lowest — anchor on the min across the WHOLE
                // shape so the hand covers the low note (owner report: a {3,5,5,5}
                // arpeggio anchored on 5, missing the fret-3 note). Scoped to the
                // single-note start so chord-shape anchors are byte-identical.
                var firstSounds = GetFrettedSoundsAt(firstStruckPos.Value);
                IReadOnlyList<SoundAtPosition> rootSounds = firstSounds;
                if (firstSounds.Count == 1)
                {
                    // A CHORD (>1 fretted sound) sitting exactly on the shape's
                    // clamped End is the NEXT hand position, not part of the
                    // arpeggio, so its (often lower) frets must not pull the
                    // arpeggio's root down — owner report: a {3,5,5,5} arpeggio
                    // before a {2,2,2} chord took the chord's fret-2 root instead of
                    // its own fret 3. A SINGLE note at End is left IN: it is the same
                    // running arpeggio sweeping on, and the hand-corrected corpus
                    // anchors those continuations together (excluding them
                    // over-segmented the anchor stream).
                    var endIsChord = GetFrettedSoundsAt(shape.End).Count > 1;
                    var arpeggioSounds = new List<SoundAtPosition>();
                    for (var i = idx; i < _sortedByStart.Count; i++)
                    {
                        var s = _sortedByStart[i];
                        if (s.Position > shape.End) break;
                        if (s.Position < shape.Start) continue;
                        if (endIsChord && s.Position == shape.End) continue;
                        arpeggioSounds.Add(s);
                    }

                    rootSounds = arpeggioSounds;
                }

                var root = ComputeRequiredRoot(rootSounds) ?? MinAnchorFret;
                _handShapeStartRoot[shape.Start] = root;
                if (firstSounds.Count > 1 && SoftRootRange(firstSounds) is { } soft && soft.Root == root)
                    _handShapeSoftLow[shape.Start] = (soft.Low, firstStruckPos.Value);
                _handShapeRootTrace?.Add(shape.Start, new AnchorRuleTrace(
                    firstSounds.Count == 1 ? AnchorRule.P1HandShapeArpeggio : AnchorRule.P1HandShapeChordStart,
                    _lastRootSource));

                // A shape whose internal reach exceeds a playable hand span is
                // NOT a lock — the hand climbs THROUGH it (e.g. a let-ring run
                // fret 2 → 9). Marking it wide lets the struck-note walk break
                // it into multiple anchors that follow the hand, instead of one
                // over-wide window that "stretches fret 2 to cover fret 7".
                if (ShapeMaxFret(shape, root) - root + 1 > MaxHandSpanWidth(root))
                    _wideShapeStarts.Add(shape.Start);
            }

            // If no fretted sounds fall in the shape (e.g. all open-string
            // chord), we mark it unresolved — the main pass will inherit
            // whatever anchor is current at the shape's start.
        }
    }

    private (FractionalPosition Start, FractionalPosition End)? FindActiveHandShape(FractionalPosition position)
    {
        // End is EXCLUSIVE: shapes abut (the next may start exactly at the
        // previous End — the per-chord import shapes always do), and an
        // inclusive check matches the PREVIOUS shape first, locking a new
        // chord to the old chord's root and never emitting its anchor.
        //
        // A rescan of the whole shape list from index 0 for EVERY
        // candidate position would be quadratic; the cursor walks it once for the whole pass. See
        // AscendingSpanCursor for the equivalence and the cost it replaced.
        var index = _handShapeCursor.IndexAt(position);
        if (index < 0) return null;

        var shape = _handShapes[index];
        // A wide shape does not lock (see PrecomputeHandShapeRoots) — fall
        // through to the struck-note walk so the hand can climb. Shapes don't
        // overlap, so this position is in no other shape.
        return _wideShapeStarts.Contains(shape.Start) ? null : shape;
    }

    // ─── Tap / chained-pull-off runs (P3) ───────────────────────────────────

    // For each position that belongs to a tap or chained-pull-off run,
    // record the run's min / max fret. This lets the main pass widen a
    // position's anchor inline without resorting to a post-pass.
    private void PrecomputeRunSpans()
    {
        _runSpanByPosition = [];

        var positions = _byPosition.Keys.OrderBy(p => p).ToList();
        for (var i = 0; i < positions.Count; i++)
        {
            var sounds = GetFrettedSoundsAt(positions[i]);
            var isTapStart = sounds.Any(s => s.Hopo == HopoType.Tap);
            var isPullRun =
                sounds.Any(s => s.Hopo == HopoType.PullOff) &&
                i + 1 < positions.Count &&
                GetFrettedSoundsAt(positions[i + 1]).Any(s => s.Hopo == HopoType.PullOff);

            if (!isTapStart && !isPullRun) continue;

            // Walk backward and forward across the continuous run.
            var start = i;
            while (start > 0)
            {
                var prev = GetFrettedSoundsAt(positions[start - 1]);
                if (prev.Any(s => s.Hopo != HopoType.None)) start--;
                else break;
            }

            // The seed note — the struck note the pull-off chain comes
            // FROM — has no HoPo itself. Include its position so the run
            // span covers its fret too.
            if (isPullRun && start > 0)
                start--;

            var end = i;
            while (end + 1 < positions.Count)
            {
                var nxt = GetFrettedSoundsAt(positions[end + 1]);
                if (nxt.Any(s => s.Hopo != HopoType.None)) end++;
                else break;
            }

            var minF = int.MaxValue;
            var maxF = int.MinValue;
            for (var j = start; j <= end; j++)
            {
                var runSounds = GetFrettedSoundsAt(positions[j]);
                if (runSounds.Count == 0) continue;
                minF = Math.Min(minF, runSounds.Min(s => s.Fret));
                maxF = Math.Max(maxF, runSounds.Max(s => s.Fret));
            }

            if (minF == int.MaxValue) continue;
            if (maxF - minF + 1 <= BaseAnchorWidth) continue; // no widening needed

            for (var j = start; j <= end; j++)
                _runSpanByPosition[positions[j]] = (minF, maxF);

            i = end;
        }
    }

    // Ordered list of every struck position with the lowest fret played there,
    // plus a position → ordinal map. Drives the abandonment look-ahead in the
    // struck-note walk (see AbandonmentRoot).
    private void PrecomputeStruckOrder()
    {
        _struckOrder = new List<(FractionalPosition, int)>(_byPosition.Count);
        _struckOrdinal = new Dictionary<FractionalPosition, int>(_byPosition.Count);

        foreach (var position in _byPosition.Keys.OrderBy(p => p))
        {
            var sounds = GetFrettedSoundsAt(position);
            if (sounds.Count == 0) continue;

            var minFret = int.MaxValue;
            foreach (var s in sounds)
            {
                if (s.Fret < minFret)
                    minFret = s.Fret;
            }

            _struckOrdinal[position] = _struckOrder.Count;
            _struckOrder.Add((position, minFret));
        }
    }

    // ─── Index construction ─────────────────────────────────────────────────

    private void BuildIndexes(ChartLevel level, IReadOnlyList<ChordTemplate>? chordTemplates,
        Func<ChordTemplate, ChordTemplate?>? freeGrip)
    {
        _byPosition = [];
        _sortedByStart = new List<SoundAtPosition>(level.Notes.Count + level.Chords.Count * 6);

        foreach (var note in level.Notes)
        {
            // A sound at or below the capo is OPEN (fret 0 sounds AT the capo),
            // so it never constrains the fret hand. Capo 0 ⇒ the original
            // "Fret <= 0" open-string filter, unchanged.
            if (note.Fret <= _capo) continue;
            var sap = SoundAtPosition.FromNote(note);
            _sortedByStart.Add(sap);
            if (!_byPosition.TryGetValue(note.Position, out var list))
            {
                list = [];
                _byPosition[note.Position] = list;
            }

            list.Add(sap);
        }

        // By Id, never by list position: ids are not indexes (an RS-XML chart's
        // need not be contiguous), and ChordFingeringFollower resolves the same way.
        var byId = chordTemplates != null ? ChordTemplateResolver.IndexById(chordTemplates) : null;
        var gripByTemplate = new Dictionary<ChordTemplate, (ChordTemplate Grip, int Slack)>(ReferenceEqualityComparer.Instance);
        foreach (var chord in level.Chords)
        {
            var template = byId != null && byId.TryGetValue(chord.TemplateId, out var found) ? found : null;

            // A FREE chord is read through its BASE grip, so the variant an earlier
            // pass shifted it onto (no finger 1) neither implies a root nor pins
            // the anchor. Bass double stops are never held: the hold is measured
            // on guitar.
            var grip = template;
            var freeSlack = NotFree;
            if (template != null && freeGrip != null)
            {
                if (!gripByTemplate.TryGetValue(template, out var resolved))
                {
                    var baseGrip = freeGrip(template);
                    resolved = baseGrip == null
                        ? (template, NotFree)
                        : (baseGrip, _bassArrangement ? NotFree : FreeFingeringSlack(baseGrip));
                    gripByTemplate[template] = resolved;
                }

                (grip, freeSlack) = resolved;
            }

            foreach (var cn in chord.ChordNotes)
            {
                if (cn.Fret <= _capo) continue;
                var finger = grip?.GetFinger(cn.StringIndex) ?? -1;
                var sap = SoundAtPosition.FromChordNote(chord.Position, cn, finger, freeSlack);
                _sortedByStart.Add(sap);
                if (!_byPosition.TryGetValue(chord.Position, out var list))
                {
                    list = [];
                    _byPosition[chord.Position] = list;
                }

                list.Add(sap);
            }
        }

        _sortedByStart.Sort((a, b) => a.Position.CompareTo(b.Position));

        _handShapes = level.HandShapes
            .Where(h => h.EndPosition > h.Position)
            .Select(h => (h.Position, h.EndPosition))
            .OrderBy(h => h.Position)
            .ToList();
        // The list is rebuilt per run, so the cursor over it is too — never
        // carried across levels.
        _handShapeCursor = new AscendingSpanCursor(_handShapes);
    }

    // ─── Indexed lookups ────────────────────────────────────────────────────

    private List<SoundAtPosition> GetFrettedSoundsAt(FractionalPosition position) => _byPosition.TryGetValue(position, out var list) ? list : EmptySoundList;

    private static int LowerBound(List<SoundAtPosition> list, FractionalPosition target)
    {
        int lo = 0, hi = list.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (list[mid].Position.CompareTo(target) < 0) lo = mid + 1;
            else hi = mid;
        }

        return lo;
    }

    // ─── Barre detection ────────────────────────────────────────────────────

    private int? DetectBarreFret(IReadOnlyList<SoundAtPosition> struck)
    {
        if (struck.Count < 2) return null;
        var fret = struck[0].Fret;
        if (fret < MinAnchorFret) return null;
        for (var i = 1; i < struck.Count; i++)
        {
            if (struck[i].Fret != fret)
                return null;
        }

        return fret;
    }

    // ─── Position enumeration ───────────────────────────────────────────────

    private List<FractionalPosition> CollectAnchorPositions(
        ChartLevel level,
        FractionalPosition? rangeStart,
        FractionalPosition? rangeEnd)
    {
        var positions = new HashSet<FractionalPosition>();

        foreach (var note in level.Notes)
        {
            AddIfInRange(positions, note.Position, rangeStart, rangeEnd);
            // Only pitched slides force a mandatory destination anchor.
            if (note.SlideTo is > 0 && !note.UnpitchedSlide)
                AddIfInRange(positions, note.Position + note.Sustain, rangeStart, rangeEnd);
        }

        foreach (var chord in level.Chords)
        {
            AddIfInRange(positions, chord.Position, rangeStart, rangeEnd);
            foreach (var chordNote in chord.ChordNotes)
            {
                if (chordNote.SlideTo is > 0 && !chordNote.UnpitchedSlide)
                    AddIfInRange(positions, chord.Position + chordNote.Sustain, rangeStart, rangeEnd);
            }
        }

        // Handshape starts are mandatory anchor positions — otherwise a shape
        // that begins on an empty beat before the first strum of its chord
        // never seeds its own anchor (handshape starvation bug).
        foreach (var shape in _handShapes)
            AddIfInRange(positions, shape.Start, rangeStart, rangeEnd);

        return positions.OrderBy(p => p).ToList();
    }

    private static void AddIfInRange(
        ISet<FractionalPosition> positions,
        FractionalPosition position,
        FractionalPosition? rangeStart,
        FractionalPosition? rangeEnd)
    {
        if (rangeStart.HasValue && position < rangeStart.Value) return;
        if (rangeEnd.HasValue && position > rangeEnd.Value) return;
        positions.Add(position);
    }

    // ─── Output ─────────────────────────────────────────────────────────────

    // Open-string sounds never anchor, so a level that opens on them starts its
    // first anchor at the first FRETTED sound and ChartExportValidator reports
    // the notes before it. The first anchor is moved back to the level's first
    // sound (inside the requested range), keeping its fret (already >= the capo
    // floor) and width. A level with no anchors at all has nothing to copy.
    private static void AnchorLeadingOpenSounds(
        List<FretAnchor> anchors,
        ChartLevel level,
        FractionalPosition? rangeStart,
        FractionalPosition? rangeEnd)
    {
        if (anchors.Count == 0) return;
        var first = anchors[0];
        FractionalPosition? firstSound = null;
        foreach (var position in level.Notes.Select(n => n.Position).Concat(level.Chords.Select(c => c.Position)))
        {
            if (rangeStart.HasValue && position < rangeStart.Value) continue;
            if (rangeEnd.HasValue && position > rangeEnd.Value) continue;
            if (!firstSound.HasValue || position < firstSound.Value) firstSound = position;
        }

        if (firstSound.HasValue && firstSound.Value < first.Position)
            anchors.Insert(0, new FretAnchor(firstSound.Value, first.Fret, first.Width));
    }

    private static List<FretAnchor> BuildAnchors(
        IReadOnlyList<FractionalPosition> allPositions,
        IReadOnlyDictionary<FractionalPosition, AnchorHint> hints)
    {
        var anchors = new List<FretAnchor>();
        foreach (var position in allPositions)
        {
            if (!hints.TryGetValue(position, out var hint)) continue;
            anchors.Add(new FretAnchor(position, hint.AnchorFret, hint.Width));
        }

        return anchors;
    }

    // ─── Types ──────────────────────────────────────────────────────────────

    // ─── Attribution tags (measurement side-channel, see RuleTrace) ──────────

    internal enum AnchorRule
    {
        P1HandShapeChordStart,
        P1HandShapeArpeggio,
        P1HandShapeInferred,
        P2SlideDestination,
        P3RunMin,
        P4P5StruckWalk,
        P6Abandonment,
        P1HeldFreeChord,
        P7PulledBack,
        P0OctaveRoot
    }

    internal enum RootSource
    {
        Unknown,
        Finger1,
        ImpliedFinger,
        Barre,
        LowestFret,
        RunMin,
        Floor,
        GroupDescend,
        Held
    }

    internal readonly record struct AnchorRuleTrace(AnchorRule Rule, RootSource Root);
}
