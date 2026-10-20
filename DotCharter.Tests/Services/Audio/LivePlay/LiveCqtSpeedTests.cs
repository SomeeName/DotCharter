using DotCharter.Services.Audio.LivePlay;
using JuceSharp.Dsp;

namespace DotCharter.Tests.Services.Audio.LivePlay;

/// <summary>
///     "Does note detection still work when the song is slowed down?"
///     <para>
///     This is the test that was missing, and its absence is why practice speeds shipped
///     broken twice. The previous attempt (commit 1ec1523d) had ten tests, all asserting
///     <see cref="LiveTimeMapping"/> arithmetic in ISOLATION. The arithmetic was correct.
///     It was correct and useless, because nothing ever COMPOSED the mapping with the CQT
///     it feeds — and the CQT's <c>Push</c> has a contract the mapping was violating on
///     every single call.
///     </para>
///     <para>
///     So these tests deliberately simulate the real pump: wall-clock audio, delivered in
///     the chunk sizes <c>LivePlayViewModel.Tick</c> actually delivers, while the
///     transport advances at <c>speed ×</c>. No hardware, fully deterministic.
///     </para>
/// </summary>
public sealed class LiveCqtSpeedTests
{
    private const int Sr = 44100;

    /// <summary>The pump reads ~1,600 samples per 30 Hz tick. The exact number matters:
    /// it is well BELOW the 4,096 samples the CQT needs resident to emit its first
    /// frame, which is why a per-push reset starves it forever rather than merely
    /// degrading it.</summary>
    private const int TickSamples = 1600;

    private static float[] Note(int midi, double leadInSec, double durSec, int sr = Sr)
    {
        int lead = (int)(leadInSec * sr), len = (int)(durSec * sr);
        var y = new float[lead + len];
        var f = 440f * MathF.Pow(2f, (midi - 69) / 12f);
        for (var i = 0; i < len; i++)
        {
            var t = (float)i / sr;
            // A couple of harmonics so it looks like a plucked string, not a pure tone.
            y[lead + i] = (MathF.Sin(2 * MathF.PI * f * t)
                           + 0.5f * MathF.Sin(4 * MathF.PI * f * t)
                           + 0.25f * MathF.Sin(6 * MathF.PI * f * t))
                          * MathF.Exp(-t * 1.2f) * 0.3f;
        }
        return y;
    }

    /// <summary>
    ///     Drive a streaming verifier exactly the way the live pump does: push WALL-CLOCK
    ///     audio at its raw capture-stream position, and anchor the chart axis onto it
    ///     each tick using the transport time the player would have been hearing.
    /// </summary>
    private static LiveCqtVerifier PumpAtSpeed(float[] wallClockAudio, double speed, double transportStartMs = 0)
    {
        var v = new LiveCqtVerifier(new JuceConstantQ(Sr, maxKernelSamples: LiveCqtVerifier.LiveMaxKernelSamples));

        for (var off = 0; off < wallClockAudio.Length; off += TickSamples)
        {
            var n = Math.Min(TickSamples, wallClockAudio.Length - off);
            v.Push(wallClockAudio.AsSpan(off, n), off);

            // The transport advances at speed × wall-clock. After `off` captured samples,
            // the playhead is at start + off/sr*speed seconds.
            var transportMs = transportStartMs + (off / (double)Sr) * 1000.0 * speed;
            v.SetAnchor(off, transportMs, speed);
        }
        return v;
    }

    /// <summary>
    ///     THE regression test. A note played 400 ms (wall-clock) into the capture must be
    ///     confirmed at its CHART time, at every practice speed.
    ///     <para>
    ///     Before the fix this passes at 1.0 and fails at every other speed — because the
    ///     speed-scaled push position reset the CQT ring on every push, so it never
    ///     emitted a single frame and confirmed nothing, ever.
    ///     </para>
    /// </summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(0.75)]
    [InlineData(0.6)]   // the speed ladder's default first pass
    [InlineData(0.5)]   // the owner's report: "Fear of the Dark at 50%"
    [InlineData(0.25)]  // the ladder's floor
    public void A_played_note_is_confirmed_at_every_practice_speed(double speed)
    {
        // The onset is deliberately LATE in the capture (1.2 s). The whole point is that a
        // broken mapping looks for the note at speed×(onset+probe) instead of (onset+probe),
        // and that gap has to be large enough to land in silence — bigger than the
        // verifier's own 140 ms post-window, at EVERY speed under test. With an onset at
        // 0.4 s the gap at 0.75× is only 175 ms and the post-window still catches the note,
        // so the test passes against the BROKEN code and proves nothing. (Verified: it did.)
        const double onsetWallSec = 1.2;
        var audio = Note(52, leadInSec: onsetWallSec, durSec: 1.0); // E3

        var v = PumpAtSpeed(audio, speed);

        // Chart time of that note: the player heard the song at speed×, so a note captured
        // 1.2 s of wall-clock in sits at 1.2 × speed song-seconds.
        var chartMs = onsetWallSec * 1000.0 * speed;

        // Probe a little into the sustain, where the matcher pull-verifies from. In chart
        // ms, 100 ms of real time is 100 × speed.
        var probeMs = chartMs + (100.0 * speed);

        var outcome = v.Verify(new ExpectedSound { TimeMs = probeMs, MidiNotes = [52] });
        Assert.True(outcome.Confirmed,
            $"speed {speed:0.00}: expected the played E3 to confirm at chart {probeMs:0} ms");
    }

    /// <summary>A wrong fret must still be rejected at a slow speed — the fix must not
    /// buy its hits by making the verifier credulous.</summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(0.5)]
    public void A_wrong_fret_is_still_rejected_at_every_speed(double speed)
    {
        const double onsetWallSec = 1.2;
        var audio = Note(52, leadInSec: onsetWallSec, durSec: 1.0); // E3 played
        var v = PumpAtSpeed(audio, speed);

        var probeMs = (onsetWallSec * 1000.0 * speed) + (100.0 * speed);

        Assert.False(
            v.Verify(new ExpectedSound { TimeMs = probeMs, MidiNotes = [53] }).Confirmed,
            $"speed {speed:0.00}: F3 was never played and must not confirm");
    }

    /// <summary>
    ///     The contract that was being violated, pinned directly. A speed-scaled push
    ///     position is a stream discontinuity, and a discontinuity throws away every
    ///     buffered frame — so a caller that scales it gets an ETERNALLY EMPTY transform
    ///     and no error. This test documents that, so the next person who reaches for
    ///     "just scale the position" trips over it.
    /// </summary>
    [Fact]
    public void Scaling_the_push_position_resets_the_transform_on_every_push()
    {
        var audio = Note(52, leadInSec: 0.4, durSec: 1.2);
        var cqt = new JuceConstantQ(Sr, maxKernelSamples: LiveCqtVerifier.LiveMaxKernelSamples);

        var pushes = 0;
        for (var off = 0; off < audio.Length; off += TickSamples)
        {
            var n = Math.Min(TickSamples, audio.Length - off);
            // What the old code did: label wall-clock audio with a transport-scaled index.
            cqt.Push(audio.AsSpan(off, n), (long)Math.Round(off * 0.5));
            pushes++;
        }

        // Every push after the first was non-contiguous, so every one wiped the ring.
        Assert.True(cqt.DiscontinuityResets >= pushes - 1,
            $"expected ~{pushes - 1} discontinuity resets, saw {cqt.DiscontinuityResets}");

        // And the consequence: the ring never accumulates a frame.
        Assert.True(cqt.NewestFrame < cqt.OldestFrame, "the ring should be empty");
    }

    /// <summary>Contiguous pushes — what the fixed pump does — must NOT reset anything.</summary>
    [Fact]
    public void Contiguous_pushes_never_reset_the_transform()
    {
        var audio = Note(52, leadInSec: 0.4, durSec: 1.2);
        var cqt = new JuceConstantQ(Sr, maxKernelSamples: LiveCqtVerifier.LiveMaxKernelSamples);

        for (var off = 0; off < audio.Length; off += TickSamples)
            cqt.Push(audio.AsSpan(off, Math.Min(TickSamples, audio.Length - off)), off);

        Assert.Equal(0, cqt.DiscontinuityResets);
        Assert.True(cqt.NewestFrame >= cqt.OldestFrame, "the ring should hold frames");
    }
}
