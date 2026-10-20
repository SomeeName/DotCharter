using System.Collections.ObjectModel;
using DotCharter.Services.Editing.HandShapes;
using DotCharter.Services.Editing.Improvement;

namespace DotCharter.Services.Editing.Fixing;

/// <summary>
///     Port of Log Charter's ArrangementFixer pipeline, adapted to
///     DotCharter's domain. Fixers run in Log Charter's order; each is a
///     pure in-place mutation with a change count. Deliberately NOT ported:
///     chord-template dedup / unused-template removal (RscpWriter's
///     RegenerateTemplates already owns template lifecycle at save time)
///     and beat-map extension (owned by the tempo-map services).
/// </summary>
public sealed class ArrangementAutoFixService : IArrangementAutoFixService
{
    public void Fix(Arrangement arrangement, ArrangementFixResult result)
    {
        result.ArrangementsProcessed++;

        result.Add("sortedCollections", SortTimedCollections(arrangement));
        result.Add("invalidChordsFixed", RemoveInvalidChords(arrangement));
        result.Add("duplicatesRemoved", RemoveDuplicatePositions(arrangement));
        result.Add("linkedNotesJoined", JoinSimilarLinkedNotes(arrangement));
        result.Add("noteLengthsFixed", FixNoteLengths(arrangement));
        result.Add("bendsOutOfBoundsRemoved", RemoveBendsOutOfBounds(arrangement));
        result.Add("slidesFixed", FixSlides(arrangement));
        result.Add("capoFretsRaised", FixCapoFrets(arrangement));
        result.Add("anchorsAddedAtPhrases", AddMissingAnchorsAtPhraseStarts(arrangement));
        result.Add("handShapesAddedOverChords", CoverChordsOutsideHandShapes(arrangement));
        result.Add("templateFingersCleaned", FixTemplateFingers(arrangement));
        result.Add("tonesDeclared", DeclareTonesUsedByChanges(arrangement));
    }

    // ---- 1. deterministic ordering ----------------------------------------

    private static int SortTimedCollections(Arrangement arrangement)
    {
        var changed = 0;
        changed += SortInPlace(arrangement.EventPoints, e => e.Position);
        changed += SortInPlace(arrangement.ToneChanges, t => t.Position);
        foreach (var level in arrangement.Levels)
        {
            changed += SortInPlace(level.Notes, n => n.Position);
            changed += SortInPlace(level.Chords, c => c.Position);
            changed += SortInPlace(level.Anchors, a => a.Position);
            changed += SortInPlace(level.HandShapes, h => h.Position);
        }

        return changed;
    }

    private static int SortInPlace<T>(ObservableCollection<T> items, Func<T, FractionalPosition> key)
    {
        var sorted = items.OrderBy(key).ToList();
        var alreadySorted = true;
        for (var i = 0; i < items.Count; i++)
        {
            if (!ReferenceEquals(items[i], sorted[i]))
            {
                alreadySorted = false;
                break;
            }
        }

        if (alreadySorted) return 0;

        items.Clear();
        foreach (var item in sorted) items.Add(item);
        return 1;
    }

    // ---- 2. invalid chords -------------------------------------------------

    /// <summary>
    ///     A chord with NO chord notes describes no sound at all and is removed.
    ///     <para>
    ///         <b>A DANGLING TemplateId is repaired, not deleted.</b> Removing the
    ///         chord as well would throw away authored SOUNDS to fix a
    ///         broken POINTER — the shape is sitting right there in
    ///         <c>ChordNotes</c>, which is why <c>ChordTemplateResolver</c> can
    ///         resolve-or-create for it, and why the validation rules stopped
    ///         gating on <c>TemplateId >= 0</c> for the same reason (a guard that
    ///         turns a missing input into an answer). Dangling ids are ordinary
    ///         chart state, not corruption: the tab importers' idempotency skips
    ///         leave chords on the <c>-1</c> sentinel, a cross-arrangement paste
    ///         can bring an id its destination never had, and undoing
    ///         a template-creating edit can remove a value-equal template and leave
    ///         every chord pointing at it. <c>SngFromDomainMapper</c> already
    ///         drops such a chord from the exported psarc silently, so the two
    ///         failure modes stacked: the export lost it, and running auto-fix
    ///         first lost it from the CHART.
    ///     </para>
    /// </summary>
    private static int RemoveInvalidChords(Arrangement arrangement)
    {
        var templateIds = arrangement.ChordTemplates.Select(t => t.Id).ToHashSet();
        var changed = 0;
        var created = new List<ChordTemplate>();

        foreach (var level in arrangement.Levels)
        {
            for (var i = level.Chords.Count - 1; i >= 0; i--)
            {
                var chord = level.Chords[i];
                if (chord.ChordNotes.Count == 0)
                {
                    level.Chords.RemoveAt(i);
                    changed++;
                    continue;
                }

                if (chord.TemplateId < 0 || templateIds.Contains(chord.TemplateId)) continue;

                // Resolve-or-create for the shape the chord actually PLAYS. The
                // resolver keeps the arpeggio flavour of whatever the id pointed
                // at — nothing, here — so this is the plain one, and a minted
                // template arrives with suggested fingering and name.
                var template = ChordTemplateResolver.ResolveForChord(arrangement, chord, created);
                chord.TemplateId = template.Id;
                changed++;
            }
        }

        foreach (var template in created)
        {
            arrangement.ChordTemplates.Add(template);
            templateIds.Add(template.Id);
        }

        return changed;
    }

    // ---- 3. duplicate positions ---------------------------------------------

    private static int RemoveDuplicatePositions(Arrangement arrangement)
    {
        var removed = 0;
        foreach (var level in arrangement.Levels)
        {
            var seenNotes = new HashSet<(FractionalPosition, int)>();
            for (var i = 0; i < level.Notes.Count;)
            {
                if (!seenNotes.Add((level.Notes[i].Position, level.Notes[i].StringIndex)))
                {
                    level.Notes.RemoveAt(i);
                    removed++;
                }
                else
                {
                    i++;
                }
            }

            var seenChords = new HashSet<FractionalPosition>();
            for (var i = 0; i < level.Chords.Count;)
            {
                if (!seenChords.Add(level.Chords[i].Position))
                {
                    level.Chords.RemoveAt(i);
                    removed++;
                }
                else
                {
                    i++;
                }
            }

            var seenAnchors = new HashSet<FractionalPosition>();
            for (var i = 0; i < level.Anchors.Count;)
            {
                if (!seenAnchors.Add(level.Anchors[i].Position))
                {
                    level.Anchors.RemoveAt(i);
                    removed++;
                }
                else
                {
                    i++;
                }
            }

            var seenShapes = new HashSet<FractionalPosition>();
            for (var i = 0; i < level.HandShapes.Count;)
            {
                if (!seenShapes.Add(level.HandShapes[i].Position))
                {
                    level.HandShapes.RemoveAt(i);
                    removed++;
                }
                else
                {
                    i++;
                }
            }
        }

        return removed;
    }

    // ---- 4. join similar linked notes ---------------------------------------

    private static int JoinSimilarLinkedNotes(Arrangement arrangement)
    {
        var joined = 0;
        foreach (var level in arrangement.Levels)
        {
            for (var i = 0; i < level.Notes.Count - 1;)
            {
                var current = level.Notes[i];
                var next = FindNextOnString(level.Notes, i, current.StringIndex);
                if (next == null
                    || !current.LinkNext
                    || current.SlideTo != null || current.UnpitchedSlide
                    || next.Position != current.EndPosition
                    || next.Fret != current.Fret
                    || next.Vibrato != current.Vibrato
                    || next.Tremolo != current.Tremolo)
                {
                    i++;
                    continue;
                }

                // Merge: current absorbs next's span, bends and link state.
                current.Sustain = next.EndPosition - current.Position;
                current.LinkNext = next.LinkNext;
                current.SlideTo = next.SlideTo;
                current.UnpitchedSlide = next.UnpitchedSlide;
                foreach (var bend in next.BendValues) current.BendValues.Add(bend);
                level.Notes.Remove(next);
                joined++;
                // Re-check the same index — the merged note may link again.
            }
        }

        return joined;
    }

    private static GuitarNote? FindNextOnString(IList<GuitarNote> notes, int fromIndex, int stringIndex)
    {
        for (var j = fromIndex + 1; j < notes.Count; j++)
        {
            if (notes[j].StringIndex == stringIndex)
                return notes[j];
        }

        return null;
    }

    // ---- 4b. sounds linked INTO a chord: DELIBERATELY NOT A FIXER ------------
    //
    // There is deliberately no `UnlinkSoundsLinkedIntoChords` pass here, which would clear
    // LinkNext on every sound whose sustain ended on a chord claiming the same
    // string. It must not exist: a LINKED CHORD IS LEGAL TO
    // AUTHOR, and what RS cannot spell is the LANDING, which is repaired at the
    // EXPORT boundary by Parsing/Xml/Song/LinkLandingBreakup — never in the
    // chart. (Same rule, same reason, as the inspector not raising
    // NoteLinkedToChord; see docs/agents/services/export-companions.md.)
    //
    // It would not be merely redundant, it would be DESTRUCTIVE, in the one way a fixer
    // that "only drops a flag" is easy to believe is safe. Clearing LinkNext
    // leaves the SUSTAIN untouched — so the tie that renders as a link
    // would become a bare sustain running visibly past the end of its own hand shape,
    // and every linked slide chord in the chart would come apart at once. On the
    // owner's chart one auto-fix run would clear 27 links, which is exactly the 27
    // findings the removed LinkNextRule reported: the fixer would be
    // "repairing" something the export already handles correctly.

    // ---- 5. note lengths -----------------------------------------------------

    private static int FixNoteLengths(Arrangement arrangement)
    {
        var fixedCount = 0;
        foreach (var level in arrangement.Levels)
        {
            // Per-string timeline over notes AND chord notes so a sustain can
            // never overlap the next sound on its string.
            //
            // PassOtherNotes travels WITH the entry, because a CHORD NOTE does
            // not carry the flag — `PassOtherNotes` lives on Chord, since a whole
            // strum rings out or it does not. Reading it only off GuitarNote left
            // the chord half of this pass unable to see it at all, so one
            // auto-fix run truncated every authored ring-out on every chord in
            // the chart: a fixer changing far more than the finding it names.
            // ChartSpacingEnforcer is the reference and derives it the same way
            // (`new SoundEntry(chordNote.StringIndex, chord.Position, chordNote,
            // chord.PassOtherNotes)`); the two must not disagree about which
            // sustains are authored.
            var byString = new Dictionary<int, List<(FractionalPosition pos, object sound, bool passOtherNotes)>>();
            foreach (var note in level.Notes)
                Track(byString, note.StringIndex, note.Position, note, note.PassOtherNotes);
            foreach (var chord in level.Chords)
            {
                foreach (var cn in chord.ChordNotes)
                    Track(byString, cn.StringIndex, chord.Position, cn, chord.PassOtherNotes);
            }

            foreach (var (_, sounds) in byString)
            {
                sounds.Sort(static (a, b) => a.pos < b.pos ? -1 : a.pos > b.pos ? 1 : 0);
                for (var i = 0; i < sounds.Count - 1; i++)
                {
                    if (sounds[i].passOtherNotes) continue;

                    var nextPos = sounds[i + 1].pos;
                    switch (sounds[i].sound)
                    {
                        case GuitarNote n
                            when n.Sustain != FractionalPosition.Zero && n.EndPosition > nextPos:
                            n.Sustain = FractionalPosition.Max(FractionalPosition.Zero, nextPos - n.Position);
                            fixedCount++;
                            break;
                        case ChordNote cn
                            when cn.Sustain != FractionalPosition.Zero
                                 && sounds[i].pos + cn.Sustain > nextPos:
                            cn.Sustain = FractionalPosition.Max(FractionalPosition.Zero, nextPos - sounds[i].pos);
                            fixedCount++;
                            break;
                    }
                }
            }
        }

        return fixedCount;

        static void Track(Dictionary<int, List<(FractionalPosition, object, bool)>> map, int stringIndex,
            FractionalPosition pos, object sound, bool passOtherNotes)
        {
            if (!map.TryGetValue(stringIndex, out var list))
                map[stringIndex] = list = [];
            list.Add((pos, sound, passOtherNotes));
        }
    }

    // ---- 6. bends outside the note ------------------------------------------

    private static int RemoveBendsOutOfBounds(Arrangement arrangement)
    {
        var removed = 0;
        foreach (var level in arrangement.Levels)
        {
            foreach (var note in level.Notes)
                removed += RemoveOutOfRange(note.BendValues, note.Position, note.EndPosition);
            foreach (var chord in level.Chords)
            {
                foreach (var cn in chord.ChordNotes)
                    removed += RemoveOutOfRange(cn.BendValues, chord.Position, chord.Position + cn.Sustain);
            }
        }

        return removed;

        static int RemoveOutOfRange(ObservableCollection<BendValue> bends,
            FractionalPosition start, FractionalPosition end)
        {
            var count = 0;
            for (var i = bends.Count - 1; i >= 0; i--)
            {
                if (bends[i].Position < start || bends[i].Position > end)
                {
                    bends.RemoveAt(i);
                    count++;
                }
            }

            return count;
        }
    }

    // ---- 7. slides ------------------------------------------------------------

    private static int FixSlides(Arrangement arrangement)
    {
        var fixedCount = 0;
        foreach (var level in arrangement.Levels)
        {
            foreach (var note in level.Notes)
                fixedCount += ClearDegenerateSlide(note);

            foreach (var chord in level.Chords)
            {
                foreach (var cn in chord.ChordNotes)
                    fixedCount += ClearDegenerateSlide(cn);
            }
        }

        return fixedCount;
    }

    /// <summary>
    ///     Log Charter: negative slide targets are cleared entirely.
    ///     EOF: a slide onto its own start fret won't export — clear it.
    ///     <para>
    ///         Shared with <see cref="FixCapoFrets" />, which can CREATE the
    ///         second case after this fixer has already run — see there.
    ///     </para>
    /// </summary>
    private static int ClearDegenerateSlide(IGuitarSound sound)
    {
        if (sound.SlideTo is not { } slideTo) return 0;
        if (slideTo > 0 && slideTo != sound.Fret) return 0;

        sound.SlideTo = null;
        sound.UnpitchedSlide = false;
        return 1;
    }

    // ---- 8. capo frets ---------------------------------------------------------

    /// <summary>
    ///     Frets are ABSOLUTE and the capo stops the string, so a non-zero fret
    ///     below the capo is unplayable (inspector <c>L01</c>): raise it to the
    ///     capo, which is the same sounding pitch. Fret 0 is the capo already and
    ///     is left alone.
    ///     <para>
    ///         <b>A slide TARGET is a fret and obeys the same rule.</b> Leaving it
    ///         behind was two bugs at once. It wrote a slide DOWN to a fret the
    ///         capo makes unplayable — an <c>L01</c> the charter never authored,
    ///         which the export gate then blocks on. And when the raise landed the
    ///         note ON its own target, it produced exactly the degenerate slide
    ///         <see cref="FixSlides" /> exists to clear, one pipeline step too
    ///         late: <c>Fix</c> was therefore NOT IDEMPOTENT — a second run
    ///         cleared it and reported a change on a chart auto-fix had just
    ///         finished with.
    ///     </para>
    /// </summary>
    private static int FixCapoFrets(Arrangement arrangement)
    {
        var capo = arrangement.Capo;
        if (capo <= 0) return 0;

        var raised = 0;
        foreach (var level in arrangement.Levels)
        {
            foreach (var note in level.Notes)
                raised += RaiseToCapo(note, capo);

            foreach (var chord in level.Chords)
            {
                foreach (var cn in chord.ChordNotes)
                    raised += RaiseToCapo(cn, capo);
            }
        }

        foreach (var template in arrangement.ChordTemplates)
        {
            foreach (var key in template.Frets.Keys.ToList())
            {
                if (template.Frets[key] > 0 && template.Frets[key] < capo)
                {
                    template.Frets[key] = capo;
                    raised++;
                }
            }
        }

        return raised;
    }

    private static int RaiseToCapo(IGuitarSound sound, int capo)
    {
        var raised = 0;

        if (sound.Fret > 0 && sound.Fret < capo)
        {
            sound.Fret = capo;
            raised++;
        }

        // A pitched slide target is a fret on the same neck. An UNPITCHED slide
        // target is a rendering hint rather than a stopped fret, but it is drawn
        // on that neck too and below the capo it points off the playable board.
        if (sound.SlideTo is { } target && target > 0 && target < capo)
        {
            sound.SlideTo = capo;
            raised++;
        }

        // The raise can land the sound on its own target; clearing it here is
        // what keeps the pipeline a fixed point (FixSlides has already run).
        raised += ClearDegenerateSlide(sound);

        return raised;
    }

    // ---- 9. anchors at phrase starts -------------------------------------------

    /// <summary>
    ///     The fix for <see cref="ArrangementIssueType.AnchorMissingAtPhraseStart" />
    ///     (E06) — see <c>PhraseAnchorRule</c> for why an anchor may not span a
    ///     phrase boundary. It restates the anchor already in force at the
    ///     phrase's first sound, so the chart is unchanged musically and
    ///     visually; the anchor is split rather than moved.
    ///     <para>
    ///         <b>Both narrowing conditions matter.</b> Inserting
    ///         at EVERY phrase of any level that has notes
    ///         ANYWHERE, and deduping only on an anchor sitting exactly on the
    ///         boundary, would litter anchors into phrases the level does not
    ///         play, and add a redundant one whenever an existing anchor sat
    ///         just after the boundary but still before the first note — which
    ///         is already correct and is what Editor on Fire accepts
    ///         (<c>eof_enforce_rs_phrase_begin_with_fret_hand_position</c>
    ///         searches the whole start→first-note range).
    ///     </para>
    /// </summary>
    private static int AddMissingAnchorsAtPhraseStarts(Arrangement arrangement)
    {
        var added = 0;

        // BOTH phrase stores — see PhraseMarkers. Keyed on EventPoint alone
        // this fixer did nothing at all for an imported chart.
        var phrases = PhraseMarkers.Playable(arrangement);
        if (phrases.Count == 0) return 0;

        var capo = arrangement.Capo;

        foreach (var level in arrangement.Levels)
        {
            if (level.Notes.Count == 0 && level.Chords.Count == 0) continue;

            for (var i = 0; i < phrases.Count; i++)
            {
                var start = phrases[i].Position;
                FractionalPosition? end = i + 1 < phrases.Count ? phrases[i + 1].Position : null;

                // Nothing played here at this difficulty: Rocksmith asks the
                // hand for nothing, so there is nothing to anchor.
                if (PhraseStartAnchors.FirstSound(level, start, end) is not { } firstSound) continue;

                // Anywhere in start→first-sound counts, not just the boundary.
                if (PhraseStartAnchors.IsCovered(level, start, firstSound)) continue;

                var previous = PhraseStartAnchors.Previous(level, start);

                // The lowest LEGAL fret-hand position is capo + 1 — an anchor
                // on or below the capo is CapoAnchorRangeRule's L02. Defaulting
                // to fret 1 manufactured that finding at every phrase this fixer
                // touched on a capo'd chart: an auto-fix must never invent an
                // error the editor never showed. An IMPORTED previous anchor can
                // already be sub-capo, which copying propagates. Math.Max only
                // ever raises, so a chart without a capo restates the previous
                // anchor verbatim — which is why this is safe to apply in bulk.
                var insert = new FretAnchor
                {
                    Position = start,
                    Fret = Math.Max(previous?.Fret ?? 1, ChartImprovementService.MinAnchorFret(capo)),
                    Width = previous?.Width ?? 4
                };

                var index = 0;
                while (index < level.Anchors.Count && level.Anchors[index].Position < insert.Position)
                    index++;
                level.Anchors.Insert(index, insert);
                added++;
            }
        }

        return added;
    }

    // (There is no local structural-phrase predicate in this file —
    // PhraseMarkers.Playable already applies PhraseMarkers.IsStructural, and a
    // second copy is how the two definitions drift apart.)

    // ---- 10. template fingers ----------------------------------------------------

    /// <summary>
    ///     A tone change naming a tone the arrangement never declares is dead in
    ///     game — Rocksmith switches only to a declared tone — and D01 flagged it
    ///     while nothing could act on it.
    ///     <para>
    ///         Placing a tone change is the natural way to author tones, so the
    ///         charter ends up with changes to "Clean" and "Overdrive" and four
    ///         empty slots, then six identical errors telling them a fact rather
    ///         than a remedy. This fills the free A–D slots from the names the
    ///         changes already use, in FIRST-USE order, so slot A is the tone the
    ///         song reaches first.
    ///     </para>
    ///     <para>
    ///         It only ever fills EMPTY slots. A declared tone is an authoring
    ///         decision (and the Tone Designer may own a body for it), so
    ///         overwriting one to make an error go away would trade a visible
    ///         problem for a silent one. Past four distinct undeclared names the
    ///         remaining changes keep their issue, which is correct: that is a
    ///         real Rocksmith limit and only the charter can decide which tones
    ///         to merge.
    ///     </para>
    /// </summary>
    private static int DeclareTonesUsedByChanges(Arrangement arrangement)
    {
        if (arrangement.ToneChanges.Count == 0) return 0;

        var declared = new HashSet<string>(ChartToneNames.Declared(arrangement), StringComparer.OrdinalIgnoreCase);
        var wanted = new List<string>();

        foreach (var change in arrangement.ToneChanges.OrderBy(c => c.Position))
        {
            var name = change.ToneName?.Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;   // the unnamed case is not ours to invent
            if (declared.Contains(name)) continue;
            if (wanted.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            wanted.Add(name);
        }

        if (wanted.Count == 0) return 0;

        var filled = 0;
        foreach (var name in wanted)
        {
            if (string.IsNullOrWhiteSpace(arrangement.ToneA)) arrangement.ToneA = name;
            else if (string.IsNullOrWhiteSpace(arrangement.ToneB)) arrangement.ToneB = name;
            else if (string.IsNullOrWhiteSpace(arrangement.ToneC)) arrangement.ToneC = name;
            else if (string.IsNullOrWhiteSpace(arrangement.ToneD)) arrangement.ToneD = name;
            else break;                                      // four slots, and that is the format

            filled++;
        }

        return filled;
    }

    /// <summary>
    ///     D11 (<see cref="ArrangementIssueType.ChordOutsideHandShape" />): covers every
    ///     chord no hand shape covers, on every level, with the "Generate hand shapes"
    /// planner — the rule and the fix share its coverage predicate, so the
    ///     fix is a fixed point. It only ADDS bars (never moves or resizes one), keeps
    ///     every sustain, and runs after the phrase-start anchors so no new bar
    ///     straddles one (I20).
    /// </summary>
    private static int CoverChordsOutsideHandShapes(Arrangement arrangement) =>
        ArrangementHandShapeGenerator.ApplyInPlace(arrangement, ArrangementHandShapeGenerator.Plan(arrangement));

    private static int FixTemplateFingers(Arrangement arrangement)
    {
        var cleaned = 0;
        foreach (var template in arrangement.ChordTemplates)
        {
            foreach (var key in template.Fingers.Keys.ToList())
            {
                if (template.Fingers[key] is < 0 or > 4)
                {
                    template.Fingers.Remove(key);
                    cleaned++;
                }
            }
        }

        return cleaned;
    }
}
