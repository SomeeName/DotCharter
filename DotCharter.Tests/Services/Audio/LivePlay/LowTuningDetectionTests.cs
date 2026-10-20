using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Tests.Services.Audio.LivePlay;

/// <summary>
///     Detection below standard low E.
///     <para>
///     The CQT was built from MIDI 40 (E2) upward, so <c>BinForMidi</c> returned a
///     NEGATIVE bin for anything lower and the verifier bailed out with a hard
///     <c>false</c> — before it ever looked at the audio. Every note below E2 was an
///     automatic Miss no matter how perfectly it was played:
///     </para>
///     <list type="bullet">
///       <item>Eb standard (a half step down) — low string open = MIDI 39</item>
///       <item>Drop D — MIDI 38</item>
///       <item>7-string low B — MIDI 35</item>
///       <item>Bass, whose open low E is MIDI 28</item>
///     </list>
///     <para>
///     And the drop-tune pedal, whose entire job is to put the player below E2.
///     </para>
/// </summary>
public sealed class LowTuningDetectionTests
{
    private const int Sr = 44100;

    private static float[] Pluck(int midi, double leadInSec = 0.4, double durSec = 1.4)
    {
        var f0 = 440.0 * Math.Pow(2.0, (midi - 69) / 12.0);
        int lead = (int)(leadInSec * Sr), len = (int)(durSec * Sr);
        var y = new float[lead + len];
        for (var i = 0; i < len; i++)
        {
            var t = i / (double)Sr;
            double s = 0;
            for (var h = 1; h <= 8; h++)
            {
                var fh = f0 * h;
                if (fh > Sr / 2.0) break;
                s += Math.Sin(2 * Math.PI * fh * t) / h;
            }
            var env = (1.0 - Math.Exp(-t * 400.0)) * Math.Exp(-t * 1.6);
            y[lead + i] = (float)(s * env) * 0.5f;
        }
        return y;
    }

    private static LiveCqtVerifier Streamed(float[] audio)
    {
        var v = LiveCqtVerifier.CreateStreaming(Sr);
        const int chunk = 1600;
        for (var off = 0; off < audio.Length; off += chunk)
            v.Push(audio.AsSpan(off, Math.Min(chunk, audio.Length - off)), off);
        return v;
    }

    /// <summary>
    ///     The regression. Each of these is a real tuning a real player uses, and every
    ///     one of them was scored zero.
    /// </summary>
    [Theory]
    [InlineData(40, "E2 — standard low E (the control: this always worked)")]
    [InlineData(39, "Eb standard — Hendrix, SRV, GN'R, Metallica, Slipknot")]
    [InlineData(38, "Drop D")]
    [InlineData(35, "7-string low B")]
    [InlineData(28, "Bass low E")]
    public void A_note_below_standard_low_E_can_actually_be_hit(int midi, string why)
    {
        var v = Streamed(Pluck(midi));

        var outcome = v.Verify(new ExpectedSound { TimeMs = 500, MidiNotes = [midi] });

        Assert.True(outcome.Confirmed,
            $"MIDI {midi} ({why}) was played perfectly and still did not confirm — " +
            "it is outside the CQT's range, so it can never be a hit.");
    }

    /// <summary>
    ///     The guard: widening the range must not make the verifier credulous. A note that
    ///     was NOT played must still be rejected down there, where the kernels are
    ///     truncated and resolution is at its worst.
    /// </summary>
    [Theory]
    [InlineData(38, 41)]   // played drop-D, charted 3 semitones up
    [InlineData(35, 38)]   // played low B, charted D
    public void A_low_note_that_was_not_played_is_still_rejected(int played, int charted)
    {
        var v = Streamed(Pluck(played));

        Assert.False(
            v.Verify(new ExpectedSound { TimeMs = 500, MidiNotes = [charted] }).Confirmed,
            $"MIDI {charted} was never played (the player played {played}) and must not confirm");
    }
}
