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

    private static LiveCqtVerifier OfflineOver(float[] audio, double chordCoverage = 0.5)
    {
        var cqt = new JuceConstantQ();
        cqt.AnalyzeWhole(audio);
        return new LiveCqtVerifier(cqt, chordCoverage);
    }

    [Fact]
    public void SingleNote_ConfirmsPlayed_RejectsWrongFret()
    {
        var v = OfflineOver(Chord([52]));   // E3 at 200 ms
        Assert.True(v.Verify(new ExpectedSound { TimeMs = 200, MidiNotes = [52] }).Confirmed);
        Assert.False(v.Verify(new ExpectedSound { TimeMs = 200, MidiNotes = [53] }).Confirmed);
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
