using DotCharter.Services.Audio.LivePlay;
using JuceSharp.Dsp;

namespace DotCharter.Tests.Services.Audio.LivePlay;

/// <summary>Detection v8: the CQT verifier adapter (single note + chord coverage),
/// tested offline against synthesized audio (no live capture).</summary>
public sealed class LiveCqtVerifierTests
{
    private const int Sr = 44100;

    // A sustained additive chord starting after a lead-in, decaying gently.
    private static float[] Chord(int[] midis, double leadIn = 0.2, double dur = 1.0)
    {
        int lead = (int)(leadIn * Sr), len = (int)(dur * Sr);
        var y = new float[lead + len];
        foreach (var m in midis)
        {
            var f = 440f * MathF.Pow(2f, (m - 69) / 12f);
            for (var i = 0; i < len; i++)
            {
                var t = (float)i / Sr;
                y[lead + i] += MathF.Sin(2 * MathF.PI * f * t) * MathF.Exp(-t * 1.2f) / midis.Length;
            }
        }
        return y;
    }

    private static LiveCqtVerifier OfflineOver(float[] audio, double chordCoverage = 0.5,
        LiveChordVoicingStrictness strictness = LiveChordVoicingStrictness.Standard)
    {
        var cqt = new JuceConstantQ();
        cqt.AnalyzeWhole(audio);
        return new LiveCqtVerifier(cqt, chordCoverage) { ChordStrictness = strictness };
    }

    [Theory]
    [InlineData(2, 0)]
    [InlineData(0, 3)]
    [InlineData(0, -3)]
    public void ChordTones_AcceptTheirOwnBendAndSlideRanges(int bend, int slide)
    {
        var displacement = bend > 0 ? bend : slide;
        var v = OfflineOver(Chord([64 + displacement, 71 + displacement, 76 + displacement]),
            chordCoverage: 1.0);
        var sound = new ExpectedSound
        {
            TimeMs = 200, MidiNotes = [64, 71, 76], IsChord = true, MaxBendSemis = bend, SlideSemis = slide
        };

        // Older runtimes carry only the aggregate; new timelines carry each tone's range.
        typeof(ExpectedSound).GetProperty("ChordBendSemis")?.SetValue(sound, new[] { bend, bend, bend });
        Assert.True(v.Verify(sound).Confirmed);
        Assert.True(v.VerifyEarly(sound).Confirmed);
        // A sound with no travel cannot borrow the displaced chord's pitches.
        Assert.False(v.Verify(new ExpectedSound { TimeMs = 200, MidiNotes = [64, 71, 76], IsChord = true }).Confirmed);
    }

    [Theory]
    [InlineData(false, 66, false)]
    [InlineData(false, 64, true)]
    [InlineData(true, 66, true)]
    public void Chord_bends_belong_to_the_tone_that_authored_them(bool bendBoth, int playedLower, bool hit)
    {
        var arrangement = new Arrangement { ArrangementType = ArrangementType.Lead };
        var level = new ChartLevel();
        arrangement.Levels.Add(level);
        var chord = new Chord { Position = new FractionalPosition(0) };
        var lower = new ChordNote { StringIndex = 5, Fret = 0 };
        if (bendBoth) lower.BendValues.Add(new BendValue(new FractionalPosition(0), 2));
        var upper = new ChordNote { StringIndex = 4, Fret = 12 };
        upper.BendValues.Add(new BendValue(new FractionalPosition(0), 2));
        chord.ChordNotes.Add(lower);
        chord.ChordNotes.Add(upper);
        level.Chords.Add(chord);
        var expected = new List<ExpectedSound>();
        new LiveExpectedTimeline().Build(expected, arrangement, level,
            [new BeatMarker { Time = 200, Bpm = 120 }, new BeatMarker { Time = 700, Bpm = 120 }], 0);
        var sound = Assert.Single(expected);
        Assert.Equal(new[] { 64, 71 }, sound.MidiNotes);
        var verifier = OfflineOver(Chord([playedLower, 73]), chordCoverage: 1.0,
            strictness: LiveChordVoicingStrictness.Strict);

        Assert.Equal(hit, verifier.Verify(sound).Confirmed);
        Assert.Equal(hit, verifier.VerifyEarly(sound).Confirmed);
    }

    [Theory]
    [InlineData(0, 2, false, true, false)]
    [InlineData(0, 2, true, false, false)]
    [InlineData(0, 2, true, true, true)]
    [InlineData(2, 2, false, true, true)]
    [InlineData(0, 0, true, false, true)]
    [InlineData(0, 0, false, true, false)]
    public void Unison_strings_keep_distinct_bend_requirements(int firstBend, int secondBend, bool basePlayed, bool bentPlayed, bool hit)
    {
        var arrangement = new Arrangement { ArrangementType = ArrangementType.Lead };
        var level = new ChartLevel();
        arrangement.Levels.Add(level);
        var chord = new Chord { Position = new FractionalPosition(0) };
        var first = new ChordNote { StringIndex = 5, Fret = 0 };
        var second = new ChordNote { StringIndex = 4, Fret = 5 };
        if (firstBend > 0) first.BendValues.Add(new BendValue(new FractionalPosition(0), firstBend));
        if (secondBend > 0) second.BendValues.Add(new BendValue(new FractionalPosition(0), secondBend));
        chord.ChordNotes.Add(first);
        chord.ChordNotes.Add(second);
        level.Chords.Add(chord);
        var expected = new List<ExpectedSound>();
        new LiveExpectedTimeline().Build(expected, arrangement, level,
            [new BeatMarker { Time = 200, Bpm = 120 }, new BeatMarker { Time = 700, Bpm = 120 }], 0);
        var sound = Assert.Single(expected);
        Assert.Equal(new[] { 64, 64 }, sound.MidiNotes);
        var played = new List<int>();
        if (basePlayed) played.Add(64);
        if (bentPlayed) played.Add(66);
        var verifier = OfflineOver(Chord(played.ToArray()), chordCoverage: 1.0,
            strictness: LiveChordVoicingStrictness.Strict);

        Assert.Equal(hit, verifier.Verify(sound).Confirmed);
        Assert.Equal(hit, verifier.VerifyEarly(sound).Confirmed);
    }

    [Fact]
    public void SingleNote_ConfirmsPlayed_RejectsWrongFret()
    {
        var v = OfflineOver(Chord([52]));   // E3 at 200 ms
        Assert.True(v.Verify(new ExpectedSound { TimeMs = 200, MidiNotes = [52] }).Confirmed);
        Assert.False(v.Verify(new ExpectedSound { TimeMs = 200, MidiNotes = [53] }).Confirmed);
    }

    [Theory]
    [InlineData(false, -0.5, false)]
    [InlineData(false, 0.5, true)]
    [InlineData(true, -0.5, true)]
    [InlineData(true, 0.5, true)]
    public void Mixed_bend_unisons_keep_the_presets_ordinary_detuned_tolerance(bool forgiving, double detune, bool hit)
    {
        var verifier = DetunedUnisonVerifier([64 + detune, 66], forgiving);
        var mixed = new ExpectedSound { TimeMs = 200, MidiNotes = [64, 64], ChordBendSemis = [0, 2], IsChord = true };
        var ordinary = new ExpectedSound { TimeMs = 200, MidiNotes = [64, 66], IsChord = true };

        Assert.Equal(hit, verifier.Verify(ordinary).Confirmed);
        Assert.Equal(hit, verifier.VerifyEarly(ordinary).Confirmed);
        Assert.Equal(hit, verifier.Verify(mixed).Confirmed);
        Assert.Equal(hit, verifier.VerifyEarly(mixed).Confirmed);
    }

    // A single detuned string is a negative control for distinct unison evidence in both modes.
    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    public void One_detuned_pitch_cannot_supply_two_unison_requirements(int bend, bool forgiving)
    {
        var verifier = DetunedUnisonVerifier([64.5], forgiving);
        var sound = new ExpectedSound { TimeMs = 200, MidiNotes = [64, 64], ChordBendSemis = [0, bend], IsChord = true };

        Assert.False(verifier.Verify(sound).Confirmed);
        Assert.False(verifier.VerifyEarly(sound).Confirmed);
    }

    [Fact]
    public void A_forgiving_check_confirms_an_in_tune_semitone_mixed_unison()
    {
        // A staggered entry remains valid alongside the simultaneous-pair cases in
        // CqtUnisonResolutionTests. The first string keeps ringing when the second enters.
        var verifier = DetunedUnisonVerifier([64, 65], true, [0.2, 0.3]);
        var sound = new ExpectedSound { TimeMs = 200, MidiNotes = [64, 64], ChordBendSemis = [0, 1], IsChord = true };

        Assert.True(verifier.Verify(sound).Confirmed);
        Assert.True(verifier.VerifyEarly(sound).Confirmed);
    }

    [Fact]
    public void Detuned_unison_matching_reuses_its_per_frame_scratch_storage()
    {
        var verifier = DetunedUnisonVerifier([63.5, 66], true);
        var sound = new ExpectedSound { TimeMs = 200, MidiNotes = [64, 64], ChordBendSemis = [0, 2], IsChord = true };
        // Isolate the unison path from ChordQuality's existing interface enumerators.
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var confirms = typeof(LiveCqtVerifier).GetMethod("ConfirmsChordTone", flags)!
            .CreateDelegate<Func<JuceChartVerifier, ExpectedSound, int, double, bool, bool, bool>>(verifier);
        var chord = (JuceChartVerifier)typeof(LiveCqtVerifier).GetField("_chordVerifier", flags)!.GetValue(verifier)!;

        AllocationProbe.AssertAllocationFree("detuned-unison chord-tone matching", () =>
        {
            for (var i = 0; i < 32; i++)
                confirms(chord, sound, 64, 0.2, true, false);
        });

        Assert.True(confirms(chord, sound, 64, 0.2, true, false));
    }

    private static LiveCqtVerifier DetunedUnisonVerifier(double[] midis, bool forgiving, double[]? entries = null)
    {
        var audio = new float[(int)(1.2 * Sr)];
        for (var n = 0; n < midis.Length; n++)
        {
            var frequency = 440 * Math.Pow(2, (midis[n] - 69) / 12);
            var first = (int)((entries?[n] ?? 0.2) * Sr);
            for (var i = 0; i < Sr && first + i < audio.Length; i++)
            {
                var t = (double)i / Sr;
                audio[first + i] += (float)(Math.Sin(2 * Math.PI * frequency * t) * Math.Exp(-t * 1.2) / midis.Length);
            }
        }

        var cqt = new JuceConstantQ();
        cqt.AnalyzeWhole(audio);
        return new LiveCqtVerifier(cqt, 1) { Forgiving = forgiving, ChordStrictness = LiveChordVoicingStrictness.Strict };
    }

    [Fact]
    public void Chord_ConfirmsAtHalfCoverage_RejectsWrongChord()
    {
        int[] cmaj7 = [48, 52, 55, 59];
        var v = OfflineOver(Chord(cmaj7));
        var hit = v.Verify(new ExpectedSound { TimeMs = 200, MidiNotes = cmaj7, IsChord = true });
        Assert.True(hit.Confirmed);
        Assert.InRange(hit.Confidence, 0.5f, 1f);

        // Whole chord shifted up a semitone must not pass.
        int[] wrong = [49, 53, 56, 60];
        Assert.False(v.Verify(new ExpectedSound { TimeMs = 200, MidiNotes = wrong, IsChord = true }).Confirmed);
    }

    [Fact]
    public void EmptySound_IsNotConfirmed()
    {
        var v = OfflineOver(Chord([52]));
        Assert.False(v.Verify(new ExpectedSound { TimeMs = 200, MidiNotes = [] }).Confirmed);
    }

    [Fact]
    public void Streaming_Push_VerifiesRecentNote()
    {
        // Feed the same chord through the streaming path and verify it while the
        // frames are still resident in the rolling buffer.
        var audio = Chord([52], leadIn: 0.2, dur: 1.0);
        var cqt = new JuceConstantQ();
        var v = new LiveCqtVerifier(cqt);
        for (var off = 0; off < audio.Length; off += 1024)
            v.Push(audio.AsSpan(off, Math.Min(1024, audio.Length - off)), off);

        // 200 ms onset is well inside the ring for a 1.2 s clip.
        Assert.True(v.Verify(new ExpectedSound { TimeMs = 300, MidiNotes = [52] }).Confirmed);
    }
}
