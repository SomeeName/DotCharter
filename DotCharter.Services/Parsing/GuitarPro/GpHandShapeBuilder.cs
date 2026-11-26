using Arrangement = DotCharter.Core.Domain.Project.Arrangement;
using ChartLevel = DotCharter.Core.Domain.Project.ChartLevel;
using DotCharter.Services.Editing.ChordShapes;
using DotCharter.Services.Editing.HandShapes;

namespace DotCharter.Services.Parsing.GuitarPro;

/// <summary>
///     Groups consecutive let-ring beats parsed from a Guitar Pro file into
///     HandShapes (with a synthesised chord template for the run's aggregate
///     shape) so the editor can render them as a single sustained hand position
///     rather than a string of disconnected single notes. Mirrors what MuseScore
///     does when materialising tied/let-ring runs.
///     <para>
///     <b>A let-ring run is a PLAIN hand shape, not an arpeggio (owner decision,
///     0.34.1).</b> <see cref="ChordTemplate.Arpeggio" /> is a real semantic flag
///     with downstream meaning — it sets <c>ChordMask.Arpeggio</c> in the
///     Rocksmith SNG export, picks the <c>.eof</c> arpeggio section type over the
///     plain-handshape one, and makes the 3D highway draw purple arpeggio
///     brackets instead of a hand-shape bar. Let ring is none of that: it is a
///     sustain instruction, not a "let the shape ring as ghost notes" phrase, and
///     flagging it arpeggio mislabelled it in every one of those places. Do not
///     set the flag here again.
///     </para>
/// </summary>
internal static class GpHandShapeBuilder
{
    public static void BuildLetRingHandShapes(ChartLevel level, Arrangement arrangement,
        HashSet<FractionalPosition>? letRingPositions, GpChordDiagrams? diagrams = null,
        GpAuthoredFingering? authoredFingers = null, ChordNamingOptions? naming = null,
        HashSet<FractionalPosition>? otherRunPositions = null, GpBarTimeline? bars = null)
    {
        if (letRingPositions == null || letRingPositions.Count == 0) return;
        bars ??= GpBarTimeline.None;

        var beatBuckets = CollectBeats(level);

        // Every played onset, sorted — the bar end clips to the first sound after
        // the run so a let-ring run rings until the next played thing, never past
        // it (owner report: let-ring bars overlapped the following note).
        var allOnsets = beatBuckets.Keys.ToList();

        var ordered = beatBuckets.Values.ToList();
        var runs = SplitIntoRuns(ordered, letRingPositions, otherRunPositions, bars);
        var claimed = runs.SelectMany(r => r).Select(b => b.Position).ToHashSet();
        foreach (var run in runs)
        {
            if (LeadingGripTone(ordered, run, letRingPositions, otherRunPositions, claimed, level) is { } lead)
            {
                run.Insert(0, lead);
                claimed.Add(lead.Position);
            }

            // Aggregate every (string, fret) pair the run touches into one
            // shape; "first wins" if the same string is fretted twice (the
            // hand shape only displays one fret per string anyway).
            var frets = new Dictionary<int, int>();
            foreach (var beat in run)
            {
                foreach (var (stringIdx, fret) in beat.Notes)
                    frets.TryAdd(stringIdx, fret);
            }

            if (frets.Count == 0) continue;

            var template = FindOrCreateTemplate(arrangement, frets, diagrams, authoredFingers, naming);
            var first = run[0];
            var end = RunEnd(run, allOnsets, bars);

            SupersedeShapesInside(level, first.Position, end);
            level.HandShapes.Add(new HandShape
            {
                Position = first.Position,
                EndPosition = end,
                TemplateId = template.Id,
                Name = string.Empty
            });

            // The handshape bar now carries the duration, so the run's notes become
            // short strikes — exactly the RS convention ChordHandShapeBuilder applies
            // to chords (owner report: let-ring arpeggio notes shouldn't keep their
            // sustain under the shape). A sound that needs its sustain to render a
            // technique (bend / slide / vibrato / tremolo / link) keeps it.
            // A trailing grip tone was not tabbed let ring: its own sustain stays.
            foreach (var sound in run.Where(b => letRingPositions.Contains(b.Position)).SelectMany(beat => beat.Sounds))
            {
                if (!NeedsSustain(sound))
                    sound.Sustain = FractionalPosition.Zero;
            }

            ZeroCarriedTail(run[^1], letRingPositions, end);
        }
    }

    /// <summary>
    ///     The run's trailing grip tone (<see cref="IsTrailingGripTone" />) when its sustain reaches
    ///     no further than the shape's end: the shape carries that length, so the note becomes a
    ///     strike like the let-ring notes before it. In the owner's tab every arpeggio bar ends on a
    ///     plain dotted quarter that sustained exactly to the barline, and the owner zeroed it 18
    ///     times. A note held PAST the shape (tied over the barline) keeps its sustain, and
    ///     so does one whose technique needs the length.
    /// </summary>
    private static void ZeroCarriedTail(BeatInfo tail, HashSet<FractionalPosition> letRingPositions,
        FractionalPosition end)
    {
        if (letRingPositions.Contains(tail.Position)) return;
        foreach (var sound in tail.Sounds)
        {
            if (sound is GuitarNote note && !NeedsSustain(note) && (note.Position + note.Sustain).CompareTo(end) <= 0)
                note.Sustain = FractionalPosition.Zero;
        }
    }

    /// <summary>
    ///     Every beat position with its sounds and sustain, so runs that aren't broken
    ///     by a non-let-ring beat sitting in between can be detected.
    /// </summary>
    private static SortedDictionary<FractionalPosition, BeatInfo> CollectBeats(ChartLevel level)
    {
        var beatBuckets = new SortedDictionary<FractionalPosition, BeatInfo>();
        foreach (var n in level.Notes)
        {
            var info = BucketAt(beatBuckets, n.Position, n.Sustain);
            info.Notes.Add((n.StringIndex, n.Fret));
            info.Sounds.Add(n);
            if (n.Sustain.CompareTo(info.Sustain) > 0) info.Sustain = n.Sustain;
            // The AUTHORED length survives on NotationDuration even though this
            // pass runs after AdjustSustains has zeroed the playable Sustain of a
            // sub-quarter note — it is the only reliable ring-out for the bar end
            // below (owner report: a 16th-note let-ring run's bar collapsed onto
            // its own last note's onset because Sustain was already 0).
            info.TakeAuthoredSpan(n.NotationDuration);
        }

        foreach (var ch in level.Chords)
        {
            var info = BucketAt(beatBuckets, ch.Position, FractionalPosition.Zero);
            foreach (var cn in ch.ChordNotes)
            {
                info.Notes.Add((cn.StringIndex, cn.Fret));
                info.Sounds.Add(cn);
                if (cn.Sustain.CompareTo(info.Sustain) > 0) info.Sustain = cn.Sustain;
            }

            // Same reason as the note loop above, one level in: this pass runs
            // AFTER GpPostProcessor.Run, where ChordHandShapeBuilder has already
            // zeroed a plain chord's ChordNote sustains to a strike (the chord's
            // own hand shape carries the duration). So Sustain above is 0 for
            // exactly the chords that matter and NotationDuration is the only
            // surviving authored length — without this fold a run ENDING on a
            // chord got ringOut == 0 and the bar collapsed onto the final
            // strum's onset, the same "cuts off at the start" failure the
            // AuthoredSpan mechanism was added to fix for notes. (Usually masked
            // by the "extend to the next onset" fallback below; when the run is
            // the last thing on the level there is no next onset and the bar
            // really does stop at the strum.) ChordHandShapeBuilder.AuthoredSpan
            // reads exactly this field for exactly this purpose.
            info.TakeAuthoredSpan(ch.NotationDuration);
        }

        return beatBuckets;
    }

    /// <summary>The bucket at <paramref name="position" />, opened with <paramref name="sustain" /> if it is new.</summary>
    private static BeatInfo BucketAt(SortedDictionary<FractionalPosition, BeatInfo> beatBuckets, FractionalPosition position,
        FractionalPosition sustain)
    {
        if (!beatBuckets.TryGetValue(position, out var info))
        {
            info = new BeatInfo { Position = position, Sustain = sustain };
            beatBuckets[position] = info;
        }

        return info;
    }

    /// <summary>The let-ring runs of two or more consecutive let-ring beats, each one hand position,
    /// plus the plain note that directly follows a run when the run's grip plays it
    /// (<see cref="IsTrailingGripTone" />).</summary>
    private static List<List<BeatInfo>> SplitIntoRuns(IEnumerable<BeatInfo> orderedBeats,
        HashSet<FractionalPosition> letRingPositions, HashSet<FractionalPosition>? otherRunPositions,
        GpBarTimeline bars)
    {
        var runs = new List<List<BeatInfo>>();
        List<BeatInfo>? current = null;
        var held = new Dictionary<int, int>();

        foreach (var beat in orderedBeats)
        {
            if (letRingPositions.Contains(beat.Position))
            {
                // A run that RE-FRETS a string it is already holding is two hand
                // positions, not one. The aggregate below is "first fret wins per
                // string", so without this split the shape declares the run's
                // FIRST fret on that string and every later note there
                // contradicts its own bar — inspector E05, "this sound plays
                // string 1 at fret 4, but the hand shape covering it declares
                // fret 2 there" (owner report: one let-ring bar ran
                // across five measures of a real GP8 file and flagged twice).
                // The hand moved; the bar has to move with it.
                // A barline ends the run too: a bar that restarts the same arpeggio is a new
                // shape, not the previous one held on (owner cut 8 two-bar shapes).
                if (current != null && (RefretsHeldString(held, beat) || WidensPastAHand(held, beat) ||
                                        bars.CrossesBarline(current[0].Position, beat.Position)))
                {
                    if (current.Count >= 2) runs.Add(current);
                    current = null;
                }

                if (current == null)
                {
                    current = [];
                    held.Clear();
                }

                current.Add(beat);
                foreach (var (stringIdx, fret) in beat.Notes) held.TryAdd(stringIdx, fret);
            }
            else if (current != null)
            {
                if (current.Count >= 2)
                {
                    if (!bars.CrossesBarline(current[0].Position, beat.Position) &&
                        IsTrailingGripTone(current[^1], held, beat, otherRunPositions))
                    {
                        current.Add(beat);
                    }

                    runs.Add(current);
                }

                current = null;
                held.Clear();
            }
        }

        if (current != null && current.Count >= 2) runs.Add(current);
        return runs;
    }

    /// <summary>
    ///     Whether <paramref name="beat" />, the beat right after a let-ring run, is the arpeggio's last
    ///     note played inside the run's grip: s1f1 LR, s2f3 LR, s4f3 LR, then s3f3 with no
    ///     let ring is one hand shape over four notes. It must be one single note, start no later than
    ///     the run's last beat rings to (no rest in between), and either re-strike a held string at
    ///     its held fret (the grip already plays it) or sit on a free string and fit the grip
    ///     (<see cref="HandShapeGripFit" />). A beat the OTHER run set rings from
    ///     (roll vs authored let ring) opens its own run and is never a tail.
    /// </summary>
    private static bool IsTrailingGripTone(BeatInfo last, Dictionary<int, int> held, BeatInfo beat,
        HashSet<FractionalPosition>? otherRunPositions)
    {
        if (beat.Sounds.Count != 1 || beat.Sounds[0] is not GuitarNote) return false;
        if (otherRunPositions?.Contains(beat.Position) == true) return false;
        var ringOut = last.AuthoredSpan.CompareTo(FractionalPosition.Zero) > 0 ? last.AuthoredSpan : last.Sustain;
        if (beat.Position.CompareTo(last.Position + ringOut) > 0) return false;
        var (stringIdx, fret) = beat.Notes[0];
        if (held.TryGetValue(stringIdx, out var heldFret)) return heldFret == fret;
        return HandShapeGripFit.Grow(held, beat.Notes) != null;
    }

    /// <summary>
    ///     The single note right before <paramref name="run" /> that the run's grip already holds:
    ///     it still rings when the run starts (let ring, or its authored length reaches the
    ///     run), sits on a string the run leaves free and inside the run's fret span, so the hand
    ///     shape starts at that note and its grip includes it, as an arpeggio shape does. A note
    ///     that slides or links away is the hand moving, so a slide-in origin never qualifies and
    ///     the shape starts on the slide's TARGET; a full mute keeps the pick-slide convention. A
    ///     note another run or an existing shape owns is left alone. EOF (gp_import.c) and Charter
    ///     (GP7NoteConverter) both ignore let ring at import, so neither has this rule.
    /// </summary>
    private static BeatInfo? LeadingGripTone(List<BeatInfo> ordered, List<BeatInfo> run,
        HashSet<FractionalPosition> letRingPositions, HashSet<FractionalPosition>? otherRunPositions,
        HashSet<FractionalPosition> claimed, ChartLevel level)
    {
        var at = ordered.IndexOf(run[0]);
        if (at <= 0) return null;
        var lead = ordered[at - 1];
        if (lead.Sounds.Count != 1 || lead.Sounds[0] is not GuitarNote note) return null;
        if (claimed.Contains(lead.Position) || otherRunPositions?.Contains(lead.Position) == true) return null;
        if (note.SlideTo is > 0 || note.LinkNext || note.UnpitchedSlide || note.Mute == MuteType.Full) return null;
        var ringOut = lead.AuthoredSpan.CompareTo(FractionalPosition.Zero) > 0 ? lead.AuthoredSpan : lead.Sustain;
        if (!letRingPositions.Contains(lead.Position) && (lead.Position + ringOut).CompareTo(run[0].Position) < 0) return null;
        if (level.HandShapes.Any(h => h.Position.CompareTo(lead.Position) <= 0 && h.EndPosition.CompareTo(lead.Position) > 0))
            return null;

        var grip = new HashSet<int>();
        var fretted = new List<int>();
        foreach (var (stringIdx, fret) in run.SelectMany(b => b.Notes))
        {
            grip.Add(stringIdx);
            if (fret > 0) fretted.Add(fret);
        }

        return note.Fret > 0 && fretted.Count > 0 && !grip.Contains(note.StringIndex) &&
               note.Fret >= fretted.Min() && note.Fret <= fretted.Max()
            ? lead
            : null;
    }

    /// <summary>Hand shapes whose fretted notes span more than <see cref="ReportedFretSpan" /> frets
    /// (lowest to highest): reported by the import, never changed.</summary>
    public static int CountWideShapes(ChartLevel level, Arrangement arrangement)
    {
        var count = 0;
        foreach (var shape in level.HandShapes)
        {
            var template = arrangement.FindChordTemplate(shape.TemplateId);
            var fretted = template?.Frets.Values.Where(f => f > 0).ToList();
            if (fretted is { Count: > 1 } && fretted.Max() - fretted.Min() > ReportedFretSpan) count++;
        }

        return count;
    }

    /// <summary>Frets a hand shape may span before the import report names it: four.</summary>
    public const int ReportedFretSpan = 4;

    private static FractionalPosition RunEnd(List<BeatInfo> run, List<FractionalPosition> allOnsets, GpBarTimeline bars)
    {
        var first = run[0];
        var last = run[^1];

        // The bar carries the run's ring-out. Prefer the AUTHORED length
        // (NotationDuration, which survives the sustain trim) over the raw
        // Sustain, which AdjustSustains has already zeroed for a sub-quarter
        // let-ring note and ChordHandShapeBuilder for a plain chord (the run
        // may END on one) — using it made the bar collapse onto the last note's
        // own onset (the bar would "cut off at the start").
        var ringOut = last.AuthoredSpan.CompareTo(FractionalPosition.Zero) > 0
            ? last.AuthoredSpan
            : last.Sustain;
        // The written end on the swung grid, the rule the chord shapes use: a triplet feel
        // moves an off-beat note later but not its written end, so swung onset + written
        // length overshot by the swing shift (and a downbeat one fell short of the swung
        // off-beat). A barline ends the bar too, even when only a rest bar follows.
        var end = bars.PlacedEnd(last.Position, ringOut);
        if (bars.NextBarline(last.Position) is { } barline && end.CompareTo(barline) > 0) end = barline;

        // Clip the bar to the first sound STRICTLY after the run's last note:
        // a let-ring run rings until the next played thing, never past it
        // (otherwise the bar overlaps the following note).
        // If no authored ring-out survived, the collapse above would leave
        // end == last.Position — extend it to that next sound instead so the
        // last note still visibly rings.
        var nextAfterLast = FirstOnsetAfter(allOnsets, last.Position);
        if (nextAfterLast is { } np &&
            (end.CompareTo(np) > 0 || end.CompareTo(last.Position) <= 0))
        {
            end = np;
        }

        if (end.CompareTo(first.Position) <= 0) end = last.Position + ringOut;
        return end;
    }

    /// <summary>
    ///     A run that STARTS on a chord starts on a shape the chord builder already
    ///     made, and two shapes sharing a Position make
    ///     <c>GpPostProcessor.ClampHandShapes</c> clamp the first to ZERO WIDTH — a bar
    ///     that covers nothing, still holding a template id, exported as an RS
    ///     handShape with startTime == endTime (found by an independent re-import
    ///     check). The run describes the same grip over a longer span, so
    ///     it SUPERSEDES that shape rather than standing beside it. Only a shape the
    ///     run fully contains is replaced; one that outlives the run is a different
    ///     hand position and is left to the clamp.
    /// </summary>
    private static void SupersedeShapesInside(ChartLevel level, FractionalPosition start, FractionalPosition end)
    {
        for (var i = level.HandShapes.Count - 1; i >= 0; i--)
        {
            var existing = level.HandShapes[i];
            if (existing.Position.CompareTo(start) == 0 &&
                existing.EndPosition.CompareTo(end) <= 0)
            {
                level.HandShapes.RemoveAt(i);
            }
        }
    }

    /// <summary>
    ///     Whether adding <paramref name="beat" /> would stretch the run's grip
    ///     past what one hand can hold. <see cref="RefretsHeldString" /> only
    ///     catches a string played TWICE at different frets; a string the run has
    ///     never touched slips past it and widens the aggregate instead.
    ///     <para>
    ///     Real hit (found by an independent re-import check): a
    ///     let-ring run starting on a {8,10,8,9,8} chord grip absorbed a lone
    ///     fret-2 note NINE beats later and synthesized a
    ///     <c>2 8 10 8 9 8</c> template — an 8-fret stretch, physically
    ///     impossible, and invisible to the E05 fret check because the shape is
    ///     built FROM the notes it covers, so it can never contradict them.
    ///     </para>
    ///     <para>
    ///     Open strings are excluded: fret 0 asks nothing of the fret hand. The
    ///     bound is the same one <c>GpImportProbe</c> already calls an "absurd"
    ///     template — more than 5 frets between the lowest and highest fretted
    ///     note.
    ///     </para>
    /// </summary>
    private static bool WidensPastAHand(Dictionary<int, int> held, BeatInfo beat)
    {
        var low = int.MaxValue;
        var high = int.MinValue;

        foreach (var fret in held.Values)
        {
            if (fret > 0)
            {
                low = Math.Min(low, fret);
                high = Math.Max(high, fret);
            }
        }

        foreach (var (stringIdx, fret) in beat.Notes)
        {
            if (fret <= 0 || held.ContainsKey(stringIdx)) continue;
            var newLow = Math.Min(low, fret);
            var newHigh = Math.Max(high, fret);
            if (newLow <= newHigh && newHigh - newLow > MaxPlayableFretSpan) return true;
        }

        return false;
    }

    /// <summary>Frets between the lowest and highest FRETTED note of one hand
    /// shape, above which the grip is not a hand position at all.</summary>
    private const int MaxPlayableFretSpan = HandShapeGripFit.MaxFretSpan;

    /// <summary>Whether <paramref name="beat" /> plays a string the run already
    /// holds, at a DIFFERENT fret — the signal that the fret hand moved and the
    /// run must be cut here. A string the run has not touched yet, or one played
    /// at the same fret again, keeps the run going.</summary>
    private static bool RefretsHeldString(Dictionary<int, int> held, BeatInfo beat)
    {
        foreach (var (stringIdx, fret) in beat.Notes)
        {
            if (held.TryGetValue(stringIdx, out var heldFret) && heldFret != fret)
                return true;
        }

        return false;
    }

    /// <summary>
    ///     Find-or-create the run's shape, the same contract
    ///     <see cref="ChordHandShapeBuilder" /> uses: a <see cref="ChordTemplate" />
    ///     is SHARED and deduped by frets, never mutated in place. A
    ///     let-ring shape is an ORDINARY hand shape (see the class remarks), so it
    ///     participates in the same dedupe pool — a run whose aggregate shape a
    ///     chord already plays reuses that chord's template instead of adding a
    ///     duplicate.
    /// </summary>
    private static ChordTemplate FindOrCreateTemplate(Arrangement arrangement, Dictionary<int, int> frets,
        GpChordDiagrams? diagrams, GpAuthoredFingering? authoredFingers, ChordNamingOptions? naming)
    {
        foreach (var t in arrangement.ChordTemplates)
        {
            if (!t.Arpeggio && FretsMatch(t, frets))
                return t;
        }

        var template = new ChordTemplate { Name = string.Empty };
        foreach (var (stringIdx, fret) in frets) template.Frets[stringIdx] = fret;

        // A let-ring run is single notes, so no strum names it — but the shape
        // is still a real hand position, so it gets the same charting-ready
        // treatment every other imported shape does (shape catalogue +
        // hand-aware fallback fingering, tuning-aware name). The human can
        // still override both.
        ChordTemplateFingerSetter.SetSuggestedFingers(template, arrangement.StringCount);

        // ...unless a GP6-8 chord DIAGRAM of the track is fretted exactly like
        // the run (an arpeggiated chord): then its name and fingering are
        // the tab's, and the fingering is marked authored for the FHP pass, as
        // ChordHandShapeBuilder does for a strum.
        var (authoredName, authoredGrip) = diagrams?.Borrow(frets) ?? (null, null);
        template.Name = authoredName ?? NewChordNames.For(arrangement, template.Frets, naming);
        if (authoredGrip != null)
        {
            foreach (var (stringIdx, finger) in authoredGrip) template.Fingers[stringIdx] = finger;
            authoredFingers?.MarkAuthored(template);
        }

        template.Id = arrangement.ChordTemplates.Count == 0
            ? 0
            : arrangement.ChordTemplates.Max(t => t.Id) + 1;
        arrangement.ChordTemplates.Add(template);
        return template;
    }

    private static bool FretsMatch(ChordTemplate template, Dictionary<int, int> frets)
    {
        if (template.Frets.Count != frets.Count) return false;
        foreach (var (stringIdx, fret) in frets)
        {
            if (!template.Frets.TryGetValue(stringIdx, out var existing) || existing != fret)
                return false;
        }

        return true;
    }

    /// <summary>First onset strictly after <paramref name="after" /> in the sorted
    /// onset list, or null when the run is the last thing in the level.</summary>
    private static FractionalPosition? FirstOnsetAfter(List<FractionalPosition> sortedOnsets,
        FractionalPosition after)
    {
        int lo = 0, hi = sortedOnsets.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (sortedOnsets[mid].CompareTo(after) <= 0) lo = mid + 1;
            else hi = mid;
        }

        return lo < sortedOnsets.Count ? sortedOnsets[lo] : null;
    }

    /// <summary>A let-ring note keeps its sustain only when a technique
    /// needs the length to render — otherwise the handshape bar carries it and the
    /// note is a strike.</summary>
    private static bool NeedsSustain(IGuitarSound sound) =>
        sound.SlideTo.HasValue || sound.UnpitchedSlide || sound.Vibrato ||
        sound.Tremolo || sound.LinkNext || sound.BendValues.Count > 0;

    private sealed class BeatInfo
    {
        public FractionalPosition Position { get; set; }
        public FractionalPosition Sustain { get; set; } = FractionalPosition.Zero;

        /// <summary>Max authored NotationDuration of the beat's notes AND chords,
        /// as a span. Survives the sustain trim (AdjustSustains for notes,
        /// ChordHandShapeBuilder's strike trim for chords); the bar-end ring-out
        /// prefers it.</summary>
        public FractionalPosition AuthoredSpan { get; set; } = FractionalPosition.Zero;

        public void TakeAuthoredSpan(Fraction? notationDuration)
        {
            if (notationDuration is not { Numerator: > 0 } duration) return;

            var authored = new FractionalPosition(0, duration);
            if (authored.CompareTo(AuthoredSpan) > 0) AuthoredSpan = authored;
        }

        public List<(int StringIndex, int Fret)> Notes { get; } = [];
        public List<IGuitarSound> Sounds { get; } = [];
    }
}
