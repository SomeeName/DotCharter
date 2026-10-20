namespace DotCharter.Services.Export.Audio;

/// <summary>
///     Wwise's IMA-ADPCM variant: the codec behind a <c>fmt </c> tag of 2 in a
///     <c>.wem</c>. Pure and allocation-light; no I/O.
/// </summary>
/// <remarks>
///     Layout, per vgmstream's <c>decode_wwise_ima</c>:
///     <list type="bullet">
///         <item>
///             Fixed <see cref="BlockBytes" />-byte blocks, one per channel,
///             <b>interleaved by block</b>: <c>[ch0 36B][ch1 36B][ch0 36B]…</c>.
///         </item>
///         <item>
///             Block header: <c>int16</c> initial sample, <c>uint8</c> step index,
///             <c>uint8</c> reserved. Then 32 bytes of 4-bit codes.
///         </item>
///         <item>
///             <see cref="SamplesPerBlock" /> samples come out per block: the
///             header sample is sample 0, then 63 codes. The 64th code is
///             <b>skipped</b> by the decoder — we emit a zero there.
///         </item>
///     </list>
///     The step delta is the "mul" flavour — <c>((code &amp; 7) * 2 + 1) * step &gt;&gt; 3</c>,
///     multiplying <em>before</em> the shift. That is NOT the same as textbook IMA's
///     accumulate-then-shift: they diverge on truncation (step 7, code 1 gives 2
///     here and 1 there), so a textbook IMA decoder will drift on this data.
///     <para>
///         <b>Unverified against the game.</b> <see cref="Encode" /> and
///         <see cref="Decode" /> are exact inverses of each other, which the tests
///         pin — but that only proves self-consistency. Nibble ORDER within a byte
///         (low first, as here) is the one field a round-trip cannot validate: if
///         Wwise packs high-first, the game would decode noise while our own tests
///         stay green. Prefer <see cref="WemCodec.Pcm" /> (which has no such
///         ambiguity) until a package has actually been loaded by Rocksmith 2014.
///     </para>
/// </remarks>
internal static class WwiseImaAdpcm
{
    /// <summary>Bytes per block, per channel.</summary>
    public const int BlockBytes = 0x24; // 36

    /// <summary>Samples decoded per block, per channel.</summary>
    public const int SamplesPerBlock = 64;

    private const int HeaderBytes = 4;

    private static readonly int[] StepTable =
    [
        7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45,
        50, 55, 60, 66, 73, 80, 88, 97, 107, 118, 130, 143, 157, 173, 190, 209, 230, 253,
        279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963, 1060, 1166,
        1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026,
        4428, 4871, 5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899,
        15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767
    ];

    private static readonly int[] IndexTable = [-1, -1, -1, -1, 2, 4, 6, 8];

    /// <summary>Number of whole blocks needed to hold <paramref name="frameCount" /> frames.</summary>
    public static int BlockCount(int frameCount) =>
        (frameCount + SamplesPerBlock - 1) / SamplesPerBlock;

    /// <summary>Total encoded byte length for the given frame count and channel count.</summary>
    public static int EncodedLength(int frameCount, int channels) =>
        BlockCount(frameCount) * BlockBytes * channels;

    /// <summary>
    ///     Encodes interleaved 16-bit PCM into Wwise IMA-ADPCM. The tail of the
    ///     final block is padded with the last sample (not silence — a jump to
    ///     zero would click).
    /// </summary>
    public static byte[] Encode(ReadOnlySpan<short> interleaved, int channels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        if (interleaved.Length % channels != 0)
            throw new ArgumentException("PCM length is not a whole number of frames.", nameof(interleaved));

        var frames = interleaved.Length / channels;
        var blocks = BlockCount(frames);
        var output = new byte[blocks * BlockBytes * channels];

        // Adaptation state is carried ACROSS blocks per channel: each block stores
        // the index it starts from, so the predictor keeps converging instead of
        // resetting to a cold step every 64 samples.
        var stepIndex = new int[channels];

        for (var block = 0; block < blocks; block++)
        {
            for (var ch = 0; ch < channels; ch++)
            {
                var offset = (block * channels + ch) * BlockBytes;
                var firstFrame = block * SamplesPerBlock;

                var predictor = SampleAt(interleaved, frames, channels, firstFrame, ch);
                var index = stepIndex[ch];

                output[offset + 0] = (byte)(predictor & 0xFF);
                output[offset + 1] = (byte)((predictor >> 8) & 0xFF);
                output[offset + 2] = (byte)index;
                output[offset + 3] = 0;

                // 63 coded samples; the 64th code is skipped on decode, so leave it 0.
                for (var i = 1; i < SamplesPerBlock; i++)
                {
                    var target = SampleAt(interleaved, frames, channels, firstFrame + i, ch);
                    var code = EncodeSample(target, ref predictor, ref index);

                    var codeIndex = i - 1;
                    var byteOffset = offset + HeaderBytes + (codeIndex >> 1);
                    // Low nibble holds the earlier sample.
                    if ((codeIndex & 1) == 0) output[byteOffset] = (byte)code;
                    else output[byteOffset] |= (byte)(code << 4);
                }

                stepIndex[ch] = index;
            }
        }

        return output;
    }

    /// <summary>Decodes Wwise IMA-ADPCM back to interleaved 16-bit PCM.</summary>
    public static short[] Decode(ReadOnlySpan<byte> data, int channels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        var blockStride = BlockBytes * channels;
        var blocks = data.Length / blockStride;
        var output = new short[blocks * SamplesPerBlock * channels];

        for (var block = 0; block < blocks; block++)
        {
            for (var ch = 0; ch < channels; ch++)
            {
                var offset = (block * channels + ch) * BlockBytes;

                var predictor = (short)(data[offset] | (data[offset + 1] << 8));
                var index = Math.Clamp(data[offset + 2], 0, StepTable.Length - 1);

                var baseFrame = block * SamplesPerBlock;
                output[(baseFrame * channels) + ch] = predictor;

                var running = (int)predictor;
                for (var i = 1; i < SamplesPerBlock; i++)
                {
                    var codeIndex = i - 1;
                    var b = data[offset + HeaderBytes + (codeIndex >> 1)];
                    var code = (codeIndex & 1) == 0 ? b & 0x0F : (b >> 4) & 0x0F;

                    DecodeSample(code, ref running, ref index);
                    output[((baseFrame + i) * channels) + ch] = (short)running;
                }
            }
        }

        return output;
    }

    /// <summary>
    ///     Quantizes one sample against the running predictor, advancing both the
    ///     predictor and the step index EXACTLY as the decoder will — anything else
    ///     and the two drift apart within a block.
    /// </summary>
    private static int EncodeSample(short target, ref short predictor, ref int index)
    {
        var step = StepTable[index];
        var diff = target - predictor;

        var code = 0;
        if (diff < 0)
        {
            code = 8;
            diff = -diff;
        }

        // Invert delta = ((2n+1) * step) >> 3 for n, then clamp into the 3-bit range.
        var magnitude = Math.Clamp((((diff << 3) / step) - 1) / 2, 0, 7);
        code |= magnitude;

        var running = (int)predictor;
        DecodeSample(code, ref running, ref index);
        predictor = (short)running;
        return code;
    }

    private static void DecodeSample(int code, ref int predictor, ref int index)
    {
        var step = StepTable[index];

        var delta = ((code & 7) * 2 + 1) * step >> 3;
        if ((code & 8) != 0) delta = -delta;

        predictor = Math.Clamp(predictor + delta, short.MinValue, short.MaxValue);
        index = Math.Clamp(index + IndexTable[code & 7], 0, StepTable.Length - 1);
    }

    /// <summary>Reads a sample, clamping past the end to the last real frame so the
    /// final partial block pads with a held value rather than a click to zero.</summary>
    private static short SampleAt(ReadOnlySpan<short> interleaved, int frames, int channels, int frame, int ch)
    {
        if (frames == 0) return 0;
        var f = Math.Min(frame, frames - 1);
        return interleaved[(f * channels) + ch];
    }
}
