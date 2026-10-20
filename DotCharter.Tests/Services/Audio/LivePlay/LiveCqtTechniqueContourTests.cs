using DotCharter.Services.Audio.LivePlay;
using JuceSharp.Dsp;

namespace DotCharter.Tests.Services.Audio.LivePlay;

/// <summary>
///     <see cref="LiveCqtVerifier.JudgeTechnique" /> end to end on synthesized audio (#645): the
///     contour is read off the same streaming CQT that confirms notes, so a bent, a sliding and
///     a vibrato tone are told from a held one — and the judge answers "not yet" until the
///     window's audio has reached the CQT.
/// </summary>
public sealed class LiveCqtTechniqueContourTests
{
    private const int Sr = 44100;
    private const int E4 = 64;
    private const double LeadInMs = 200;

    // A plucked-ish tone at E4 from LeadInMs, its pitch in cents given per millisecond.
    private static float[] Tone(Func<double, double> centsAtMs, double ms = 1200)
    {
        var lead = (int)(LeadInMs * Sr / 1000);
        var y = new float[lead + (int)(ms * Sr / 1000)];
        var f0 = 440.0 * Math.Pow(2, (E4 - 69) / 12.0);
        double phase = 0;
        for (var i = lead; i < y.Length; i++)
        {
            var t = (i - lead) * 1000.0 / Sr;
            var f = f0 * Math.Pow(2, centsAtMs(t) / 1200);
            phase += 2 * Math.PI * f / Sr;
            var env = Math.Exp(-t / 1500);
            // A guitar-like spectrum: the octave and the twelfth ride along.
            y[i] = (float)(env * (0.6 * Math.Sin(phase) + 0.3 * Math.Sin(2 * phase) + 0.1 * Math.Sin(3 * phase)));
        }

        return y;
    }

    private static LiveCqtVerifier Streamed(float[] audio, int samples = -1)
    {
        var v = LiveCqtVerifier.CreateStreaming(Sr);
        var n = samples < 0 ? audio.Length : samples;
        for (var off = 0; off < n; off += 1600)
            v.Push(audio.AsSpan(off, Math.Min(1600, n - off)), off);
        return v;
    }

    private static ExpectedSound Sound(TechniqueContourExpectation contour) =>
        new() { TimeMs = LeadInMs, MidiNotes = [E4], StringIndex = 0, TechniqueContour = contour };

    private static readonly TechniqueContourExpectation FullBend = new(ContourTechniques.Bend, 2, 250, 0, 900);
    private static readonly TechniqueContourExpectation SlideUpThree = new(ContourTechniques.Slide, 0, 0, 3, 700);
    private static readonly TechniqueContourExpectation Vibrato = new(ContourTechniques.Vibrato, 0, 0, 0, 1000);

    private static double BendTo200(double t) => t < 100 ? 0 : t < 250 ? 200 * (t - 100) / 150 : 200;

    [Fact]
    public void A_bent_tone_meets_a_charted_bend_and_a_held_one_does_not()
    {
        Assert.Equal(TechniqueContourVerdict.Met, Streamed(Tone(BendTo200)).JudgeTechnique(Sound(FullBend)));
        Assert.Equal(TechniqueContourVerdict.NotMet, Streamed(Tone(_ => 0)).JudgeTechnique(Sound(FullBend)));
    }

    [Fact]
    public void A_sliding_tone_meets_a_charted_slide_and_a_held_one_does_not()
    {
        static double Slide(double t) => t < 150 ? 0 : Math.Min(300, (t - 150) * 1.5);
        Assert.Equal(TechniqueContourVerdict.Met, Streamed(Tone(Slide)).JudgeTechnique(Sound(SlideUpThree)));
        Assert.Equal(TechniqueContourVerdict.NotMet, Streamed(Tone(_ => 0)).JudgeTechnique(Sound(SlideUpThree)));
    }

    [Fact]
    public void A_vibrato_tone_meets_a_charted_vibrato_and_a_held_one_does_not()
    {
        // 5.5 Hz, ±30 cents: a modest finger vibrato.
        static double Vib(double t) => 30 * Math.Sin(2 * Math.PI * 5.5 * t / 1000);
        Assert.Equal(TechniqueContourVerdict.Met, Streamed(Tone(Vib)).JudgeTechnique(Sound(Vibrato)));
        Assert.Equal(TechniqueContourVerdict.NotMet, Streamed(Tone(_ => 0)).JudgeTechnique(Sound(Vibrato)));
    }

    [Fact]
    public void The_contour_reads_the_bend_in_cents_from_the_charted_pitch()
    {
        var v = Streamed(Tone(BendTo200));
        var sound = Sound(FullBend);
        var hops = 100;
        var cents = new float[hops];
        var first = (long)Math.Round(LeadInMs / 1000 * Sr / LiveCqtConfig.Hop);

        v.ReadContour(sound, first, cents);

        // Held at the charted pitch before the bend, at the full step after it.
        var before = cents.Skip(4).Take(8).Where(c => !float.IsNaN(c)).ToArray();
        var after = cents.Skip(70).Take(20).Where(c => !float.IsNaN(c)).ToArray();
        Assert.NotEmpty(before);
        Assert.NotEmpty(after);
        Assert.InRange(before.Average(), -25, 25);
        Assert.InRange(after.Average(), 170, 230);
    }

    [Fact]
    public void Before_the_window_has_reached_the_CQT_the_answer_is_not_yet()
    {
        var audio = Tone(BendTo200);
        // Only the first 400 ms of the note: the bend's evidence runs to 500 ms.
        var partial = Streamed(audio, (int)((LeadInMs + 400) * Sr / 1000));

        Assert.Null(partial.JudgeTechnique(Sound(FullBend)));
    }

    [Fact]
    public void Silence_where_a_bend_is_charted_is_unknown_not_a_downgrade() =>
        Assert.Equal(TechniqueContourVerdict.Unknown, Streamed(new float[Sr * 2]).JudgeTechnique(Sound(FullBend)));

    [Fact]
    public void A_sound_with_nothing_to_judge_is_unknown()
    {
        var v = Streamed(Tone(BendTo200));
        Assert.Equal(TechniqueContourVerdict.Unknown, v.JudgeTechnique(Sound(default)));
        Assert.Equal(TechniqueContourVerdict.Unknown, v.JudgeTechnique(new ExpectedSound
        {
            TimeMs = LeadInMs, MidiNotes = [E4, E4 + 7], IsChord = true, TechniqueContour = FullBend
        }));
    }
}
