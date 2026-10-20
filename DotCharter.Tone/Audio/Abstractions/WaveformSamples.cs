namespace DotCharter.Tone.Audio.Abstractions;

/// <summary>
/// Mipmap pyramid of peak amplitudes used by the timeline waveform renderer.
/// <para>
/// Level 0 is the base resolution (a small fixed number of audio samples per
/// block). Each subsequent level is half the length of the previous and
/// stores the max of each adjacent pair. Renderers pick the level whose block
/// width best matches "≥ 1 pixel" so the on-screen detail scales smoothly
/// with zoom.
/// </para>
/// <para>
/// <see cref="BodyMipmaps"/> is the SECOND pyramid over the exact same base
/// array: level k holds the MEAN of the base blocks level k of
/// <see cref="Mipmaps"/> takes the MAX of. Max alone saturates — over a
/// 100 ms span almost every bar of music touches its loudest sample, so a
/// zoomed-out peak-only waveform is a featureless slab. The mean is the
/// loudness body a DAW draws under the peak envelope: peaks show the
/// transients, the body shows how loud the passage actually is, and the gap
/// between them is the crest factor. It costs nothing on disk (both pyramids
/// are derived from the one persisted base level) and nothing per frame (the
/// renderer indexes it exactly like the peak level it already reads).
/// </para>
/// <para>All values are normalized 0..1, and <c>Body[l][i] &lt;= Mipmaps[l][i]</c>
/// by construction (a mean never exceeds the max of the same span).</para>
/// <para>
/// <b>THE INDEX↔TIME CONTRACT: block <c>i</c> of level <c>l</c> starts at
/// <c>i × BaseBlockMs × 2^l</c>, exactly.</b> That multiplication is the ONLY
/// way any consumer turns a block index back into a time — the renderer
/// (<c>WaveformLayer</c>), the lyric onset snap (<c>WaveformOnsetSnapper</c>)
/// and the minimap's strip length all do it — so a base block that is not
/// really <see cref="BaseBlockMs" /> wide is not a rounding detail, it is a
/// drift that grows for the whole length of the song. The native generator
/// therefore bins on FRACTIONAL sample boundaries; rounding
/// samples-per-block to an integer would be wrong: 2 ms at 44.1 kHz is 88.2 samples, so
/// every block would really be 1.99546 ms and the drawn waveform would run 0.227 % late
/// (+408 ms three minutes in — visibly right of its own audio, while 48 kHz's
/// exact 96 samples hides it). Pinned end-to-end by
/// <c>WaveformGenerationAlignmentTests</c>.
/// </para>
/// </summary>
public sealed record WaveformSamples(float[][] Mipmaps, double BaseBlockMs, float[][]? BodyMipmaps = null)
{
    /// <summary>
    ///     The base block width EVERY live generation uses — i.e. the finest
    ///     detail the timeline waveform can ever show, and the reason it is
    ///     this small.
    ///     <para>Level 0 is 2 ms, because 20 ms is the whole "the waveform
    ///     looks low-res" report: level 0 is the finest level the LOD picker
    ///     can reach, so at 1 px/ms zoom the waveform is a staircase with 20 px
    ///     treads and at 2 px/ms it is 40 px treads — no amount of renderer
    ///     work recovers detail the pyramid never stored. At 2 ms a tread is
    ///     2–4 px across the whole practical zoom range, which is below the
    ///     threshold where a step reads as a step, and a 2 ms window still
    ///     resolves a pick attack (rise time ~1–5 ms).</para>
    ///     <para>Cost is linear and small: ~1.2 MB of base peaks for a 10-minute
    ///     song (was ~120 KB) on disk and roughly twice that in memory for both
    ///     pyramids. Generation time is unchanged — it is dominated by the
    ///     decode, not by the binning.</para>
    ///     <para><b>Changing this value invalidates the disk cache.</b> That is
    ///     enforced, not remembered: <c>WaveformDiskCache</c> refuses an entry
    ///     whose stored block width is not this one (a miss, so the next open
    ///     regenerates). Never read a cached <c>BaseBlockMs</c> as authoritative
    ///     for the current generator.</para>
    /// </summary>
    public const double GenerationBlockMs = 2.0;

    public static WaveformSamples Empty { get; } = new([], 0);

    public bool HasData => Mipmaps.Length > 0 && Mipmaps[0].Length > 0;

    /// <summary>The loudness-body pyramid, falling back to the peak pyramid
    /// for a hand-constructed <see cref="WaveformSamples"/> that has none (the
    /// body then equals the peak, which is what level 0 holds anyway — a
    /// degraded look, never a crash or an out-of-range index).</summary>
    public float[][] Body => BodyMipmaps ?? Mipmaps;

    /// <summary>
    ///     Builds both pyramids from base-level peaks (halving until a level is
    ///     ≤32 blocks: max-of-pair for <see cref="Mipmaps"/>, mean-of-pair for
    ///     <see cref="Body"/>). The single derivation used by both live
    ///     generation and the disk cache — only level 0 is ever persisted, so
    ///     the two paths can never disagree.
    /// </summary>
    public static WaveformSamples FromBasePeaks(float[] basePeaks, double baseBlockMs)
    {
        if (basePeaks.Length == 0 || baseBlockMs <= 0) return Empty;

        // Level 0 is shared by reference between the two pyramids: over ONE
        // base block the max and the mean are the same number, so allocating a
        // copy would only cost memory.
        var levels = new List<float[]> { basePeaks };
        var bodyLevels = new List<float[]> { basePeaks };
        var cur = basePeaks;
        var curBody = basePeaks;
        while (cur.Length > 32)
        {
            var next = new float[(cur.Length + 1) / 2];
            var nextBody = new float[next.Length];
            for (var i = 0; i < next.Length; i++)
            {
                var hasPair = 2 * i + 1 < cur.Length;

                var a = cur[2 * i];
                var b = hasPair ? cur[2 * i + 1] : a;
                next[i] = Math.Max(a, b);

                // The mean pyramid halves its OWN previous level, so level k is
                // the arithmetic mean over the same 2^k base blocks level k of
                // the peak pyramid maxes over. The odd tail carries (mean of one
                // value = that value), matching the peak pyramid's tail rule so
                // both stay the same length.
                var ba = curBody[2 * i];
                var bb = hasPair ? curBody[2 * i + 1] : ba;
                nextBody[i] = 0.5f * (ba + bb);
            }

            levels.Add(next);
            bodyLevels.Add(nextBody);
            cur = next;
            curBody = nextBody;
        }

        return new WaveformSamples(levels.ToArray(), baseBlockMs, bodyLevels.ToArray());
    }

    /// <summary>Returns the mipmap level whose block is at least <paramref name="targetBlockMs"/> wide.</summary>
    public int PickLevel(double targetBlockMs)
    {
        var level = 0;
        while (level < Mipmaps.Length - 1 && BaseBlockMs * (1 << level) < targetBlockMs)
            level++;
        return level;
    }

    /// <summary>
    ///     Returns the level whose block width is CLOSEST (in log2 distance) to
    ///     <paramref name="targetBlockMs" /> — the level a consumer that analyses
    ///     at a fixed time resolution wants, as opposed to
    ///     <see cref="PickLevel" />'s "at least this wide" pixel-matching rule.
    ///     <para>It exists so an analyser's window stays pinned to the resolution
    ///     it was tuned for even when the pyramid's BASE block changes: a
    ///     20 ms-base pyramid answers level 0 for a 20 ms target, and so does a
    ///     finer-based one (with its nearest level) — never "the base level,
    ///     whatever that happens to be now".</para>
    /// </summary>
    public int PickLevelNearest(double targetBlockMs)
    {
        if (Mipmaps.Length <= 1 || targetBlockMs <= 0 || BaseBlockMs <= 0) return 0;

        var best = 0;
        var bestDistance = double.MaxValue;
        for (var level = 0; level < Mipmaps.Length; level++)
        {
            // Compare in log space: 16 ms and 32 ms are equally far from 20 ms
            // in absolute terms but the octave below is the closer match.
            var distance = Math.Abs(Math.Log2(BaseBlockMs * (1 << level) / targetBlockMs));
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = level;
        }

        return best;
    }
}
