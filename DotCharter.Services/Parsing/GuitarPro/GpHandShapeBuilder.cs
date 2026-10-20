using Arrangement = DotCharter.Core.Domain.Project.Arrangement;
using ChartLevel = DotCharter.Core.Domain.Project.ChartLevel;

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
        GpAuthoredFingering? authoredFingers = null)
    {
        if (letRingPositions == null || letRingPositions.Count == 0) return;

        var beatBuckets = CollectBeats(level);

        // Every played onset, sorted — the bar end clips to the first sound after
        // the run so a let-ring run rings until the next played thing, never past
        // it (owner report: let-ring bars overlapped the following note).
        var allOnsets = beatBuckets.Keys.ToList();

        foreach (var run in SplitIntoRuns(beatBuckets.Values, letRingPositions))
        {
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

            var template = FindOrCreateTemplate(arrangement, frets, diagrams, authoredFingers);
            var first = run[0];
            var end = RunEnd(run, allOnsets);

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
            foreach (var sound in run.SelectMany(beat => beat.Sounds))
            {
                if (!NeedsSustain(sound))
                    sound.Sustain = FractionalPosition.Zero;
            }
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

    /// <summary>The let-ring runs of two or more consecutive let-ring beats, each one hand position.</summary>
    private static List<List<BeatInfo>> SplitIntoRuns(IEnumerable<BeatInfo> orderedBeats,
        HashSet<FractionalPosition> letRingPositions)
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
                if (current != null && (RefretsHeldString(held, beat) || WidensPastAHand(held, beat)))
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
                if (current.Count >= 2) runs.Add(current);
                current = null;
                held.Clear();
            }
        }

        if (current != null && current.Count >= 2) runs.Add(current);
        return runs;
    }

    private static FractionalPosition RunEnd(List<BeatInfo> run, List<FractionalPosition> allOnsets)
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
        var end = last.Position + ringOut;

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
    private const int MaxPlayableFretSpan = 5;

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
        GpChordDiagrams? diagrams, GpAuthoredFingering? authoredFingers)
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
        template.Name = ChordNameSuggester.SuggestFirstChordName(arrangement, template.Frets);

        // ...unless a GP6-8 chord DIAGRAM of the track is fretted exactly like
        // the run (an arpeggiated chord): then its name and fingering are
        // the tab's, and the fingering is marked authored for the FHP pass, as
        // ChordHandShapeBuilder does for a strum.
        var (authoredName, authoredGrip) = diagrams?.Borrow(frets) ?? (null, null);
        if (authoredName != null) template.Name = authoredName;
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
