using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Tone.Infrastructure.Storage;
using JuceSharp;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
///     The one thing the waveform pyramid promises the renderer: <b>block index
///     <c>i</c> starts at <c>i × BaseBlockMs</c></b>. Every consumer maps index →
///     time with exactly that multiplication (<c>WaveformLayer</c>'s
///     <c>x = (i·blockMs − viewStartMs)·zoom</c>, <c>WaveformOnsetSnapper</c>,
///     the minimap), so a block that is not <c>BaseBlockMs</c> wide is not a
///     rounding detail — it is a drift that grows without bound for the whole
///     length of the song.
///     <para>
///         It shipped broken: the native binner used a ROUNDED integer
///         samples-per-block, and 2 ms at 44.1 kHz is 88.2 samples. Rounded to
///         88, every block was 1.99546 ms and the drawn waveform ran 0.227 %
///         LATE — +68 ms 30 s in, +408 ms at three minutes, i.e. visibly right
///         of the audio it represents, while 48 kHz (96 samples exactly) was
///         perfect and hid it. This test is the reason that cannot come back:
///         it drives the REAL native generator with impulses at exactly known
///         times and checks every one of them lands in its own block, at BOTH
///         rate families.
///     </para>
///     <para>Skips cleanly when the native DLL can't load (same convention as
///     <c>DspOfflineProcessingTests</c>), so it never depends on native being
///     present.</para>
/// </summary>
public sealed class WaveformGenerationAlignmentTests
{
    /// <summary>Impulse onsets, in ms. The late ones are the point: a rate
    /// mismatch is invisible at t=0 and unmistakable at 90 s.</summary>
    private static readonly double[] ImpulseMs = [0, 1_000, 10_000, 30_000, 60_000, 90_000];

    private const double BurstMs = 20.0;
    private const double TotalMs = 92_000.0;

    [Theory]
    // The two families that matter. 48 kHz divides 2 ms exactly (96 samples);
    // 44.1 kHz does not (88.2) and is the one that regressed.
    [InlineData(48000)]
    [InlineData(44100)]
    public void EveryImpulseLandsInTheBlockItsOwnTimeIndexes(int sampleRate)
    {
        var path = Path.Combine(Path.GetTempPath(),
            $"dc-wf-align-{sampleRate}-{Guid.NewGuid():N}.wav");
        WriteImpulseWav(path, sampleRate);

        try
        {
            var blockMs = WaveformSamples.GenerationBlockMs;
            float[] peaks;
            try
            {
                peaks = JuceAudioEngine.GenerateWaveform(path, blockMs);
            }
            catch (DllNotFoundException)
            {
                Assert.Skip("native juce_sharp is unavailable");
                return;
            }
            catch (EntryPointNotFoundException)
            {
                Assert.Skip("native juce_sharp is unavailable");
                return;
            }

            Assert.SkipWhen(peaks.Length == 0, "native waveform generation produced nothing");

            // The pyramid's own length must agree with the audio's real
            // duration: blockCount × BaseBlockMs is what the strip is drawn
            // across, so a 0.227 % over-long pyramid IS the drift, stated as
            // one number. One block of slack for the partial tail.
            var stripMs = peaks.Length * blockMs;
            Assert.True(Math.Abs(stripMs - TotalMs) <= blockMs,
                $"{sampleRate} Hz: the pyramid claims {stripMs:0.#} ms of audio for a "
                + $"{TotalMs:0.#} ms file ({peaks.Length} blocks × {blockMs} ms).");

            var samples = WaveformSamples.FromBasePeaks(peaks, blockMs);

            foreach (var t in ImpulseMs)
            {
                // The impulse starts at t and runs BurstMs, so its energy is in
                // the blocks covering [t, t+BurstMs). Allow the block before it
                // too (a burst starting mid-block leaves the previous block
                // holding part of the rise at coarser levels).
                var expected = (int)(t / blockMs);
                var loudest = ArgMaxAround(samples.Mipmaps[0], expected, blockMs);
                var errorMs = loudest * blockMs - t;

                Assert.True(errorMs >= -blockMs && errorMs <= BurstMs,
                    $"{sampleRate} Hz: the impulse at {t:0} ms peaks in block {loudest} "
                    + $"(= {loudest * blockMs:0.###} ms), off by {errorMs:+0.###;-0.###;0} ms.");
            }
        }
        finally
        {
            BestEffortDelete.File(path);
        }
    }

    /// <summary>Loudest block near where <paramref name="expected" /> says the
    /// impulse is. The window scales with the timestamp so a PROGRESSIVE drift
    /// is still found (and reported as a distance) rather than failing with
    /// "no peak here" — while staying far too narrow to reach the neighbouring
    /// impulse, which would silently answer with the wrong one.</summary>
    private static int ArgMaxAround(float[] level, int expected, double blockMs)
    {
        var half = (int)Math.Ceiling((0.02 * expected * blockMs + 200.0) / blockMs);
        var lo = Math.Max(0, expected - half);
        var hi = Math.Min(level.Length - 1, expected + half);

        var best = lo;
        for (var i = lo; i <= hi; i++)
        {
            if (level[i] > level[best])
                best = i;
        }

        return best;
    }

    /// <summary>Mono 16-bit PCM: silence with full-scale 1 kHz bursts at
    /// <see cref="ImpulseMs" />. Sample-exact by construction, so any error the
    /// probe reports belongs to the pipeline.</summary>
    private static void WriteImpulseWav(string path, int sampleRate)
    {
        var totalFrames = (int)(TotalMs / 1000.0 * sampleRate);
        var pcm = new short[totalFrames];

        foreach (var t in ImpulseMs)
        {
            var start = (int)Math.Round(t / 1000.0 * sampleRate);
            var count = (int)Math.Round(BurstMs / 1000.0 * sampleRate);
            for (var i = 0; i < count && start + i < totalFrames; i++)
                pcm[start + i] = (short)(short.MaxValue * Math.Sin(2 * Math.PI * 1000.0 * i / sampleRate));
        }

        var dataBytes = pcm.Length * sizeof(short);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var bw = new BinaryWriter(fs);
        bw.Write("RIFF"u8.ToArray());
        bw.Write(36 + dataBytes);
        bw.Write("WAVE"u8.ToArray());
        bw.Write("fmt "u8.ToArray());
        bw.Write(16);
        bw.Write((short)1);          // PCM
        bw.Write((short)1);          // mono
        bw.Write(sampleRate);
        bw.Write(sampleRate * 2);    // byte rate
        bw.Write((short)2);          // block align
        bw.Write((short)16);
        bw.Write("data"u8.ToArray());
        bw.Write(dataBytes);
        var bytes = new byte[dataBytes];
        Buffer.BlockCopy(pcm, 0, bytes, 0, dataBytes);
        bw.Write(bytes);
    }
}
