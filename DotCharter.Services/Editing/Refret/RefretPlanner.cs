namespace DotCharter.Services.Editing.Refret;

/// <summary>
///     The pure planning core of the refret engine: candidate generation +
///     dynamic programming over one <see cref="ChartLevel" />.
///
///     Model: the level's sounds become a time-ordered list of DP UNITS. A unit
///     is a single note, a string-coupled note group (link-next chains and
///     hammer-on/pull-off runs move together — one string decision for the
///     whole group), or a chord group (consecutive same-template chords share
///     one shape decision, which also keeps hand shapes coherent). Every unit
///     carries a deterministic candidate list whose index 0 is ALWAYS the
///     original assignment, so the all-original path exists, the DP result is
///     never worse than the input on the cost model, and ties resolve toward
///     the authored chart.
///
///     The DP state is (candidate, hand position): a hand position p covers the
///     frets [p, p+HandSpan−1], so a cross-string phrase inside one position is
///     genuinely free while a single-string run of the same pitches pays real
///     position shifts. All-open sounds have no position (the hand is free to
///     travel during them). Beam: chord shape candidates are capped at
///     <see cref="RefretOptions.ChordCandidateBeam" /> (original always kept),
///     giving ≤ 4·beam states per unit — a 3,000-note chart stays well under
///     the 2 s budget.
///
///     Hard pitch rule: a candidate (string, fret) is only generated when its
///     sounding MIDI pitch — open-string pitch from tuning offsets, plus capo
///     semantics (fret 0 sounds at the capo) — is bit-identical to the
///     original's. Sounds whose pitch is not fret-linear or whose technique
///     pins them (harmonics, full mutes, unpitched slides, bends, taps, sounds
///     inside mixed hand shapes, malformed data) are FIXED: they keep their
///     single original candidate and act as hard DP anchors.
///
///     After the DP, a collision sweep reverts any CHANGED unit that would make
///     two sounds occupy the same string at the same time (pre-existing
///     collisions in the authored chart are grandfathered), and a final guard
///     reverts everything if the surviving plan somehow costs more than the
///     original — cost(refretted) ≤ cost(original) is an invariant.
/// </summary>
internal sealed class RefretPlanner
{
    private const int LinkNextToleranceMs = 10;
    private const int LegatoMaxGapMs = 500;

    /// <summary>Frets the fret hand covers without shifting (index → pinky).</summary>
    private const int HandSpan = 4;

    private const double Epsilon = 1e-9;

    /// <summary>A plan must beat the original by at least this much to be applied.</summary>
    private const double MinimumGain = 1e-6;

    private readonly RefretOptions _opt;
    private readonly int _stringCount;
    private readonly int _capo;
    private readonly int[] _openMidi;

    private RefretPlanner(Arrangement arrangement, RefretOptions opt)
    {
        _opt = opt;
        _stringCount = Math.Clamp(arrangement.StringCount, 1, StandardOpenStrings.MaxStringCount);
        _capo = Math.Max(0, arrangement.Capo);

        // GetOffsets…, not TryGet…: refret arithmetic needs SOME open-string
        // vector for every string, and standard is the same default the per-string
        // `: 0` below already applies. Deliberate, not accidental — see the
        // remarks on GetOffsetsForTuningType.
        var fallbackOffsets = arrangement.TuningPitches.Count >= _stringCount
            ? null
            : TuningUtilities.GetOffsetsForTuningType(arrangement.TuningType, _stringCount);

        _openMidi = new int[_stringCount];
        for (var s = 0; s < _stringCount; s++)
        {
            var step = s < arrangement.TuningPitches.Count
                ? arrangement.TuningPitches[s].Step
                : fallbackOffsets != null && s < fallbackOffsets.Length
                    ? fallbackOffsets[s]
                    : 0;
            // StandardOpenStrings: a 7-string's string 0 is B1, not E2.
            _openMidi[s] = StandardOpenStrings.GuitarLowEMidi
                           + StandardOpenStrings.SemitonesAboveLowE(_stringCount, s) + step;
        }
    }

    public static RefretPlan Plan(
        Arrangement arrangement,
        ChartLevel level,
        IList<BeatMarker> beats,
        FractionalPosition? rangeStart,
        FractionalPosition? rangeEnd,
        RefretOptions opt)
    {
        var planner = new RefretPlanner(arrangement, opt);
        return planner.PlanLevel(arrangement, level, beats, rangeStart, rangeEnd);
    }

    // ─── Pitch math ─────────────────────────────────────────────────────────

    /// <summary>Sounding MIDI of (string, fret) under tuning + capo; null when unplayable/malformed.</summary>
    public int? SoundingMidi(int stringIndex, int fret)
    {
        if (stringIndex < 0 || stringIndex >= _stringCount) return null;
        if (fret < 0 || fret > _opt.MaxFret) return null;
        if (_capo > 0 && fret != 0 && fret <= _capo) return null; // behind the capo
        var sounding = fret == 0 && _capo > 0 ? _capo : fret;
        return _openMidi[stringIndex] + sounding;
    }

    /// <summary>Fret that sounds <paramref name="midi" /> on <paramref name="stringIndex" />, or null.</summary>
    public int? FretFor(int stringIndex, int midi)
    {
        if (stringIndex < 0 || stringIndex >= _stringCount) return null;
        var rel = midi - _openMidi[stringIndex];
        if (_capo > 0)
        {
            if (rel == _capo) return 0;
            if (rel > _capo && rel <= _opt.MaxFret) return rel;
            return null;
        }

        if (rel >= 0 && rel <= _opt.MaxFret) return rel;
        return null;
    }

    /// <summary>Slide-target frets are always fretted (≥1) and sound at their fret number.</summary>
    private bool IsValidSlideTarget(int fret) => fret >= 1 && fret > _capo && fret <= _opt.MaxFret;

    /// <summary>
    ///     Templates are conventionally stored with Id == index; fall back to an
    ///     Id scan for imported charts where the invariant drifted.
    /// </summary>
    internal static ChordTemplate? ResolveTemplate(Arrangement arrangement, int templateId)
    {
        if (templateId < 0) return null;
        var list = arrangement.ChordTemplates;
        if (templateId < list.Count && list[templateId].Id == templateId) return list[templateId];
        return list.FirstOrDefault(t => t.Id == templateId);
    }

    // ─── Planning ───────────────────────────────────────────────────────────

    private RefretPlan PlanLevel(
        Arrangement arrangement,
        ChartLevel level,
        IList<BeatMarker> beats,
        FractionalPosition? rangeStart,
        FractionalPosition? rangeEnd)
    {
        var sounds = CollectSounds(arrangement, level, beats, rangeStart, rangeEnd);
        var units = BuildUnits(sounds);
        foreach (var unit in units) BuildCandidates(unit);

        var zeros = new int[units.Count];
        var original = SolveDp(units, zeros);

        var full = SolveDp(units, null);
        for (var i = 0; i < units.Count; i++) units[i].ChosenIndex = full.ChosenCandidates[i];

        var collisionSweepVisits = RevertCollisions(units);

        var chosen = units.Select(u => u.ChosenIndex).ToArray();
        var final = SolveDp(units, chosen);

        // Final guard: apply only STRICT improvements. A zero-gain reshuffle
        // (or a collision sweep that ate the win) reverts to the original —
        // this is what makes the whole refret idempotent and monotone.
        if (final.Cost > original.Cost - MinimumGain)
        {
            foreach (var unit in units) unit.ChosenIndex = 0;
            final = original;
        }

        return new RefretPlan
        {
            Units = units,
            CostBefore = original.Cost,
            CostAfter = final.Cost,
            TravelBefore = original.Travel,
            TravelAfter = final.Travel,
            CollisionSweepVisits = collisionSweepVisits
        };
    }

    // ─── Sound collection + fixedness ───────────────────────────────────────

    private List<RefretSound> CollectSounds(
        Arrangement arrangement,
        ChartLevel level,
        IList<BeatMarker> beats,
        FractionalPosition? rangeStart,
        FractionalPosition? rangeEnd)
    {
        var sounds = new List<RefretSound>(level.Notes.Count + level.Chords.Count);

        foreach (var note in level.Notes)
        {
            var timeMs = note.Position.GetPositionMs(beats);
            var sound = new RefretSound
            {
                Note = note,
                Position = note.Position,
                TimeMs = timeMs,
                EndMs = Math.Max(note.EndPosition.GetPositionMs(beats), timeMs + 1),
                IsFixed = IsNotePinned(note) ||
                          InRangeCheckFails(note.Position, rangeStart, rangeEnd)
            };
            sounds.Add(sound);
        }

        foreach (var chord in level.Chords)
        {
            if (chord.ChordNotes.Count == 0) continue;
            var template = ResolveTemplate(arrangement, chord.TemplateId);
            var timeMs = chord.Position.GetPositionMs(beats);
            var sound = new RefretSound
            {
                Chord = chord,
                Template = template,
                Position = chord.Position,
                TimeMs = timeMs,
                EndMs = Math.Max(chord.EndPosition.GetPositionMs(beats), timeMs + 1),
                IsFixed = IsChordPinned(chord, template) ||
                          InRangeCheckFails(chord.Position, rangeStart, rangeEnd)
            };
            sounds.Add(sound);
        }

        // Deterministic order: time, chords before notes at the same instant,
        // then lowest string.
        sounds.Sort((a, b) =>
        {
            var c = a.TimeMs.CompareTo(b.TimeMs);
            if (c != 0) return c;
            c = (a.Chord != null ? 0 : 1).CompareTo(b.Chord != null ? 0 : 1);
            if (c != 0) return c;
            return a.MinString.CompareTo(b.MinString);
        });
        for (var i = 0; i < sounds.Count; i++) sounds[i].Index = i;

        ApplyHandShapeFixing(level, sounds);
        BuildStringCouplings(sounds);
        return sounds;
    }

    private static bool InRangeCheckFails(
        FractionalPosition position,
        FractionalPosition? rangeStart,
        FractionalPosition? rangeEnd)
    {
        if (rangeStart.HasValue && position < rangeStart.Value) return true;
        if (rangeEnd.HasValue && position > rangeEnd.Value) return true;
        return false;
    }

    private bool IsNotePinned(GuitarNote note)
    {
        if (note.Harmonic != HarmonicType.None) return true; // natural harmonics aren't fret-linear; pinch tone is positional
        if (note.Mute == MuteType.Full) return true; // no pitch — keep position
        if (note.UnpitchedSlide) return true;
        if (note.BendValues.Count > 0) return true; // bends keep their string, which pins the fret too
        if (note.Hopo == HopoType.Tap) return true; // tap phrases are authored deliberately
        if (SoundingMidi(note.StringIndex, note.Fret) == null) return true; // malformed
        if (note.SlideTo is { } st && (st <= 0 || !IsValidSlideTarget(st) || note.Fret == 0)) return true;
        return false;
    }

    private bool IsChordPinned(Chord chord, ChordTemplate? template)
    {
        if (chord.TemplateId >= 0 && template == null) return true; // dangling template
        // An authored grip is never replaced — and so a bar holding one over these
        // chords is never repointed: the "clean" bar below only moves chords of ITS template.
        if (template is { IsPinned: true }) return true;

        foreach (var cn in chord.ChordNotes)
        {
            if (cn.Harmonic != HarmonicType.None) return true;
            if (cn.Mute == MuteType.Full) return true; // mixed x-shapes are deliberate
            if (cn.UnpitchedSlide) return true;
            if (cn.BendValues.Count > 0) return true;
            if (cn.Hopo != HopoType.None) return true; // chord-level legato — keep authored
            if (cn.LinkNext) return true; // chord link chains stay put
            if (SoundingMidi(cn.StringIndex, cn.Fret) == null) return true;
            if (cn.SlideTo is { } st && (st <= 0 || !IsValidSlideTarget(st) || cn.Fret == 0)) return true;
            if (template != null && template.GetFret(cn.StringIndex) != cn.Fret) return true; // template drift
        }

        // Duplicate strings inside one chord are malformed — keep as-is.
        return chord.ChordNotes.Select(cn => cn.StringIndex).Distinct().Count() != chord.ChordNotes.Count;
    }

    /// <summary>
    ///     Hand shapes must stay coherent with what is played inside them. A
    ///     shape whose span contains ONLY chords of one template can move as a
    ///     block (the chord-group unit repoints the shape's template); anything
    ///     mixed — single notes inside the shape (arpeggio picking), multiple
    ///     templates, untemplated chords — is FIXED so the shape never goes stale.
    ///     <para>
    ///     Membership is the one shared rule <see cref="HandShape.Covers" /> (End is
    ///     EXCLUSIVE). An open-coded <c>&lt;=</c> would count the chord that STARTS
    ///     exactly where the shape ends — i.e. the NEXT shape's opening chord —
    ///     as inside it and pin a whole run that could legitimately move.
    ///     </para>
    /// </summary>
    private static void ApplyHandShapeFixing(ChartLevel level, List<RefretSound> sounds)
    {
        foreach (var shape in level.HandShapes)
        {
            if (shape.EndPosition <= shape.Position) continue;
            var inside = sounds
                .Where(s => shape.Covers(s.Position))
                .ToList();
            if (inside.Count == 0) continue;

            var clean = inside.All(s =>
                s.Chord != null && s.Template != null && s.Chord.TemplateId == shape.TemplateId);
            if (clean) continue;

            foreach (var s in inside) s.IsFixed = true;
        }
    }

    /// <summary>
    ///     Couples single notes that must share one string: link-next chains
    ///     (a linked pair stays on one string) and hammer-on/pull-off runs
    ///     (legato physically requires the source note on the same string).
    ///     Implemented as union-find; a group with any fixed member is fixed.
    /// </summary>
    private static void BuildStringCouplings(List<RefretSound> sounds)
    {
        var parent = new int[sounds.Count];
        for (var i = 0; i < parent.Length; i++) parent[i] = i;

        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }

            return x;
        }

        void Union(int a, int b)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (ra != rb) parent[rb] = ra;
        }

        for (var i = 0; i < sounds.Count; i++)
        {
            var sound = sounds[i];
            var note = sound.Note;
            if (note == null) continue;

            // Link-next: the target is the next note on the same string whose
            // start sits at this note's sustain end (validation tolerance).
            if (note.LinkNext)
            {
                for (var j = i + 1; j < sounds.Count; j++)
                {
                    var cand = sounds[j];
                    if (cand.TimeMs > sound.EndMs + LinkNextToleranceMs) break;
                    if (Math.Abs(cand.TimeMs - sound.EndMs) > LinkNextToleranceMs) continue;
                    if (cand.Note != null && cand.Note.StringIndex == note.StringIndex)
                    {
                        Union(i, cand.Index);
                        break;
                    }

                    if (cand.Chord != null && cand.Chord.ChordNotes.Any(cn => cn.StringIndex == note.StringIndex))
                    {
                        // Linked into a chord (invalid per validation, but present
                        // in the wild): keep both exactly as authored.
                        sound.IsFixed = true;
                        cand.IsFixed = true;
                        break;
                    }
                }
            }

            // Legato: hammer-on/pull-off chains from the last sound on the same
            // string. A chord source (or none in reach) pins the note instead.
            if (note.Hopo is HopoType.HammerOn or HopoType.PullOff)
            {
                RefretSound? source = null;
                for (var j = i - 1; j >= 0; j--)
                {
                    var cand = sounds[j];
                    if (cand.Note != null && cand.Note.StringIndex == note.StringIndex)
                    {
                        if (sound.TimeMs - cand.EndMs <= LegatoMaxGapMs) source = cand;
                        break;
                    }

                    if (cand.Chord != null && cand.Chord.ChordNotes.Any(cn => cn.StringIndex == note.StringIndex))
                    {
                        if (sound.TimeMs - cand.EndMs <= LegatoMaxGapMs)
                        {
                            sound.IsFixed = true;
                            cand.IsFixed = true;
                        }

                        break;
                    }
                }

                if (source != null) Union(i, source.Index);
                else sound.IsFixed = true; // no usable note source (chord, too far, none) — keep authored
            }
        }

        // Materialize groups + propagate fixedness.
        var groups = new Dictionary<int, List<RefretSound>>();
        for (var i = 0; i < sounds.Count; i++)
        {
            if (sounds[i].Note == null) continue;
            var root = Find(i);
            if (!groups.TryGetValue(root, out var list))
            {
                list = [];
                groups[root] = list;
            }

            list.Add(sounds[i]);
        }

        foreach (var group in groups.Values)
        {
            if (group.Count <= 1) continue;
            var anyFixed = group.Any(s => s.IsFixed);

            // Coupled members must share one string; malformed data doesn't.
            if (group.Select(s => s.Note!.StringIndex).Distinct().Count() > 1) anyFixed = true;

            foreach (var s in group)
            {
                s.CouplingGroup = group;
                if (anyFixed) s.IsFixed = true;
            }
        }
    }

    // ─── Unit building ──────────────────────────────────────────────────────

    private List<RefretUnit> BuildUnits(List<RefretSound> sounds)
    {
        var units = new List<RefretUnit>();
        var consumed = new bool[sounds.Count];

        for (var i = 0; i < sounds.Count; i++)
        {
            if (consumed[i]) continue;
            var sound = sounds[i];

            if (sound.Note != null)
            {
                var members = sound.CouplingGroup ?? [sound];
                foreach (var m in members) consumed[m.Index] = true;
                units.Add(new RefretUnit
                {
                    NoteMembers = members.OrderBy(m => m.TimeMs).ThenBy(m => m.Index).ToList(),
                    IsFixed = members.Any(m => m.IsFixed)
                });
                continue;
            }

            // Chord: merge the run of consecutive movable chords sharing this
            // template into one shape decision (repeated strums move together;
            // hand shapes stay coherent). Fixed/untemplated chords are singletons.
            var chordMembers = new List<RefretSound> { sound };
            consumed[i] = true;
            if (!sound.IsFixed && sound.Template != null)
            {
                for (var j = i + 1; j < sounds.Count; j++)
                {
                    if (consumed[j]) continue;
                    var next = sounds[j];
                    if (next.Note != null) break; // an interleaved note ends the run
                    if (next.IsFixed || next.Template == null ||
                        next.Chord!.TemplateId != sound.Chord!.TemplateId)
                    {
                        break;
                    }

                    chordMembers.Add(next);
                    consumed[j] = true;
                }
            }

            units.Add(new RefretUnit
            {
                ChordMembers = chordMembers,
                IsFixed = sound.IsFixed
            });
        }

        units.Sort((a, b) =>
        {
            var c = a.EntryTimeMs.CompareTo(b.EntryTimeMs);
            return c != 0 ? c : a.FirstIndex.CompareTo(b.FirstIndex);
        });
        return units;
    }

    // ─── Candidate generation ───────────────────────────────────────────────

    private void BuildCandidates(RefretUnit unit)
    {
        if (unit.NoteMembers != null) BuildNoteGroupCandidates(unit);
        else BuildChordGroupCandidates(unit);
    }

    private void BuildNoteGroupCandidates(RefretUnit unit)
    {
        var members = unit.NoteMembers!;
        var originalString = members[0].Note!.StringIndex;

        var original = BuildNoteCandidate(unit, originalString, true);
        original ??= BuildVerbatimOriginal(unit);
        unit.Candidates.Add(original);

        if (unit.IsFixed) return;

        for (var s = 0; s < _stringCount; s++)
        {
            if (s == originalString) continue;
            var candidate = BuildNoteCandidate(unit, s, false);
            if (candidate != null) unit.Candidates.Add(candidate);
        }
    }

    /// <summary>Grandfathered original for data the candidate rules reject.</summary>
    private RefretCandidate BuildVerbatimOriginal(RefretUnit unit)
    {
        var members = unit.NoteMembers!;
        var candidate = new RefretCandidate
        {
            IsOriginal = true,
            StringIndex = members[0].Note!.StringIndex,
            Frets = members.Select(m => m.Note!.Fret).ToArray(),
            SlideTos = members.Select(m => m.Note!.SlideTo).ToArray()
        };
        FinishNoteCandidate(unit, candidate);
        return candidate;
    }

    private RefretCandidate? BuildNoteCandidate(RefretUnit unit, int stringIndex, bool isOriginal)
    {
        var members = unit.NoteMembers!;
        var frets = new int[members.Count];
        var slides = new int?[members.Count];

        for (var i = 0; i < members.Count; i++)
        {
            var note = members[i].Note!;
            var pitch = SoundingMidi(note.StringIndex, note.Fret);
            if (pitch == null) return null;

            var fret = FretFor(stringIndex, pitch.Value);
            if (fret == null) return null;
            frets[i] = fret.Value;

            if (note.SlideTo is { } st && st > 0)
            {
                if (fret.Value == 0) return null; // can't slide from an open string
                var targetPitch = _openMidi[note.StringIndex] + st;
                var newTarget = targetPitch - _openMidi[stringIndex];
                if (!IsValidSlideTarget(newTarget)) return null;
                slides[i] = newTarget;
            }
        }

        var candidate = new RefretCandidate
        {
            IsOriginal = isOriginal,
            StringIndex = stringIndex,
            Frets = frets,
            SlideTos = slides
        };
        FinishNoteCandidate(unit, candidate);
        return candidate;
    }

    private void FinishNoteCandidate(RefretUnit unit, RefretCandidate candidate)
    {
        var members = unit.NoteMembers!;
        var frets = candidate.Frets!;
        var cost = 0.0;

        for (var i = 0; i < members.Count; i++)
        {
            var note = members[i].Note!;
            var f = frets[i];
            if (f == 0) cost -= _opt.OpenStringBonus;
            else cost += _opt.HighFretWeight * Math.Max(0, f - _opt.HighFretThreshold);

            if (candidate.StringIndex != note.StringIndex || f != note.Fret)
                cost += _opt.InertiaWeight;
        }

        candidate.IntrinsicCost = cost;
        candidate.EntryFret = frets.FirstOrDefault(f => f > 0);
        var lastFrettedIdx = Array.FindLastIndex(frets, f => f > 0);
        candidate.ExitFret = lastFrettedIdx >= 0
            ? candidate.SlideTos![lastFrettedIdx] ?? frets[lastFrettedIdx]
            : 0;
        candidate.EntryCenter = candidate.StringIndex;
        candidate.ExitCenter = candidate.StringIndex;
    }

    private void BuildChordGroupCandidates(RefretUnit unit)
    {
        var firstChord = unit.ChordMembers![0].Chord!;
        var template = unit.ChordMembers[0].Template;

        // Shape entries: from the template when present (the full authored
        // shape), else from the chord's own notes. Sorted by original string.
        var entries = (template != null
                ? template.Frets.Select(kv => (Str: kv.Key, Fret: kv.Value))
                : firstChord.ChordNotes.Select(cn => (Str: cn.StringIndex, cn.Fret)))
            .OrderBy(e => e.Str)
            .ToList();

        var original = new RefretCandidate
        {
            IsOriginal = true,
            ShapeMap = entries.ToDictionary(e => e.Str, e => (e.Str, e.Fret))
        };
        FinishChordCandidate(unit, entries, original);
        unit.Candidates.Add(original);

        if (unit.IsFixed || entries.Count == 0) return;

        var pitches = new int[entries.Count];
        for (var i = 0; i < entries.Count; i++)
        {
            var p = SoundingMidi(entries[i].Str, entries[i].Fret);
            if (p == null) return; // malformed shape — original only
            pitches[i] = p.Value;
        }

        var found = new List<RefretCandidate>();
        var assignment = new (int Str, int Fret)[entries.Count];
        EnumerateShapes(unit, entries, pitches, assignment, 0, 0, found);

        // Deterministic order, original-first, beam cut.
        found.Sort((a, b) =>
        {
            var c = a.IntrinsicCost.CompareTo(b.IntrinsicCost);
            if (c != 0) return c;
            foreach (var key in a.ShapeMap!.Keys.OrderBy(k => k))
            {
                c = a.ShapeMap[key].Str.CompareTo(b.ShapeMap![key].Str);
                if (c != 0) return c;
                c = a.ShapeMap[key].Fret.CompareTo(b.ShapeMap[key].Fret);
                if (c != 0) return c;
            }

            return 0;
        });

        foreach (var candidate in found)
        {
            if (unit.Candidates.Count >= _opt.ChordCandidateBeam) break;
            if (MapEquality.SameEntries(candidate.ShapeMap!, original.ShapeMap!)) continue;
            unit.Candidates.Add(candidate);
        }
    }


    private void EnumerateShapes(
        RefretUnit unit,
        List<(int Str, int Fret)> entries,
        int[] pitches,
        (int Str, int Fret)[] assignment,
        int index,
        int minString,
        List<RefretCandidate> found)
    {
        if (found.Count >= 256) return; // combinatorial safety valve — never near this in practice

        if (index == entries.Count)
        {
            var candidate = TryCompleteShape(unit, entries, assignment);
            if (candidate != null) found.Add(candidate);
            return;
        }

        for (var s = minString; s < _stringCount; s++)
        {
            var fret = FretFor(s, pitches[index]);
            if (fret == null) continue;

            // Prune: fretted span within the running assignment.
            var min = int.MaxValue;
            var max = int.MinValue;
            for (var i = 0; i < index; i++)
            {
                if (assignment[i].Fret > 0)
                {
                    min = Math.Min(min, assignment[i].Fret);
                    max = Math.Max(max, assignment[i].Fret);
                }
            }

            if (fret.Value > 0)
            {
                min = Math.Min(min, fret.Value);
                max = Math.Max(max, fret.Value);
            }

            if (min != int.MaxValue && max - min > _opt.MaxChordSpan) continue;

            assignment[index] = (s, fret.Value);
            EnumerateShapes(unit, entries, pitches, assignment, index + 1, s + 1, found);
        }
    }

    private RefretCandidate? TryCompleteShape(
        RefretUnit unit,
        List<(int Str, int Fret)> entries,
        (int Str, int Fret)[] assignment)
    {
        // Finger plausibility: ≤4 fretted notes are always fingerable; more
        // requires a barre at the lowest fret, which open strings (or unplayed
        // strings) inside the barred range would break.
        var fretted = assignment.Where(a => a.Fret > 0).ToList();
        if (fretted.Count > 4)
        {
            var minFret = fretted.Min(a => a.Fret);
            var barred = assignment.Where(a => a.Fret == minFret).Select(a => a.Str).ToList();
            if (barred.Count < 2) return null;
            var above = assignment.Count(a => a.Fret > minFret);
            if (1 + above > 4) return null;
            for (var s = barred.Min(); s <= barred.Max(); s++)
            {
                var at = assignment.FirstOrDefault(a => a.Str == s, (Str: -1, Fret: -1));
                if (at.Str < 0 || at.Fret == 0) return null; // open/unplayed inside the barre
            }
        }

        var map = new Dictionary<int, (int Str, int Fret)>(entries.Count);
        for (var i = 0; i < entries.Count; i++) map[entries[i].Str] = assignment[i];

        // Every member chord's slides must survive the remap.
        foreach (var member in unit.ChordMembers!)
        {
            foreach (var cn in member.Chord!.ChordNotes)
            {
                if (!map.TryGetValue(cn.StringIndex, out var target)) return null; // chord note outside the shape
                if (cn.SlideTo is { } st && st > 0)
                {
                    if (target.Fret == 0) return null;
                    var newTarget = st + (target.Fret - cn.Fret);
                    if (!IsValidSlideTarget(newTarget)) return null;
                }
            }
        }

        var candidate = new RefretCandidate { ShapeMap = map };
        FinishChordCandidate(unit, entries, candidate);
        return candidate;
    }

    private void FinishChordCandidate(
        RefretUnit unit,
        List<(int Str, int Fret)> entries,
        RefretCandidate candidate)
    {
        var map = candidate.ShapeMap!;
        var cost = 0.0;
        var minFretted = int.MaxValue;
        var maxFretted = int.MinValue;
        var stringSum = 0.0;

        foreach (var entry in entries)
        {
            var (s, f) = map[entry.Str];
            stringSum += s;
            if (f == 0)
            {
                cost -= _opt.OpenStringBonus;
            }
            else
            {
                cost += _opt.HighFretWeight * Math.Max(0, f - _opt.HighFretThreshold);
                minFretted = Math.Min(minFretted, f);
                maxFretted = Math.Max(maxFretted, f);
            }

            if (s != entry.Str || f != entry.Fret) cost += _opt.InertiaWeight;
        }

        if (minFretted != int.MaxValue)
            cost += _opt.ChordSpanWeight * Math.Max(0, maxFretted - minFretted - 2);

        // Per STRUM, not per group: every chord in the group re-sounds the
        // shape, and this keeps the cost total independent of how consecutive
        // same-template chords happen to be grouped into units.
        cost *= unit.ChordMembers!.Count;

        candidate.IntrinsicCost = cost;
        candidate.EntryFret = minFretted == int.MaxValue ? 0 : minFretted;
        candidate.ExitFret = candidate.EntryFret;
        candidate.MaxFretted = maxFretted == int.MinValue ? 0 : maxFretted;
        candidate.EntryCenter = entries.Count > 0 ? stringSum / entries.Count : 0;
        candidate.ExitCenter = candidate.EntryCenter;
    }

    // ─── Dynamic program over (candidate, hand position) states ─────────────

    private double TimeFactor(double dtMs) => 1.0 + _opt.FastMovementBoost * (_opt.ReferenceIntervalMs / Math.Max(dtMs, _opt.MinIntervalMs));

    /// <summary>
    ///     Expands a unit's candidates into DP states. A state's hand position
    ///     covers [pos, pos+HandSpan−1]; chord shapes only get positions that
    ///     cover the whole shape. All-open candidates get position 0 — they are
    ///     transparent to movement (the hand travels freely during them).
    /// </summary>
    private List<DpState> BuildStates(RefretUnit unit, int? forcedCandidate)
    {
        var states = new List<DpState>();
        for (var k = 0; k < unit.Candidates.Count; k++)
        {
            if (forcedCandidate.HasValue && k != forcedCandidate.Value) continue;
            var candidate = unit.Candidates[k];

            if (candidate.EntryFret <= 0)
            {
                states.Add(new DpState(k, 0, 0, candidate.IntrinsicCost));
                continue;
            }

            // Position window covering the entry fret — for chords, the whole
            // shape (possibly empty for grandfathered over-span originals).
            var high = candidate.EntryFret;
            var low = Math.Max(1, (candidate.MaxFretted > 0 ? candidate.MaxFretted : candidate.EntryFret)
                                  - (HandSpan - 1));
            if (low > high) low = high; // grandfathered over-span shape

            // Internal shift: a note run whose exit fret leaves the entry
            // window drags the hand along the string (time-weighted over the
            // run's average inter-onset interval).
            var memberCount = (unit.NoteMembers ?? unit.ChordMembers)!.Count;
            var internalDt = memberCount > 1
                ? (unit.ExitTimeMs - unit.EntryTimeMs) / (memberCount - 1)
                : _opt.ReferenceIntervalMs;

            for (var pos = high; pos >= low; pos--)
            {
                var exitPos = pos;
                var shiftCost = 0.0;
                if (candidate.ExitFret > 0)
                {
                    exitPos = Math.Clamp(pos, Math.Max(1, candidate.ExitFret - (HandSpan - 1)),
                        candidate.ExitFret);
                    if (exitPos != pos)
                        shiftCost = _opt.MovementWeight * Math.Abs(exitPos - pos) * TimeFactor(internalDt);
                }

                states.Add(new DpState(k, pos, exitPos, candidate.IntrinsicCost + shiftCost));
            }
        }

        return states;
    }

    private DpOutcome SolveDp(List<RefretUnit> units, int[]? forcedCandidates)
    {
        if (units.Count == 0) return new DpOutcome(0, 0, []);

        var stateLists = new List<DpState>[units.Count];
        for (var i = 0; i < units.Count; i++)
            stateLists[i] = BuildStates(units[i], forcedCandidates?[i]);

        var prevCosts = stateLists[0].Select(s => s.Cost).ToArray();
        var parents = new int[units.Count][];

        for (var i = 1; i < units.Count; i++)
        {
            var prevStates = stateLists[i - 1];
            var currentStates = stateLists[i];
            var prevUnit = units[i - 1];
            var currentUnit = units[i];
            var dt = Math.Max(0, currentUnit.EntryTimeMs - prevUnit.ExitTimeMs);
            var moveFactor = _opt.MovementWeight * TimeFactor(dt);

            var costs = new double[currentStates.Count];
            var parent = new int[currentStates.Count];

            for (var k = 0; k < currentStates.Count; k++)
            {
                var to = currentStates[k];
                var toCandidate = currentUnit.Candidates[to.Candidate];
                var best = double.MaxValue;
                var bestJ = 0;

                for (var j = 0; j < prevStates.Count; j++)
                {
                    var from = prevStates[j];
                    var fromCandidate = prevUnit.Candidates[from.Candidate];

                    var transition = _opt.StringCrossWeight *
                                     Math.Abs(toCandidate.EntryCenter - fromCandidate.ExitCenter);
                    if (from.ExitPos > 0 && to.EntryPos > 0)
                        transition += moveFactor * Math.Abs(to.EntryPos - from.ExitPos);

                    var total = prevCosts[j] + transition;
                    if (total < best - Epsilon)
                    {
                        best = total;
                        bestJ = j;
                    }
                }

                costs[k] = best + to.Cost;
                parent[k] = bestJ;
            }

            parents[i] = parent;
            prevCosts = costs;
        }

        // Cheapest terminal; ties → lowest state index (original-leaning).
        var bestK = 0;
        for (var k = 1; k < prevCosts.Length; k++)
        {
            if (prevCosts[k] < prevCosts[bestK] - Epsilon)
                bestK = k;
        }

        var totalCost = prevCosts[bestK];

        var chosenStates = new int[units.Count];
        for (var i = units.Count - 1; i >= 0; i--)
        {
            chosenStates[i] = bestK;
            if (i > 0) bestK = parents[i][bestK];
        }

        // Raw hand-position travel along the chosen path (open sounds carry).
        var travel = 0.0;
        var lastPos = 0;
        var chosenCandidates = new int[units.Count];
        for (var i = 0; i < units.Count; i++)
        {
            var state = stateLists[i][chosenStates[i]];
            chosenCandidates[i] = state.Candidate;
            if (state.EntryPos > 0)
            {
                if (lastPos > 0) travel += Math.Abs(state.EntryPos - lastPos);
                travel += Math.Abs(state.ExitPos - state.EntryPos);
                lastPos = state.ExitPos;
            }
        }

        return new DpOutcome(totalCost, travel, chosenCandidates);
    }

    // ─── Collision sweep ────────────────────────────────────────────────────

    /// <summary>
    ///     No two sounds may occupy the same string at overlapping times. The DP
    ///     optimizes a single melodic line and cannot see cross-voice sustains,
    ///     so violations are resolved by reverting the CHANGED participant(s) to
    ///     their original assignment; authored collisions are grandfathered.
    /// </summary>
    /// <summary>
    ///     Reverts any CHANGED unit whose new string would collide with another
    ///     sound, and returns the number of INNER-LOOP PAIR VISITS it made —
    ///     the sweep's cost, in a form a test can assert on.
    ///     <para>
    ///         Test seam, and a deliberate one: the wall-clock gate that guards
    ///         this loop's complexity (<c>RefretPerfGateTests</c>) is env-gated
    ///         and runs on no machine, and the repo's own rule is to assert
    ///         STRUCTURE and log the timing. A pair-visit count IS the structure:
    ///         the old inner loop only broke on a string CHANGE, so on a chart
    ///         where most sounds live on two or three strings it rescanned the
    ///         whole same-string block per entry — quadratic per pass. The
    ///         counter is a plain local increment on a loop that already runs,
    ///         so it costs nothing measurable.
    ///     </para>
    /// </summary>
    private long RevertCollisions(List<RefretUnit> units)
    {
        var pairVisits = 0L;

        for (var pass = 0; pass < units.Count; pass++)
        {
            var occupancy = new List<(int Str, double Start, double End, RefretUnit Unit)>();
            foreach (var unit in units)
            {
                var candidate = unit.Candidates[unit.ChosenIndex];
                if (unit.NoteMembers != null)
                {
                    foreach (var member in unit.NoteMembers)
                        occupancy.Add((candidate.StringIndex, member.TimeMs, member.EndMs, unit));
                }
                else
                {
                    foreach (var member in unit.ChordMembers!)
                    {
                        foreach (var cn in member.Chord!.ChordNotes)
                        {
                            var str = candidate.ShapeMap!.TryGetValue(cn.StringIndex, out var t)
                                ? t.Str
                                : cn.StringIndex;
                            occupancy.Add((str, member.TimeMs, member.EndMs, unit));
                        }
                    }
                }
            }

            occupancy.Sort((a, b) =>
            {
                var c = a.Str.CompareTo(b.Str);
                if (c != 0) return c;
                c = a.Start.CompareTo(b.Start);
                return c != 0 ? c : a.End.CompareTo(b.End);
            });

            var reverted = false;

            // Sweep with an ACTIVE set instead of rescanning the whole
            // same-string block for every entry. The old inner loop only broke
            // when the string CHANGED, so on a chart where most sounds live on
            // two or three strings it was quadratic per pass — and the pass loop
            // runs up to units.Count times, with RefretService re-planning up to
            // MaxPasses times on top. Because occupancy is sorted by (string,
            // start), an interval whose End has already been passed by the
            // current Start can never reach any LATER entry either, so dropping
            // it is permanent: the sweep costs O(n) plus one visit per genuinely
            // overlapping pair, which is exactly the work that has to happen.
            // The visited pair set is identical to the old loop's, so the revert
            // outcome is unchanged.
            var active = new List<int>();
            for (var i = 0; i < occupancy.Count; i++)
            {
                var b = occupancy[i];
                if (i > 0 && occupancy[i - 1].Str != b.Str) active.Clear();

                var keep = 0;
                for (var k = 0; k < active.Count; k++)
                {
                    pairVisits++;
                    var a = occupancy[active[k]];
                    if (a.End - Epsilon <= b.Start) continue; // touching is fine (link chains)
                    active[keep++] = active[k];

                    if (a.Unit == b.Unit) continue;

                    if (a.Unit.ChosenIndex != 0)
                    {
                        a.Unit.ChosenIndex = 0;
                        reverted = true;
                    }

                    if (b.Unit.ChosenIndex != 0)
                    {
                        b.Unit.ChosenIndex = 0;
                        reverted = true;
                    }
                }

                active.RemoveRange(keep, active.Count - keep);
                active.Add(i);
            }

            if (!reverted) return pairVisits;
        }

        return pairVisits;
    }

    private readonly record struct DpState(int Candidate, int EntryPos, int ExitPos, double Cost);

    private sealed record DpOutcome(double Cost, double Travel, int[] ChosenCandidates);
}

// ─── Plan model ─────────────────────────────────────────────────────────────

internal sealed class RefretPlan
{
    public required List<RefretUnit> Units { get; init; }
    public double CostBefore { get; init; }
    public double CostAfter { get; init; }
    public double TravelBefore { get; init; }
    public double TravelAfter { get; init; }

    /// <summary>Inner-loop pair visits made by the collision sweep. Diagnostic /
    /// test seam only — nothing in the planner reads it. See
    /// <c>RefretPerfGateTests.TheCollisionSweepStaysLinearInTheNumberOfSounds</c>,
    /// which asserts this stays O(sounds) instead of the quadratic shape the
    /// sweep once had.</summary>
    public long CollisionSweepVisits { get; init; }
}

/// <summary>One sound (note or chord) of the level being planned.</summary>
internal sealed class RefretSound
{
    public int Index { get; set; }
    public GuitarNote? Note { get; init; }
    public Chord? Chord { get; init; }
    public ChordTemplate? Template { get; init; }
    public FractionalPosition Position { get; init; }
    public double TimeMs { get; init; }
    public double EndMs { get; init; }
    public bool IsFixed { get; set; }
    public List<RefretSound>? CouplingGroup { get; set; }

    public int MinString => Note?.StringIndex
                            ?? (Chord!.ChordNotes.Count > 0 ? Chord.ChordNotes.Min(cn => cn.StringIndex) : 0);
}

/// <summary>One DP decision: a note, a coupled note group, or a chord group.</summary>
internal sealed class RefretUnit
{
    public List<RefretSound>? NoteMembers { get; init; }
    public List<RefretSound>? ChordMembers { get; init; }
    public bool IsFixed { get; init; }
    public List<RefretCandidate> Candidates { get; } = [];
    public int ChosenIndex { get; set; }

    public double EntryTimeMs => (NoteMembers ?? ChordMembers)![0].TimeMs;
    public double ExitTimeMs => (NoteMembers ?? ChordMembers)![^1].TimeMs;
    public int FirstIndex => (NoteMembers ?? ChordMembers)![0].Index;
}

/// <summary>One assignment option for a unit. Index 0 in a unit's list is always the original.</summary>
internal sealed class RefretCandidate
{
    public bool IsOriginal { get; init; }
    public double IntrinsicCost { get; set; }

    /// <summary>First fretted fret of the unit; 0 = all open (transparent to movement).</summary>
    public int EntryFret { get; set; }

    /// <summary>Last fretted fret of the unit (slide targets shift it).</summary>
    public int ExitFret { get; set; }

    /// <summary>Chord shapes: highest fretted fret (positions must cover the whole shape).</summary>
    public int MaxFretted { get; set; }

    public double EntryCenter { get; set; }
    public double ExitCenter { get; set; }

    // Note groups: the chosen string plus per-member frets/slide targets.
    public int StringIndex { get; init; } = -1;
    public int[]? Frets { get; init; }
    public int?[]? SlideTos { get; init; }

    // Chord groups: original shape string → (new string, new fret).
    public Dictionary<int, (int Str, int Fret)>? ShapeMap { get; init; }
}
