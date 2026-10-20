namespace DotCharter.Services.Editing.Improvement;

/// <summary>
/// Auto difficulty-level generation — the inverse of
/// <see cref="LevelSquashService.TrySquash"/>: expands a max-difficulty chart
/// into N progressively thinner levels so self-authored charts get dynamic
/// difficulty and proper RS export instead of a single level.
///
/// Deterministic: the same arrangement + level count (+ beat grid) always
/// produces byte-identical levels. Rules, in order:
///
/// 1. <b>Density thinning, the way official ladders thin.</b> Phrase-starting
///    sounds come first and are never dropped; every other sound is ranked by
///    how often an official ladder keeps its kind on an easy level — the metric
///    weight of its onset (bar start, mid-bar beat, beat, off-beat) times its
///    length (the gap to the next onset, or its sustain if longer) — with equal
///    ranks spread evenly across the song by a van der Corput ordering. Level k
///    keeps the first <c>OfficialDensity(k/(N-1))</c> share of that fixed
///    ranking, the official mean density curve; because the ranking is shared,
///    every level's sound set is a superset of the level below it (monotonic
///    density). Measured against the base game's ladders by
///    <c>LocalLevelLadderScoreTest</c>: onset F1 0.775 → 0.819.
/// 2. <b>Chord reduction in steps, the way official ladders reduce.</b> A
///    kept chord plays the strings <see cref="ChordReductionPlanner" /> keeps at
///    the level's depth: the bass string first, then the strings above it in
///    order as the depth rises, from the keep rates measured on 688 official
///    arrangements. One string left is the bass note (keeping its sustain,
///    mute, harmonic and slap/pop); a proper subset is a reduced chord on its
///    own resolved template, fingered as the full chord fingers it; all of
///    them is the chord. Hand shapes follow what is played
///    (<see cref="LevelHandShapes" />): a bar stays over the chords a level
///    keeps (on the reduced template over reduced grips) and over a passage
///    that still has two or more distinct notes. New templates are appended
///    to <see cref="Arrangement.ChordTemplates" />; none is ever mutated.
/// 3. <b>Technique stripping.</b> On levels below half difficulty
///    (ratio &lt; 0.5) notes and chord notes lose bends,
///    slides (pitched + unpitched), tremolo, vibrato and HOPO — they become
///    plain picked notes. Palm mute is retained (it changes sound, not
///    difficulty). Harmonics are left untouched (removing one changes pitch).
/// 4. <b>LinkNext hygiene.</b> A kept note's LinkNext flag is cleared when the
///    note it linked to did not survive thinning on that level — and, on every
///    generated level below the top, when the level's own REWRITES left the
///    chain unspellable even though the target survived. "Did my target
///    survive?" is only half the question on a level that also rewrites what it
///    keeps: rule 2 drops strings from the landing chord (so nothing may land on
///    the source's string at all) and rule 3 strips the slide that explained a
///    fret jump.
///    Both leave a dangling link that <c>LinkNextRule</c> reports —
///    <c>LinkNextMissingTargetNote</c> and <c>LinkNextFretMismatch</c> — on
///    every level the generator wrote.
///
/// Anchors and fret-hand-mute markers are carried onto every level. The top
/// level (index N-1) is always a verbatim clone of the source level.
/// <see cref="Arrangement.PhraseInfos"/> MaxDifficulty is set to N-1 for every
/// phrase marker in EITHER phrase store (the value
/// <c>DynamicDifficultyEngine</c> clamps against) — except the structural
/// COUNT / END / noguitar phrases, which stay at 0. See UpdatePhraseInfos.
/// </summary>
public sealed class LevelGenerationService : ILevelGenerationService
{
    /// <summary>Levels with keep-ratio below this get technique stripping.</summary>
    private const double SimplifyBelowRatio = 0.5;

    public bool TryGenerate(Arrangement arrangement, int levelCount, IList<BeatMarker>? beats = null,
        IProgress<int>? levelProgress = null)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        if (levelCount < 2) return false;
        if (arrangement.Levels.Count == 0) return false;

        // Source = the hardest existing level (a squashed chart has exactly one).
        var source = arrangement.Levels.OrderBy(l => l.DifficultyIndex).Last();

        var sounds = RankSounds(source, arrangement, beats);
        var newLevels = new List<ChartLevel>(levelCount);
        var chords = new ChordReductionPlanner(arrangement);

        for (var k = 0; k < levelCount; k++)
        {
            var ratio = (k + 1) / (double)levelCount;
            var keepCount = k == levelCount - 1
                ? sounds.Count
                : Math.Min(sounds.Count, (int)Math.Ceiling(sounds.Count * OfficialDensity(k / (double)(levelCount - 1))));
            // Phrase-starting sounds are never dropped.
            var tier0Count = sounds.Count(s => s.Tier == 0);
            keepCount = Math.Max(keepCount, tier0Count);

            var kept = new HashSet<object>(ReferenceEqualityComparer.Instance);
            for (var i = 0; i < keepCount; i++) kept.Add(sounds[i].Sound);

            newLevels.Add(k == levelCount - 1
                ? BuildTopLevel(source, k)
                : BuildLevel(source, k, ratio, k / (double)(levelCount - 1), kept, chords));
            levelProgress?.Report(k + 1);
        }

        arrangement.Levels.Clear();
        foreach (var level in newLevels) arrangement.Levels.Add(level);
        foreach (var template in chords.Created) arrangement.ChordTemplates.Add(template);

        UpdatePhraseInfos(arrangement, levelCount - 1);
        return true;
    }

    // ---------------------------------------------------------------- ranking

    private sealed record RankedSound(object Sound, FractionalPosition Position, int Tier, double SpreadKey, int Index);

    /// <summary>Fixed, deterministic keep-priority order over all sounds of the
    /// source level. Earlier = survives to lower difficulty levels.</summary>
    private static List<RankedSound> RankSounds(ChartLevel source, Arrangement arrangement,
        IList<BeatMarker>? beats)
    {
        // Merge notes + chords into one position-ordered sound list. Chords
        // sort before notes at the same position, then lower strings first —
        // purely to make the ranking total and deterministic.
        var ordered = new List<(object Sound, FractionalPosition Position, int KindOrder, int StringOrder)>();
        foreach (var chord in source.Chords) ordered.Add((chord, chord.Position, 0, -1));
        foreach (var note in source.Notes) ordered.Add((note, note.Position, 1, note.StringIndex));
        var sortedSounds = ordered
            .OrderBy(s => s.Position)
            .ThenBy(s => s.KindOrder)
            .ThenBy(s => s.StringOrder)
            .ToList();

        var phraseStartSounds = FindPhraseStartSounds(sortedSounds.Select(s => (s.Sound, s.Position)).ToList(),
            arrangement);

        // Phrase-starting sounds first, never dropped; every other sound by how
        // likely an official ladder keeps it on an easy level.
        var importance = SoundImportance(sortedSounds.Select(s => (s.Sound, s.Position)).ToList(), beats);
        var result = new List<RankedSound>(sortedSounds.Count);
        for (var i = 0; i < sortedSounds.Count; i++)
        {
            var (sound, position, _, _) = sortedSounds[i];
            var tier = phraseStartSounds.Contains(sound) ? 0 : 1;
            result.Add(new RankedSound(sound, position, tier, -importance[i], i));
        }

        // Equal importance spreads evenly across the song (van der Corput over the
        // index), so thinning a run of identical eighths does not chop its tail.
        return result
            .OrderBy(r => r.Tier)
            .ThenBy(r => r.SpreadKey)
            .ThenBy(r => RadicalInverse(r.Index))
            .ThenBy(r => r.Index)
            .ToList();
    }

    /// <summary>
    ///     How strongly an official ladder keeps each sound, from what the base game's 159
    ///     dynamic-difficulty arrangements keep on their easy levels
    ///     (<c>LocalLevelLadderScoreTest</c>): the metric weight of its onset (a bar start is kept
    ///     ~13x as often as a plain beat, a mid-bar beat ~2x) times its LENGTH, the longer of the
    ///     gap to the next onset and its own sustain (a note held 1.5 s+ is kept ~5x as often as
    ///     the average).
    /// </summary>
    private static double[] SoundImportance(IReadOnlyList<(object Sound, FractionalPosition Position)> sounds,
        IList<BeatMarker>? beats)
    {
        var times = sounds.Select(s => TimeMs(s.Position, beats)).ToArray();
        var importance = new double[sounds.Count];
        var next = double.NaN;
        for (var i = sounds.Count - 1; i >= 0; i--)
        {
            if (i + 1 < sounds.Count && sounds[i + 1].Position != sounds[i].Position) next = times[i + 1];
            var gap = double.IsNaN(next) ? 2_000 : next - times[i];
            var length = Math.Max(gap, SustainMs(sounds[i].Sound, beats));
            importance[i] = MetricWeight(sounds[i].Position, beats) * Math.Clamp(length / 300.0, 0.3, 5.0);
        }


        return importance;
    }

    private static double MetricWeight(FractionalPosition position, IList<BeatMarker>? beats)
    {
        if (position.Fraction != Fraction.Zero) return position.Fraction == new Fraction(1, 2) ? 0.8 : 0.45;
        if (IsMeasureStart(position.BeatId, beats)) return 13;
        return IsMidBar(position.BeatId, beats) ? 2 : 1;
    }

    private static bool IsMidBar(int beatId, IList<BeatMarker>? beats)
    {
        if (beats == null || beatId <= 0 || beatId >= beats.Count) return false;
        var bar = beatId;
        while (bar > 0 && !beats[bar].IsMeasure) bar--;
        var length = Math.Max(1, beats[bar].BeatsInMeasure);
        return length % 2 == 0 && beatId - bar == length / 2;
    }

    private static double TimeMs(FractionalPosition position, IList<BeatMarker>? beats) =>
        beats is { Count: > 1 } ? position.GetPositionMs(beats) : position.DoubleValue * 500;

    private static double SustainMs(object sound, IList<BeatMarker>? beats)
    {
        var (position, end) = sound switch
        {
            GuitarNote note => (note.Position, note.EndPosition),
            Chord chord when chord.ChordNotes.Count > 0 => (chord.Position, chord.Position + chord.ChordNotes.Max(n => n.Sustain)),
            _ => (FractionalPosition.Zero, FractionalPosition.Zero)
        };
        return end <= position ? 0 : TimeMs(end, beats) - TimeMs(position, beats);
    }

    /// <summary>The share of the hardest level's onsets an official ladder keeps at each depth
    /// (0 = easiest, 1 = hardest), in steps of 0.05: the mean over the base game's 159
    /// dynamic-difficulty arrangements. It is not linear: the easiest level keeps ~6 %, and
    /// the upper half of a ladder fills in faster than the lower.</summary>
    private static readonly double[] OfficialDensityCurve =
    [
        0.056, 0.091, 0.116, 0.161, 0.206, 0.251, 0.321, 0.357, 0.420, 0.494, 0.575,
        0.631, 0.689, 0.756, 0.821, 0.844, 0.899, 0.925, 0.953, 0.981, 1.0
    ];

    private static double OfficialDensity(double depth)
    {
        var x = Math.Clamp(depth, 0, 1) * (OfficialDensityCurve.Length - 1);
        var i = Math.Min((int)x, OfficialDensityCurve.Length - 2);
        return OfficialDensityCurve[i] + (OfficialDensityCurve[i + 1] - OfficialDensityCurve[i]) * (x - i);
    }

    /// <summary>The first sound at/after each phrase boundary. Boundaries come
    /// from event points with a phrase name plus explicit phrase iterations.</summary>
    private static HashSet<object> FindPhraseStartSounds(
        IReadOnlyList<(object Sound, FractionalPosition Position)> soundsByPosition, Arrangement arrangement)
    {
        var boundaries = arrangement.EventPoints
            .Where(ep => !string.IsNullOrWhiteSpace(ep.Phrase))
            .Select(ep => ep.Position)
            .Concat(arrangement.PhraseIterations.Select(pi => pi.Position))
            .Distinct()
            .OrderBy(p => p)
            .ToList();

        var starts = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var boundary in boundaries)
        {
            foreach (var (sound, position) in soundsByPosition)
            {
                if (position < boundary) continue;
                starts.Add(sound);
                break;
            }
        }

        return starts;
    }

    private static bool IsMeasureStart(int beatId, IList<BeatMarker>? beats)
    {
        if (beats == null || beats.Count == 0) return false;
        return beatId >= 0 && beatId < beats.Count && beats[beatId].IsMeasure;
    }

    /// <summary>Van der Corput radical inverse in base 2 (bit reversal of the
    /// index into [0,1)) — deterministic even spreading.</summary>
    private static double RadicalInverse(int index)
    {
        var result = 0.0;
        var factor = 0.5;
        var i = (uint)index;
        while (i > 0)
        {
            result += factor * (i & 1);
            i >>= 1;
            factor *= 0.5;
        }

        return result;
    }

    // ---------------------------------------------------------------- levels

    /// <summary>The top level: a verbatim clone of the source, links, techniques and hand shapes
    /// exactly as the charter authored them.</summary>
    private static ChartLevel BuildTopLevel(ChartLevel source, int difficultyIndex)
    {
        var level = new ChartLevel { DifficultyIndex = difficultyIndex };
        foreach (var note in source.Notes) level.Notes.Add(note.Clone());
        foreach (var chord in source.Chords) level.Chords.Add(chord.Clone());
        foreach (var anchor in source.Anchors) level.Anchors.Add(anchor.Clone());
        foreach (var fhm in source.FretHandMutes) level.FretHandMutes.Add(new FretHandMute(fhm.Position));
        foreach (var handShape in source.HandShapes) level.HandShapes.Add(handShape.Clone());
        SortLevel(level);
        return level;
    }

    private static ChartLevel BuildLevel(ChartLevel source, int difficultyIndex, double ratio, double depth,
        HashSet<object> kept, ChordReductionPlanner chords)
    {
        var level = new ChartLevel { DifficultyIndex = difficultyIndex };
        var simplify = ratio < SimplifyBelowRatio;

        foreach (var note in source.Notes)
        {
            if (!kept.Contains(note)) continue;
            var clone = note.Clone();
            if (simplify) StripTechniques(clone);
            if (clone.LinkNext && !IsLinkTargetKept(source, kept, note.StringIndex, note.Position))
                clone.LinkNext = false;
            level.Notes.Add(clone);
        }

        // Rule 2: each kept chord plays the subset of its strings the measured ladder keeps at
        // this depth — the whole chord, a reduced grip on its own template, or its bass note.
        var reducedChords = new HashSet<Chord>(ReferenceEqualityComparer.Instance);
        var bassNotes = new HashSet<GuitarNote>(ReferenceEqualityComparer.Instance);
        foreach (var chord in source.Chords)
        {
            if (!kept.Contains(chord)) continue;
            var keptStrings = ChordReductionPlanner.KeptStrings(chord, depth);
            if (keptStrings.Count == 1 && chord.ChordNotes.Count > 1)
            {
                if (TrySimplifyChordToNote(chord, keepTechniques: !simplify, out var rootNote))
                {
                    if (rootNote.LinkNext && !IsLinkTargetKept(source, kept, rootNote.StringIndex, chord.Position))
                        rootNote.LinkNext = false;
                    level.Notes.Add(rootNote);
                    bassNotes.Add(rootNote);
                }

                continue;
            }

            Chord clone;
            if (keptStrings.Count < chord.ChordNotes.Select(n => n.StringIndex).Distinct().Count())
            {
                clone = chords.Reduce(chord, keptStrings);
                reducedChords.Add(clone);
            }
            else
            {
                clone = chord.Clone();
            }

            foreach (var chordNote in clone.ChordNotes)
            {
                if (simplify) StripTechniques(chordNote);
                if (chordNote.LinkNext && !IsLinkTargetKept(source, kept, chordNote.StringIndex, chord.Position))
                    chordNote.LinkNext = false;
            }

            level.Chords.Add(clone);
        }

        // Anchors + fret-hand-mute markers guide the hand at every difficulty.
        foreach (var anchor in source.Anchors) level.Anchors.Add(anchor.Clone());
        foreach (var fhm in source.FretHandMutes) level.FretHandMutes.Add(new FretHandMute(fhm.Position));

        // A generated level REWRITES the sounds it keeps (reduced grips, bass notes, stripped
        // techniques), so "my target survived" is not enough — see rule 4. Re-check every
        // surviving link against what THIS level actually spells.
        ClearLinksThisLevelCannotSpell(level);

        SortLevel(level);
        LevelHandShapes.Carry(source, level, depth, reducedChords, bassNotes);
        return level;
    }

    private static void SortLevel(ChartLevel level)
    {
        SortByPosition(level.Notes, static n => n.Position);
        SortByPosition(level.Chords, static c => c.Position);
        SortByPosition(level.Anchors, static a => a.Position);
        SortByPosition(level.HandShapes, static h => h.Position);
        SortByPosition(level.FretHandMutes, static f => f.Position);
    }

    /// <summary>
    ///     Chord → single note: the root (lowest-string) chord note keeps the
    ///     chord's position and its own sustain, palm/full mute, harmonic and
    ///     bass slap/pop; every other technique is stripped. A chord with no
    ///     chord notes has no root to keep and is dropped.
    ///     <para>
    ///         <b>This is a DIFFICULTY reduction, so it may only drop what makes
    ///         a sound harder</b> — the same line <see cref="StripTechniques(GuitarNote)" />
    ///         draws for single notes, and for the same reasons. A harmonic
    ///         changes the sounding PITCH, bass slap/pop changes how the note is
    ///         produced, and <c>Ignore</c> decides whether the game judges the
    ///         sound at all; none of the three is difficulty and all three used
    ///         to vanish here, because this note is built from scratch rather
    ///         than cloned. A strum the charter marked ignore came back as a
    ///         judged note two difficulties down.
    ///     </para>
    /// </summary>
    private static bool TrySimplifyChordToNote(Chord chord, bool keepTechniques, out GuitarNote rootNote)
    {
        rootNote = null!;
        var root = chord.ChordNotes.OrderBy(n => n.StringIndex).FirstOrDefault();
        if (root == null) return false;

        rootNote = new GuitarNote
        {
            Position = chord.Position,
            StringIndex = root.StringIndex,
            Fret = root.Fret,
            Sustain = root.Sustain,
            Mute = root.Mute,
            Harmonic = root.Harmonic,
            BassPicking = root.BassPicking,
            Ignore = chord.Ignore || root.Ignore,
            Accent = chord.Accent
        };
        if (!keepTechniques) return true;

        // Above the technique-stripping levels the bass note plays the chord note as charted.
        foreach (var bend in root.BendValues) rootNote.BendValues.Add(bend.Clone());
        rootNote.SlideTo = root.SlideTo;
        rootNote.UnpitchedSlide = root.UnpitchedSlide;
        rootNote.Tremolo = root.Tremolo;
        rootNote.Vibrato = root.Vibrato;
        rootNote.VibratoStrength = root.VibratoStrength;
        rootNote.Hopo = root.Hopo;
        rootNote.LinkNext = root.LinkNext;
        return true;
    }

    /// <summary>
    ///     Clears every LinkNext this level's own rewrites left dangling. A link can only land on
    ///     a sound THIS level puts on the same string where the source's sustain ends; after chord
    ///     reduction that string is often gone (a reduced grip or a bass note keeps fewer strings),
    ///     and after technique stripping the slide that justified a fret jump is gone with it.
    ///     Both are the exact shapes <c>LinkNextRule</c> reports, so the generator may not
    ///     manufacture them. Every generated level except the top, whose links must survive
    ///     exactly as the charter authored them.
    /// </summary>
    private static void ClearLinksThisLevelCannotSpell(ChartLevel level)
    {
        var landings = new Dictionary<(int StringIndex, FractionalPosition Position), int>();
        foreach (var note in level.Notes)
            landings.TryAdd((note.StringIndex, note.Position), note.Fret);
        foreach (var chord in level.Chords)
        {
            foreach (var chordNote in chord.ChordNotes)
                landings.TryAdd((chordNote.StringIndex, chord.Position), chordNote.Fret);
        }

        foreach (var note in level.Notes)
        {
            if (note.LinkNext && !Lands(note.StringIndex, note.Position, note.EndPosition, note.Fret, note.SlideTo))
                note.LinkNext = false;
        }

        foreach (var chord in level.Chords)
        {
            foreach (var chordNote in chord.ChordNotes)
            {
                if (chordNote.LinkNext && !Lands(chordNote.StringIndex, chord.Position, chord.Position + chordNote.Sustain,
                        chordNote.Fret, chordNote.SlideTo))
                {
                    chordNote.LinkNext = false;
                }
            }
        }

        bool Lands(int stringIndex, FractionalPosition start, FractionalPosition end, int fret, int? slideTo) =>
            end != start
            && landings.TryGetValue((stringIndex, end), out var landingFret)
            && (landingFret == fret || slideTo == landingFret);
    }

    private static void StripTechniques(ChordNote chordNote)
    {
        chordNote.BendValues.Clear();
        chordNote.SlideTo = null;
        chordNote.UnpitchedSlide = false;
        chordNote.Tremolo = false;
        chordNote.Vibrato = false;
        chordNote.VibratoStrength = 0;
        chordNote.Hopo = HopoType.None;
    }

    private static void StripTechniques(GuitarNote note)
    {
        note.BendValues.Clear();
        note.SlideTo = null;
        note.UnpitchedSlide = false;
        note.Tremolo = false;
        note.Vibrato = false;
        note.VibratoStrength = 0;
        note.Hopo = HopoType.None;
        // Mute (palm/full) is deliberately retained — it changes the sound,
        // not the difficulty. Harmonics are retained (removing one would
        // change the sounding pitch).
    }

    /// <summary>True when the next source sound on <paramref name="stringIndex"/>
    /// strictly after <paramref name="position"/> survived thinning (LinkNext
    /// targets the following note on the same string).</summary>
    private static bool IsLinkTargetKept(ChartLevel source, HashSet<object> kept, int stringIndex,
        FractionalPosition position)
    {
        object? target = null;
        var targetPosition = default(FractionalPosition);
        var found = false;

        foreach (var note in source.Notes)
        {
            if (note.StringIndex != stringIndex || note.Position <= position) continue;
            if (!found || note.Position < targetPosition)
            {
                target = note;
                targetPosition = note.Position;
                found = true;
            }
        }

        foreach (var chord in source.Chords)
        {
            if (chord.Position <= position) continue;
            if (chord.ChordNotes.All(n => n.StringIndex != stringIndex)) continue;
            if (!found || chord.Position < targetPosition)
            {
                target = chord;
                targetPosition = chord.Position;
                found = true;
            }
        }

        return found && kept.Contains(target!);
    }

    /// <summary>
    ///     Records the new ceiling on <see cref="Arrangement.PhraseInfos" /> — the
    ///     value <c>LiveNoteMatcher</c>, <see cref="LevelSquashService" /> and the
    ///     RS export all read back.
    ///     <para>
    ///         <b>BOTH phrase stores, via <see cref="PhraseMarkers.All" />.</b>
    ///         Keyed on <c>EventPoint.Phrase</c> alone this found ZERO phrases in
    ///         every imported chart — the RS-XML and psarc readers fill
    ///         <see cref="Arrangement.Phrases" /> + <c>PhraseIterations</c>
    ///         instead — so an imported or phrase-generated chart came out of
    ///         level generation with no ceiling recorded for a single one of its
    ///         real phrases.
    ///     </para>
    ///     <para>
    ///         <b>And the structural phrases are not raised.</b> COUNT / END /
    ///         noguitar carry no sounds and <c>PhraseSectionGenerator</c> writes
    ///         them at difficulty 0 on purpose; setting every value in the
    ///         dictionary raised them back to N-1.
    ///     </para>
    /// </summary>
    private static void UpdatePhraseInfos(Arrangement arrangement, int maxDifficulty)
    {
        foreach (var marker in PhraseMarkers.All(arrangement))
        {
            if (string.IsNullOrWhiteSpace(marker.Name)) continue;
            var ceiling = PhraseMarkers.IsStructural(marker.Name) ? 0 : maxDifficulty;
            if (arrangement.PhraseInfos.TryGetValue(marker.Name, out var info)) info.MaxDifficulty = ceiling;
            else arrangement.PhraseInfos[marker.Name] = new PhraseInfo(ceiling, false);
        }
    }

    private static void SortByPosition<T>(IList<T> items, Func<T, FractionalPosition> positionSelector)
    {
        if (items.Count <= 1) return;
        var sorted = items.OrderBy(positionSelector).ToList();
        for (var i = 0; i < sorted.Count; i++)
            items[i] = sorted[i];
    }
}
