using DotCharter.Services.Audio.Calibration;
using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Tests.Services.Audio.Calibration;

/// <summary>
///     The loopback calibration listens for the engine's CLAP — 90 ms of white
///     noise under a quadratic decay, base gain 0.45 (<c>AudioEngine::startClickVoice</c>).
///     These synthesise that clap at the level a loopback cable actually returns
///     it and ask each detector whether it is heard.
///     <para>
///         The owner's friend looped a Focusrite's output into input 1 and the
///         wizard heard nothing (2026-08-30). The note analyser the wizard used
///         until then has an absolute attack gate for hiss (<c>MinAttackRms</c>
///         = 0.01); the clap clears it only when the loop returns within ~20 dB
///         of unity. <see cref="LoopbackClapDetector" /> judges the level jump
///         against the ring's own floor instead.
///     </para>
/// </summary>
public sealed class LoopbackClapDetectionTests
{
    private const int Sr = 48000;

    private static float[] Clap(float gain, int seed)
    {
        var total = (int)Math.Round(Sr * 90.0 / 1000.0);
        var buf = new float[total];
        var rng = (uint)seed;
        for (var i = 0; i < total; i++)
        {
            var t = (float)i / total;
            var env = (1f - t) * (1f - t);
            rng = rng * 1664525u + 1013904223u;
            var s = (rng >> 8) / 16777216f * 2f - 1f;
            buf[i] = s * env * 0.45f * gain;
        }
        return buf;
    }

    /// <summary>1 s of hiss, then five claps 0.8 s apart over the same hiss.
    /// Returns the stream and the sample each clap starts at.</summary>
    private static (float[] Stream, int[] ClapStarts) Stream(float clapGain, float hissRms)
    {
        var stream = new List<float>();
        var starts = new List<int>();
        var rng = new Random(7);
        float Hiss() => (float)(rng.NextDouble() * 2 - 1) * hissRms * 1.732f;
        for (var i = 0; i < Sr; i++) stream.Add(Hiss());
        for (var k = 0; k < 5; k++)
        {
            starts.Add(stream.Count);
            foreach (var s in Clap(clapGain, 0x12345 + k)) stream.Add(s + Hiss());
            for (var i = 0; i < (int)(Sr * 0.8); i++) stream.Add(Hiss());
        }
        return (stream.ToArray(), starts.ToArray());
    }

    private static List<LiveDetectionEvent> Run(ILiveInputDetector detector, float[] all, int block = 480)
    {
        var events = new List<LiveDetectionEvent>();
        ulong pos = 0;
        for (var off = 0; off < all.Length; off += block)
        {
            var n = Math.Min(block, all.Length - off);
            events.AddRange(detector.Push(all.AsSpan(off, n), pos));
            pos += (ulong)n;
        }
        return events;
    }

    /// <summary>The note analyser is what the wizard listened with until
    /// 0.49.3. Pinned so the number in the doc comment stays measured: at
    /// unity it hears every clap; at -30 dB it hears none.</summary>
    [Theory]
    [InlineData(1.0f, 5)]
    [InlineData(0.03f, 0)]
    public void NoteAnalyser_HearsTheClap_OnlyNearUnity(float gain, int expected)
    {
        var (all, _) = Stream(gain, hissRms: 0f);
        var events = Run(new GuitarInputAnalyzer(Sr), all);
        Assert.Equal(expected, events.Count);
    }

    /// <summary>From unity down to -40 dB of return level, over a -70 dBFS
    /// hiss floor: every clap, once, within 2 ms of where it started.</summary>
    [Theory]
    [InlineData(1.0f)]
    [InlineData(0.1f)]
    [InlineData(0.03f)]
    [InlineData(0.01f)]
    public void ClapDetector_HearsEveryClap_Once_OnTime(float gain)
    {
        var (all, starts) = Stream(gain, hissRms: 0.0003f);
        var events = Run(new LoopbackClapDetector(Sr), all);

        Assert.Equal(starts.Length, events.Count);
        for (var k = 0; k < starts.Length; k++)
        {
            var errorMs = ((double)events[k].StreamSamplePosition - starts[k]) / Sr * 1000.0;
            Assert.True(Math.Abs(errorMs) <= 2.0, $"clap {k} at gain {gain}: onset off by {errorMs:F2} ms");
            Assert.True(events[k].Techniques.HasFlag(LiveTechniqueFlags.Percussive));
            Assert.Empty(events[k].Pitches);
        }
    }

    [Fact]
    public void ClapDetector_IsBlockSizeIndependent()
    {
        var (all, _) = Stream(0.01f, hissRms: 0.0003f);
        var a = Run(new LoopbackClapDetector(Sr), all, block: 480).Select(e => e.StreamSamplePosition).ToArray();
        var b = Run(new LoopbackClapDetector(Sr), all, block: 1000).Select(e => e.StreamSamplePosition).ToArray();
        Assert.Equal(a, b);
    }

    [Fact]
    public void ClapDetector_StaysQuiet_OnHissAlone()
    {
        var rng = new Random(3);
        var hiss = new float[Sr * 5];
        for (var i = 0; i < hiss.Length; i++) hiss[i] = (float)(rng.NextDouble() * 2 - 1) * 0.02f;
        Assert.Empty(Run(new LoopbackClapDetector(Sr), hiss));
    }

    [Fact]
    public void ClapDetector_StaysQuiet_OnADigitallySilentLoop()
    {
        // A dead ring must never fire on nothing: the absolute floor is the bar.
        var silence = new float[Sr * 3];
        silence[Sr] = 0.0002f; // one stray tick, under the absolute floor
        Assert.Empty(Run(new LoopbackClapDetector(Sr), silence));
    }

    /// <summary>The floor must not learn from the clap: five claps in a row
    /// over a silent ring are still five events, not one.</summary>
    [Fact]
    public void ClapDetector_DoesNotRaiseItsOwnBar()
    {
        var (all, _) = Stream(1.0f, hissRms: 0f);
        var detector = new LoopbackClapDetector(Sr);
        var events = Run(detector, all);
        Assert.Equal(5, events.Count);
        Assert.True(detector.NoiseFloor < 0.001f, $"floor learned from the claps: {detector.NoiseFloor}");
    }
}
