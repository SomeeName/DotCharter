using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.FretAnchors;
using DotCharter.Services.Editing.Refret;

namespace DotCharter.Tests.Services.Editing.Refret;

/// <summary>
///     The refret engine's contract, in order of importance: (1) pitch
///     preservation is bit-exact under any tuning/capo, (2) technique-pinned
///     sounds never move, (3) shapes stay physically playable, (4) the result
///     never costs more than the original on the model, (5) runs are
///     deterministic. Plus the behavioral cases: a low-string riff must not get
///     scattered across strings, and a deliberately awkward single-string scale
///     must measurably improve.
/// </summary>
public class RefretServiceTests
{
    // ─── Fixture helpers ─────────────────────────────────────────────────────

    private static RefretService CreateService() => new(new FretAnchorGenerationService(), new FingeringGenerationService());

    private static List<BeatMarker> BuildBeats(int count, double msPerBeat = 500)
    {
        var beats = new List<BeatMarker>();
        for (var i = 0; i < count; i++)
            beats.Add(new BeatMarker { Time = i * msPerBeat, Bpm = 60000.0 / msPerBeat, IsMeasure = i % 4 == 0 });
        return beats;
    }

    private static Arrangement BuildArrangement(int stringCount = 6, string tuningType = "E_STANDARD", int capo = 0)
    {
        var arr = new Arrangement { StringCount = stringCount, TuningType = tuningType, Capo = capo };
        var offsets = TuningUtilities.GetOffsetsForTuningType(tuningType, stringCount);
        for (var i = 0; i < stringCount; i++)
            arr.TuningPitches.Add(new TuningOffset(i < offsets.Length ? offsets[i] : 0));
        arr.Levels.Add(new ChartLevel());
        return arr;
    }

    private static int OpenMidi(Arrangement arr, int stringIndex) =>
        40 + StandardOpenStrings.SemitonesAboveLowE(arr.StringCount, stringIndex) + arr.TuningPitches[stringIndex].Step;

    private static int Sounding(Arrangement arr, int stringIndex, int fret) => OpenMidi(arr, stringIndex) + (fret == 0 && arr.Capo > 0 ? arr.Capo : fret);

    private static FractionalPosition Pos(int beat, int num = 0, int den = 4) => new(beat, new Fraction(num, den));

    private static GuitarNote Note(FractionalPosition pos, int str, int fret) => new() { Position = pos, StringIndex = str, Fret = fret };

    /// <summary>
    ///     The pitch identity of the whole arrangement: every note and chord
    ///     note as (time, sounding pitch, slide-target pitch), order-normalized
    ///     so string/fret reassignments cannot hide a pitch change.
    /// </summary>
    private static List<(double Pos, int Pitch, int Slide)> PitchSignature(Arrangement arr)
    {
        var sig = new List<(double, int, int)>();
        foreach (var level in arr.Levels)
        {
            foreach (var n in level.Notes)
            {
                sig.Add((n.Position.DoubleValue, Sounding(arr, n.StringIndex, n.Fret),
                    n.SlideTo is > 0 ? OpenMidi(arr, n.StringIndex) + n.SlideTo.Value : -1));
            }

            foreach (var c in level.Chords)
            {
                foreach (var cn in c.ChordNotes)
                {
                    sig.Add((c.Position.DoubleValue, Sounding(arr, cn.StringIndex, cn.Fret),
                    cn.SlideTo is > 0 ? OpenMidi(arr, cn.StringIndex) + cn.SlideTo.Value : -1));
                }
            }
        }

        return sig.OrderBy(x => x.Item1).ThenBy(x => x.Item2).ThenBy(x => x.Item3).ToList();
    }

    private static List<(double Pos, int Str, int Fret)> AssignmentSignature(Arrangement arr)
    {
        var sig = new List<(double, int, int)>();
        foreach (var level in arr.Levels)
        {
            foreach (var n in level.Notes)
                sig.Add((n.Position.DoubleValue, n.StringIndex, n.Fret));
            foreach (var c in level.Chords)
            {
                foreach (var cn in c.ChordNotes)
                    sig.Add((c.Position.DoubleValue, cn.StringIndex, cn.Fret));
            }
        }

        return sig.OrderBy(x => x.Item1).ThenBy(x => x.Item2).ThenBy(x => x.Item3).ToList();
    }

    private static void AssertNoStringCollisions(Arrangement arr, List<BeatMarker> beats)
    {
        foreach (var level in arr.Levels)
        {
            var intervals = new List<(int Str, double Start, double End)>();
            foreach (var n in level.Notes)
            {
                var start = n.Position.GetPositionMs(beats);
                intervals.Add((n.StringIndex, start, Math.Max(n.EndPosition.GetPositionMs(beats), start + 1)));
            }

            foreach (var c in level.Chords)
            {
                var start = c.Position.GetPositionMs(beats);
                foreach (var cn in c.ChordNotes)
                    intervals.Add((cn.StringIndex, start, Math.Max(c.EndPosition.GetPositionMs(beats), start + 1)));
            }

            foreach (var group in intervals.GroupBy(i => i.Str))
            {
                var ordered = group.OrderBy(i => i.Start).ToList();
                for (var i = 1; i < ordered.Count; i++)
                {
                    for (var j = 0; j < i; j++)
                    {
                        Assert.False(ordered[j].End - 1e-6 > ordered[i].Start,
                        $"String {group.Key}: sounds at {ordered[j].Start:0}ms and {ordered[i].Start:0}ms overlap.");
                    }
                }
            }
        }
    }

    /// <summary>Seeded chart with the whole technique zoo — the property-test input.</summary>
    private static Arrangement BuildRandomChart(int seed, out List<BeatMarker> beats)
    {
        var rng = new Random(seed);
        var tunings = new[] { "E_STANDARD", "E_DROP_D", "D_STANDARD", "E_FLAT_STANDARD" };
        var stringCount = rng.Next(3) == 0 ? 4 : 6;
        var capo = rng.Next(4) == 0 ? rng.Next(1, 4) : 0;
        var arr = BuildArrangement(stringCount, tunings[rng.Next(tunings.Length)], capo);
        beats = BuildBeats(500, 400);
        var level = arr.Levels[0];

        var beat = 1;
        var eighth = 0;
        for (var i = 0; i < 200 && beat < 480; i++)
        {
            eighth += rng.Next(1, 3);
            beat += eighth / 2;
            eighth %= 2;
            var pos = new FractionalPosition(beat, new Fraction(eighth, 2));

            if (rng.Next(5) == 0)
            {
                var size = rng.Next(2, Math.Min(4, stringCount) + 1);
                var startStr = rng.Next(0, stringCount - size + 1);
                var baseFret = capo + rng.Next(1, 9);
                var template = new ChordTemplate { Id = arr.ChordTemplates.Count, Name = $"C{i}" };
                for (var s = 0; s < size; s++)
                    template.Frets[startStr + s] = Math.Min(24, baseFret + rng.Next(0, 3));
                arr.ChordTemplates.Add(template);

                var chord = new Chord { Position = pos, TemplateId = template.Id };
                foreach (var kv in template.Frets.OrderBy(kv => kv.Key))
                {
                    chord.ChordNotes.Add(new ChordNote
                    {
                        StringIndex = kv.Key,
                        Fret = kv.Value,
                        Mute = rng.Next(6) == 0 ? MuteType.Palm : MuteType.None
                    });
                }

                level.Chords.Add(chord);
                continue;
            }

            var str = rng.Next(stringCount);
            var fret = rng.Next(4) == 0 && capo == 0 ? 0 : capo + rng.Next(1, 15);
            var note = Note(pos, str, fret);
            switch (rng.Next(12))
            {
                case 0:
                    note.Harmonic = HarmonicType.Normal;
                    break;
                case 1:
                    note.Mute = MuteType.Full;
                    break;
                case 2:
                    note.Mute = MuteType.Palm;
                    break;
                case 3:
                    if (fret >= Math.Max(1, capo + 1) && fret <= 20)
                    {
                        note.SlideTo = fret + rng.Next(1, 4);
                        note.Sustain = new FractionalPosition(0, new Fraction(1, 2));
                    }

                    break;
                case 4:
                    note.BendValues.Add(new BendValue(pos, 1.0));
                    break;
                case 5:
                    note.Hopo = HopoType.HammerOn;
                    break;
                case 6:
                    note.Hopo = HopoType.PullOff;
                    break;
                case 7:
                    note.Vibrato = true;
                    break;
                case 8:
                    note.UnpitchedSlide = true;
                    note.SlideTo = Math.Min(24, fret + 2);
                    break;
            }

            level.Notes.Add(note);
        }

        return arr;
    }

    // ─── (1) Pitch preservation — the invariant that can never break ─────────

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void Refret_preserves_every_pitch_on_random_charts(int seed)
    {
        var arr = BuildRandomChart(seed, out var beats);
        var before = PitchSignature(arr);

        CreateService().RefretArrangement(arr, beats);

        Assert.Equal(before, PitchSignature(arr));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Refret_never_increases_model_cost(int seed)
    {
        var arr = BuildRandomChart(seed, out var beats);

        var result = CreateService().RefretArrangement(arr, beats);

        Assert.True(result.CostAfter <= result.CostBefore + 1e-6,
            $"CostAfter {result.CostAfter} exceeds CostBefore {result.CostBefore}.");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void Refret_is_deterministic_and_idempotent(int seed)
    {
        var arrA = BuildRandomChart(seed, out var beatsA);
        var arrB = BuildRandomChart(seed, out var beatsB);

        var service = CreateService();
        service.RefretArrangement(arrA, beatsA);
        service.RefretArrangement(arrB, beatsB);
        Assert.Equal(AssignmentSignature(arrA), AssignmentSignature(arrB));

        // A second pass over an already-optimal chart changes nothing.
        var second = service.RefretArrangement(arrA, beatsA);
        Assert.False(second.HasChanges,
            $"Second refret still changed {second.SoundsChanged} sound(s) — not idempotent.");
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    public void Chord_shapes_stay_playable_after_refret(int seed)
    {
        var arr = BuildRandomChart(seed, out var beats);
        CreateService().RefretArrangement(arr, beats);

        foreach (var chord in arr.Levels[0].Chords)
        {
            var strings = chord.ChordNotes.Select(cn => cn.StringIndex).ToList();
            Assert.Equal(strings.Count, strings.Distinct().Count());
            Assert.All(strings, s => Assert.InRange(s, 0, arr.StringCount - 1));

            var fretted = chord.ChordNotes.Where(cn => cn.Fret > 0).Select(cn => cn.Fret).ToList();
            if (fretted.Count > 0)
            {
                Assert.True(fretted.Max() - fretted.Min() <= 4,
                    $"Chord at {chord.Position} spans {fretted.Min()}–{fretted.Max()}.");
                Assert.All(fretted, f => Assert.InRange(f, 1, 24));
            }
        }
    }

    // ─── (2) Technique pinning ───────────────────────────────────────────────

    [Fact]
    public void Technique_pinned_notes_keep_their_exact_position()
    {
        var arr = BuildArrangement();
        var beats = BuildBeats(40);
        var level = arr.Levels[0];

        var bend = Note(Pos(1), 1, 10);
        bend.BendValues.Add(new BendValue(Pos(1), 1.0));
        var harmonic = Note(Pos(2), 1, 12);
        harmonic.Harmonic = HarmonicType.Normal;
        var fullMute = Note(Pos(3), 1, 9);
        fullMute.Mute = MuteType.Full;
        var unpitched = Note(Pos(4), 1, 11);
        unpitched.UnpitchedSlide = true;
        unpitched.SlideTo = 13;
        var tap = Note(Pos(5), 1, 14);
        tap.Hopo = HopoType.Tap;

        level.Notes.Add(bend);
        level.Notes.Add(harmonic);
        level.Notes.Add(fullMute);
        level.Notes.Add(unpitched);
        level.Notes.Add(tap);
        // Strong pull toward the nut so movable notes WOULD relocate.
        for (var b = 6; b < 14; b++) level.Notes.Add(Note(Pos(b), 2, 2));

        CreateService().RefretArrangement(arr, beats);

        Assert.Equal((1, 10), (bend.StringIndex, bend.Fret));
        Assert.Equal((1, 12), (harmonic.StringIndex, harmonic.Fret));
        Assert.Equal((1, 9), (fullMute.StringIndex, fullMute.Fret));
        Assert.Equal((1, 11, 13), (unpitched.StringIndex, unpitched.Fret, unpitched.SlideTo!.Value));
        Assert.Equal((1, 14), (tap.StringIndex, tap.Fret));
    }

    [Fact]
    public void Link_next_chains_stay_on_one_string()
    {
        var arr = BuildArrangement();
        var beats = BuildBeats(40);
        var level = arr.Levels[0];

        // Slide-linked pair high on the A string, surrounded by an open-position
        // phrase pulling everything toward the nut.
        var source = Note(Pos(4), 1, 10);
        source.SlideTo = 12;
        source.Sustain = new FractionalPosition(1);
        source.LinkNext = true;
        var target = Note(Pos(5), 1, 12);
        level.Notes.Add(source);
        level.Notes.Add(target);
        for (var b = 6; b < 12; b++) level.Notes.Add(Note(Pos(b), 2, b % 3));

        var sourcePitch = Sounding(arr, 1, 10);
        var targetPitch = Sounding(arr, 1, 12);

        CreateService().RefretArrangement(arr, beats);

        Assert.Equal(source.StringIndex, target.StringIndex);
        Assert.Equal(sourcePitch, Sounding(arr, source.StringIndex, source.Fret));
        Assert.Equal(targetPitch, Sounding(arr, target.StringIndex, target.Fret));
        Assert.Equal(target.Fret, source.SlideTo!.Value); // link+slide integrity
    }

    [Fact]
    public void Slides_keep_their_target_pitch()
    {
        var arr = BuildArrangement();
        var beats = BuildBeats(40);
        var level = arr.Levels[0];

        var slide = Note(Pos(4), 1, 10);
        slide.SlideTo = 12;
        slide.Sustain = new FractionalPosition(1);
        level.Notes.Add(slide);
        for (var b = 6; b < 12; b++) level.Notes.Add(Note(Pos(b), 2, 1 + b % 3));

        var startPitch = Sounding(arr, 1, 10);
        var targetPitch = OpenMidi(arr, 1) + 12;

        CreateService().RefretArrangement(arr, beats);

        Assert.True(slide.SlideTo!.Value >= 1);
        Assert.True(slide.Fret >= 1); // a slide can never start on an open string
        Assert.Equal(startPitch, Sounding(arr, slide.StringIndex, slide.Fret));
        Assert.Equal(targetPitch, OpenMidi(arr, slide.StringIndex) + slide.SlideTo.Value);
    }

    [Fact]
    public void Hammer_on_runs_stay_on_the_source_notes_string()
    {
        var arr = BuildArrangement();
        var beats = BuildBeats(40);
        var level = arr.Levels[0];

        var pick = Note(Pos(4), 1, 10);
        var hammer1 = Note(Pos(4, 1), 1, 12);
        hammer1.Hopo = HopoType.HammerOn;
        var hammer2 = Note(Pos(4, 2), 1, 13);
        hammer2.Hopo = HopoType.HammerOn;
        level.Notes.Add(pick);
        level.Notes.Add(hammer1);
        level.Notes.Add(hammer2);
        for (var b = 6; b < 12; b++) level.Notes.Add(Note(Pos(b), 2, 1 + b % 3));

        CreateService().RefretArrangement(arr, beats);

        Assert.Equal(pick.StringIndex, hammer1.StringIndex);
        Assert.Equal(pick.StringIndex, hammer2.StringIndex);
    }

    // ─── (3) Behavioral cases ────────────────────────────────────────────────

    [Fact]
    public void Low_E_pedal_riff_stays_on_the_low_string()
    {
        var arr = BuildArrangement();
        var beats = BuildBeats(40);
        var level = arr.Levels[0];

        // Classic 16th chug: open low-E pedal alternating with 5/7/8 — the A-string
        // alternative (0/2/3) must lose to string-crossing cost.
        int[] frets = [0, 5, 0, 7, 0, 8, 0, 7];
        for (var i = 0; i < 32; i++)
            level.Notes.Add(Note(Pos(1 + i / 4, i % 4), 0, frets[i % frets.Length]));

        CreateService().RefretArrangement(arr, beats);

        Assert.All(level.Notes, n => Assert.Equal(0, n.StringIndex));
    }

    [Fact]
    public void Awkward_single_string_scale_measurably_improves()
    {
        var arr = BuildArrangement();
        var beats = BuildBeats(40);
        var level = arr.Levels[0];

        // C major scale authored entirely on the A string — frets 3..15, eighth
        // notes. A one-position cross-string fingering exists around fret 2-5.
        int[] frets = [3, 5, 7, 8, 10, 12, 14, 15];
        for (var i = 0; i < frets.Length; i++)
            level.Notes.Add(Note(Pos(1 + i / 2, (i % 2) * 2), 1, frets[i]));

        var before = PitchSignature(arr);
        var result = CreateService().RefretArrangement(arr, beats);

        Assert.Equal(before, PitchSignature(arr));
        Assert.True(result.NotesChanged >= 4, $"Only {result.NotesChanged} notes moved.");
        Assert.True(result.TravelAfter <= result.TravelBefore * 0.5,
            $"Travel only went {result.TravelBefore} → {result.TravelAfter}; expected ≥50% reduction.");
        Assert.True(level.Notes.Select(n => n.StringIndex).Distinct().Count() > 1,
            "The scale should spread across strings.");
    }

    /// <summary>
    ///     A 7-string's strings are B1 E2 A2 D3 G3 B3 E4 (#742) — the major third sits
    ///     between strings 4 and 5, not 3 and 4 as the old private table had it. A
    ///     high scale on the top string that refret spreads across the G/B pair must
    ///     keep every pitch the TAB means.
    /// </summary>
    [Fact]
    public void Seven_string_scale_keeps_its_pitches_across_the_low_extended_strings()
    {
        var arr = BuildArrangement(7);
        var beats = BuildBeats(40);
        var level = arr.Levels[0];

        // E major scale authored entirely on the high E string (index 6), frets 12..24.
        int[] frets = [12, 14, 16, 17, 19, 21, 23, 24];
        for (var i = 0; i < frets.Length; i++)
            level.Notes.Add(Note(Pos(1 + i / 2, (i % 2) * 2), 6, frets[i]));

        var before = PitchSignature(arr);
        Assert.Equal(64 + 12, before[0].Pitch); // E5: string 6 of a 7-string is E4

        var result = CreateService().RefretArrangement(arr, beats);

        Assert.True(result.NotesChanged >= 2, $"Only {result.NotesChanged} notes moved.");
        Assert.Contains(level.Notes, n => n.StringIndex == 4); // crossed onto the G string
        Assert.Equal(before, PitchSignature(arr));
    }

    [Fact]
    public void Sustained_voices_never_gain_string_collisions()
    {
        var arr = BuildArrangement();
        var beats = BuildBeats(40);
        var level = arr.Levels[0];

        // A G-string drone held under a B-string melody whose cheaper home IS
        // the G string — the collision sweep must keep the voices apart.
        var drone = Note(Pos(1), 3, 2);
        drone.Sustain = new FractionalPosition(8);
        level.Notes.Add(drone);
        int[] melody = [9, 11, 9, 11, 9, 11, 9, 11];
        for (var i = 0; i < melody.Length; i++)
            level.Notes.Add(Note(Pos(2 + i, 0), 2, melody[i]));

        var before = PitchSignature(arr);
        CreateService().RefretArrangement(arr, beats);

        Assert.Equal(before, PitchSignature(arr));
        AssertNoStringCollisions(arr, beats);
    }

    [Fact]
    public void Long_drones_under_a_dense_melody_stay_collision_free()
    {
        // The collision sweep's DEEP case, and the one the active-set rewrite of
        // RevertCollisions touches: a held voice has to stay comparable against
        // many later onsets on the same string, while onsets it can no longer
        // reach must drop out. Same shape as the single-drone test above, scaled
        // so several melody notes sit under each drone.
        var arr = BuildArrangement();
        var beats = BuildBeats(80);
        var level = arr.Levels[0];

        for (var d = 0; d < 3; d++)
        {
            var drone = Note(Pos(1 + d * 10), 3, 2 + d);
            drone.Sustain = new FractionalPosition(9);
            level.Notes.Add(drone);
        }

        for (var i = 0; i < 48; i++)
            level.Notes.Add(Note(Pos(2 + i / 2, i % 2 * 2), 2, i % 2 == 0 ? 9 : 11));

        var before = PitchSignature(arr);
        CreateService().RefretArrangement(arr, beats);

        Assert.Equal(before, PitchSignature(arr));
        AssertNoStringCollisions(arr, beats);
    }

    // ─── Capo / tuning / bass ────────────────────────────────────────────────

    [Fact]
    public void Capo_charts_respect_the_capo_floor_and_preserve_capo_pitch_semantics()
    {
        var arr = BuildArrangement(capo: 3);
        var beats = BuildBeats(40);
        var level = arr.Levels[0];

        int[] frets = [0, 5, 7, 0, 8, 10, 12, 0, 5, 7];
        for (var i = 0; i < frets.Length; i++)
            level.Notes.Add(Note(Pos(1 + i / 2, (i % 2) * 2), (i * 7 % 3) + 1, frets[i]));

        var before = PitchSignature(arr);
        CreateService().RefretArrangement(arr, beats);

        Assert.Equal(before, PitchSignature(arr));
        Assert.All(level.Notes, n => Assert.True(n.Fret is 0 or > 3,
            $"Fret {n.Fret} sits behind the capo."));
    }

    [Fact]
    public void Four_string_drop_tuned_bass_preserves_pitches_and_keeps_the_low_riff_low()
    {
        var arr = BuildArrangement(4, "E_DROP_D");
        var beats = BuildBeats(40);
        var level = arr.Levels[0];

        // Drop-D riff: frets 0..3 on the drop string exist nowhere else.
        int[] lowRiff = [0, 3, 0, 5, 0, 3, 2, 0];
        for (var i = 0; i < lowRiff.Length; i++)
            level.Notes.Add(Note(Pos(1 + i / 4, i % 4), 0, lowRiff[i]));
        // Plus something movable higher up.
        for (var i = 0; i < 6; i++)
            level.Notes.Add(Note(Pos(4 + i, 0), 1, 9 + i % 3));

        var before = PitchSignature(arr);
        CreateService().RefretArrangement(arr, beats);

        Assert.Equal(before, PitchSignature(arr));
        for (var i = 0; i < lowRiff.Length; i++)
            Assert.Equal(0, level.Notes[i].StringIndex);
    }

    // ─── Selection range ─────────────────────────────────────────────────────

    [Fact]
    public void Range_refret_only_touches_sounds_inside_the_range()
    {
        var arr = BuildArrangement();
        var beats = BuildBeats(60);
        var level = arr.Levels[0];

        int[] frets = [3, 5, 7, 8, 10, 12, 14, 15];
        for (var i = 0; i < frets.Length; i++)
            level.Notes.Add(Note(Pos(1 + i / 2, (i % 2) * 2), 1, frets[i]));
        var outside = Note(Pos(20), 1, 15); // same awkwardness, outside the range
        level.Notes.Add(outside);

        var result = CreateService().RefretRange(arr, level, beats, Pos(1), Pos(6));

        Assert.True(result.HasChanges);
        Assert.Equal((1, 15), (outside.StringIndex, outside.Fret));
    }

    [Fact]
    public void Range_refret_leaves_the_anchor_list_sorted()
    {
        // RegenerateAnchors used to remove the stale in-range anchors and APPEND
        // the regenerated ones, so after a RANGE refret the out-of-range anchors
        // kept their places at the front and the new ones sat at the end.
        // level.Anchors is binary-searched by TimelineMath.GetVisibleRange, so
        // anchors past the splice point vanished from (or duplicated in) the
        // 2D/3D viewport, and RemoveRedundantAnchors — which compares CONSECUTIVE
        // anchors — deletes the wrong ones on an unsorted list.
        var arr = BuildArrangement();
        var beats = BuildBeats(80);
        var level = arr.Levels[0];

        int[] frets = [3, 5, 7, 8, 10, 12, 14, 15];
        for (var i = 0; i < frets.Length; i++)
            level.Notes.Add(Note(Pos(1 + i / 2, (i % 2) * 2), 1, frets[i]));
        // A sound (and its anchor) AFTER the refret range — this is what has to
        // stay ahead of nothing and behind everything the splice re-inserts.
        level.Notes.Add(Note(Pos(30), 1, 15));
        level.Anchors.Add(new FretAnchor(Pos(1), 3));
        level.Anchors.Add(new FretAnchor(Pos(30), 14));

        var result = CreateService().RefretRange(arr, level, beats, Pos(1), Pos(6));

        Assert.True(result.HasChanges);
        Assert.True(result.AnchorsRegenerated > 0);
        var positions = level.Anchors.Select(a => a.Position).ToList();
        Assert.Equal(positions.OrderBy(p => p).ToList(), positions);
    }

    // ─── Anchors / hand shapes / templates ───────────────────────────────────

    [Fact]
    public void Anchors_are_regenerated_for_the_new_positions()
    {
        var arr = BuildArrangement();
        var beats = BuildBeats(40);
        var level = arr.Levels[0];

        int[] frets = [3, 5, 7, 8, 10, 12, 14, 15];
        for (var i = 0; i < frets.Length; i++)
            level.Notes.Add(Note(Pos(1 + i / 2, (i % 2) * 2), 1, frets[i]));
        level.Anchors.Add(new FretAnchor(Pos(1), 3));
        level.Anchors.Add(new FretAnchor(Pos(3), 10));
        level.Anchors.Add(new FretAnchor(Pos(4), 14));

        var result = CreateService().RefretArrangement(arr, beats);

        Assert.True(result.HasChanges);
        Assert.True(result.AnchorsRegenerated > 0);
        // The refretted scale sits in one low position — no anchor may still
        // point at the abandoned 10th-15th fret positions.
        Assert.All(level.Anchors, a => Assert.True(a.Fret <= 8,
            $"Stale anchor at fret {a.Fret} after refret."));
    }

    [Fact]
    public void Hand_shape_follows_its_chord_groups_new_template_and_old_template_is_untouched()
    {
        var arr = BuildArrangement();
        var beats = BuildBeats(40);
        var level = arr.Levels[0];

        // A5 authored as 5-7-7 — the open A5 (0-2-2 from the A string) is
        // strictly cheaper, so the group moves and mints a new template.
        var template = new ChordTemplate { Id = 0, Name = "A5" };
        template.Frets[0] = 5;
        template.Frets[1] = 7;
        template.Frets[2] = 7;
        arr.ChordTemplates.Add(template);

        for (var b = 1; b <= 3; b++)
        {
            var chord = new Chord { Position = Pos(b), TemplateId = 0 };
            foreach (var kv in template.Frets.OrderBy(kv => kv.Key))
                chord.ChordNotes.Add(new ChordNote { StringIndex = kv.Key, Fret = kv.Value });
            level.Chords.Add(chord);
        }

        level.HandShapes.Add(new HandShape
        {
            Position = Pos(1),
            EndPosition = Pos(3, 2),
            TemplateId = 0,
            Name = "A5"
        });

        var before = PitchSignature(arr);
        var result = CreateService().RefretArrangement(arr, beats);

        Assert.Equal(before, PitchSignature(arr));
        Assert.True(result.ChordsChanged > 0, "The awkward A5 voicing should have moved.");
        Assert.Equal(1, result.TemplatesAdded);

        // Original template untouched (other levels/chords may reference it).
        Assert.Equal(5, template.Frets[0]);
        Assert.Equal(7, template.Frets[1]);
        Assert.Equal(7, template.Frets[2]);

        var newId = level.Chords[0].TemplateId;
        Assert.NotEqual(0, newId);
        Assert.All(level.Chords, c => Assert.Equal(newId, c.TemplateId));
        Assert.Equal(newId, level.HandShapes[0].TemplateId);

        var newTemplate = arr.ChordTemplates.First(t => t.Id == newId);
        Assert.Equal("A5", newTemplate.Name);
        Assert.True(newTemplate.Fingers.Count > 0, "New templates carry a generated fingering.");
    }

    [Fact]
    public void Sounds_inside_mixed_hand_shapes_are_fixed()
    {
        var arr = BuildArrangement();
        var beats = BuildBeats(40);
        var level = arr.Levels[0];

        // An arpeggio hand shape: single notes picked over a held shape. The
        // notes are awkward on purpose — they still must not move.
        var template = new ChordTemplate { Id = 0, Name = "Arp" };
        template.Frets[1] = 10;
        template.Frets[2] = 12;
        arr.ChordTemplates.Add(template);
        level.HandShapes.Add(new HandShape
        {
            Position = Pos(1),
            EndPosition = Pos(3),
            TemplateId = 0
        });
        var inside1 = Note(Pos(1), 1, 10);
        var inside2 = Note(Pos(2), 2, 12);
        level.Notes.Add(inside1);
        level.Notes.Add(inside2);
        for (var b = 4; b < 10; b++) level.Notes.Add(Note(Pos(b), 2, 1 + b % 3));

        CreateService().RefretArrangement(arr, beats);

        Assert.Equal((1, 10), (inside1.StringIndex, inside1.Fret));
        Assert.Equal((2, 12), (inside2.StringIndex, inside2.Fret));
    }

    // ─── Degenerate / extreme charts (must not crash; pitch stays exact) ──────

    [Fact]
    public void Empty_level_refret_is_a_no_op()
    {
        var arr = BuildArrangement();
        var beats = BuildBeats(40);

        var result = CreateService().RefretArrangement(arr, beats);

        Assert.False(result.HasChanges);
        Assert.Empty(arr.Levels[0].Notes);
        Assert.Empty(arr.Levels[0].Chords);
    }

    [Fact]
    public void All_fixed_chart_never_moves_a_single_sound()
    {
        // Every note is technique-pinned (a bend), so no unit is movable. The DP
        // must degrade to the identity: no change even with a strong nut pull.
        var arr = BuildArrangement();
        var beats = BuildBeats(40);
        var level = arr.Levels[0];

        for (var b = 1; b < 12; b++)
        {
            var n = Note(Pos(b), 1, 9 + b % 4);
            n.BendValues.Add(new BendValue(Pos(b), 1.0));
            level.Notes.Add(n);
        }

        var before = AssignmentSignature(arr);
        var result = CreateService().RefretArrangement(arr, beats);

        Assert.False(result.HasChanges);
        Assert.Equal(before, AssignmentSignature(arr));
    }

    [Fact]
    public void Single_string_arrangement_preserves_pitch_and_cannot_relocate()
    {
        // StringCount == 1: there is nowhere to move a note to. The planner must
        // not divide by an empty string set or index out of range — it should
        // simply leave the (only) assignment intact.
        var arr = BuildArrangement(stringCount: 1);
        var beats = BuildBeats(40);
        var level = arr.Levels[0];

        int[] frets = [3, 5, 7, 8, 10, 12, 14, 15];
        for (var i = 0; i < frets.Length; i++)
            level.Notes.Add(Note(Pos(1 + i / 2, (i % 2) * 2), 0, frets[i]));

        var before = PitchSignature(arr);
        var result = CreateService().RefretArrangement(arr, beats);

        Assert.Equal(before, PitchSignature(arr));
        Assert.All(level.Notes, n => Assert.Equal(0, n.StringIndex));
        Assert.False(result.HasChanges); // one string ⇒ only the original candidate exists
    }

    [Fact]
    public void Capo_eleven_respects_the_high_capo_floor_and_preserves_pitch()
    {
        // An extreme capo: every fret 1..11 is behind it, so a valid note is
        // either open (sounding at the 11th) or fret ≥ 12.
        var arr = BuildArrangement(capo: 11);
        var beats = BuildBeats(40);
        var level = arr.Levels[0];

        int[] frets = [0, 12, 14, 0, 13, 15, 0, 12];
        for (var i = 0; i < frets.Length; i++)
            level.Notes.Add(Note(Pos(1 + i / 2, (i % 2) * 2), (i % 3) + 1, frets[i]));

        var before = PitchSignature(arr);
        CreateService().RefretArrangement(arr, beats);

        Assert.Equal(before, PitchSignature(arr));
        Assert.All(level.Notes, n => Assert.True(n.Fret is 0 or > 11,
            $"Fret {n.Fret} sits behind the capo-11."));
    }

    [Fact]
    public void Chord_only_chart_preserves_pitches_and_stays_playable()
    {
        // No single notes at all — only a repeated awkward voicing. The open
        // A5 (0-2-2) is strictly cheaper than the authored 5-7-7, so the group
        // should move, but every sounding pitch must be identical.
        var arr = BuildArrangement();
        var beats = BuildBeats(40);
        var level = arr.Levels[0];

        var template = new ChordTemplate { Id = 0, Name = "A5" };
        template.Frets[0] = 5;
        template.Frets[1] = 7;
        template.Frets[2] = 7;
        arr.ChordTemplates.Add(template);

        for (var b = 1; b <= 4; b++)
        {
            var chord = new Chord { Position = Pos(b), TemplateId = 0 };
            foreach (var kv in template.Frets.OrderBy(kv => kv.Key))
                chord.ChordNotes.Add(new ChordNote { StringIndex = kv.Key, Fret = kv.Value });
            level.Chords.Add(chord);
        }

        var before = PitchSignature(arr);
        CreateService().RefretArrangement(arr, beats);

        Assert.Equal(before, PitchSignature(arr));
        foreach (var chord in level.Chords)
        {
            var strings = chord.ChordNotes.Select(cn => cn.StringIndex).ToList();
            Assert.Equal(strings.Count, strings.Distinct().Count()); // no doubled string
            Assert.All(strings, s => Assert.InRange(s, 0, arr.StringCount - 1));
        }
    }
}
