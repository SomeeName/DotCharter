using JuceSharp.StemSeparation.Onnx;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
/// P6. The htdemucs ONNX driver allocated every one of its per-chunk buffers
/// inside the chunk loop — ~375 MB per chunk, essentially all Large Object Heap.
/// With a ≈6.75 s stride a 3:30 song is ~32 chunks, so ~12 GB of LOH churn per
/// separation, forcing repeated gen-2 compactions during a run that is already
/// competing with the audio callback. The class doc asserted the buffers were
/// rented from <c>ArrayPool&lt;T&gt;.Shared</c>; a grep for <c>ArrayPool</c> over
/// the whole project returned exactly one hit — that <c>see cref</c>.
/// </summary>
public sealed class HtdemucsChunkScratchTests
{
    // Real model shapes (6-stem htdemucs), scaled down where the axis does not
    // matter to the identity/shape claims. Segment/timeFrames are kept small so
    // the test does not allocate hundreds of MB itself.
    private const int Stems = 6;
    private const int Segment = 1024;
    private const int TimeFrames = 8;
    private const int FreqBins = 16;
    private const int Bins = FreqBins + 1; // the model drops Nyquist, the STFT keeps it

    // The spectrogram geometry is derived from the TRANSFORM since JuceSharp
    // 0.8.x: SpectrumPlanes sizes itself nFft/2 + 1 and the `bins` argument no
    // longer decides anything. Pick an nFft that reproduces `Bins` so the shape
    // claims below still assert what they always did.
    private const int NFft = (Bins - 1) * 2; // 32 -> 17 bins
    private const int Hop = 16;

    // The 6-argument constructor. JuceSharp still ships the 7-argument one that
    // took `Bins`, but it is a forwarder that IGNORES the argument, and its own
    // doc names THIS test as the last caller in the whole DotCharter tree — the
    // one thing standing between that overload and an [Obsolete]. `Bins` above
    // is still what the shape claims assert against; it is just no longer passed
    // in as though it decided anything.
    private static HtdemucsChunkScratch New() =>
        new(Stems, Segment, TimeFrames, FreqBins, NFft, Hop);

    /// <summary>
    /// THE POINT: every chunk gets the SAME buffers back. If any of these
    /// stopped being reference-equal across chunks, the LOH churn is back.
    /// </summary>
    [Fact]
    public void EveryBufferIsAllocatedOnceAndHandedBackAgain()
    {
        var s = New();

        var left = s.Left;
        var right = s.Right;
        var interleaved0 = s.Interleaved[0];
        var specL0 = s.SpecL[0];
        var specL0Re0 = s.SpecL[0].Re[0];
        var specL0Im0 = s.SpecL[0].Im[0];
        var specR3 = s.SpecR[3];
        var inL = s.InL;
        var work0 = s.Work[0];
        var specOut = s.SpecOut(Stems * 4 * FreqBins * TimeFrames);
        var waveOut = s.WaveOut(Stems * 2 * Segment);

        // "Run" 32 more chunks.
        for (var chunk = 0; chunk < 32; chunk++)
        {
            Assert.Same(left, s.Left);
            Assert.Same(right, s.Right);
            Assert.Same(interleaved0, s.Interleaved[0]);
            Assert.Same(specL0, s.SpecL[0]);
            Assert.Same(specL0Re0, s.SpecL[0].Re[0]);
            Assert.Same(specL0Im0, s.SpecL[0].Im[0]);
            Assert.Same(specR3, s.SpecR[3]);
            Assert.Same(inL, s.InL);
            Assert.Same(work0, s.Work[0]);
            Assert.Same(specOut, s.SpecOut(Stems * 4 * FreqBins * TimeFrames));
            Assert.Same(waveOut, s.WaveOut(Stems * 2 * Segment));
        }
    }

    [Fact]
    public void TheShapesMatchTheModelContract()
    {
        var s = New();

        Assert.Equal(Stems, s.Stems);
        Assert.Equal(Segment, s.Left.Length);
        Assert.Equal(Segment, s.Right.Length);
        Assert.Equal(Stems, s.Interleaved.Length);
        Assert.Equal(Stems, s.SpecL.Length);
        Assert.Equal(Stems, s.SpecR.Length);

        for (var i = 0; i < Stems; i++)
        {
            Assert.Equal(Segment * 2, s.Interleaved[i].Length);
            Assert.Equal(TimeFrames, s.SpecL[i].FrameCount);
            Assert.Equal(TimeFrames, s.SpecR[i].FrameCount);
            Assert.Equal(Bins, s.SpecL[i].BinCount);
            Assert.Equal(Bins, s.SpecR[i].BinCount);

            // Re and Im are SEPARATE float planes now — the pair replaces one
            // Complex[] and is half the bytes. Both must be fully rowed out, or
            // the driver writes past the end of one of them.
            foreach (var row in s.SpecL[i].Re) Assert.Equal(Bins, row.Length);
            foreach (var row in s.SpecL[i].Im) Assert.Equal(Bins, row.Length);
            foreach (var row in s.SpecR[i].Re) Assert.Equal(Bins, row.Length);
            foreach (var row in s.SpecR[i].Im) Assert.Equal(Bins, row.Length);

            // The reconstruction planes must know the signal they rebuild.
            Assert.Equal(Segment, s.SpecL[i].OriginalLength);
            Assert.Equal(Segment, s.SpecR[i].OriginalLength);
        }
    }

    /// <summary>
    /// Per-stem slots must be distinct objects — the driver fills them from a
    /// <c>Parallel.For</c> over stems, so sharing one would be a data race
    /// producing silently wrong stems rather than a crash.
    /// </summary>
    [Fact]
    public void PerStemSlotsAreIndependent()
    {
        var s = New();
        for (var a = 0; a < Stems; a++)
        {
            for (var b = a + 1; b < Stems; b++)
            {
                Assert.NotSame(s.Interleaved[a], s.Interleaved[b]);
                Assert.NotSame(s.SpecL[a], s.SpecL[b]);
                Assert.NotSame(s.SpecR[a], s.SpecR[b]);
                Assert.NotSame(s.SpecL[a], s.SpecR[a]);

                // The re/im planes are what the Parallel.For actually writes, so the
                // independence claim has to hold one level down as well.
                Assert.NotSame(s.SpecL[a].Re, s.SpecL[b].Re);
                Assert.NotSame(s.SpecL[a].Re, s.SpecL[a].Im);

                // ...and each stem owns its transform workspace, which is what lets
                // the inverse run with no allocation at all.
                Assert.NotSame(s.Work[a], s.Work[b]);
            }
        }
    }

    /// <summary>
    /// The reused spectrogram rows carry one piece of state ACROSS chunks: the
    /// driver writes bins 0..FreqBins-1 and leaves the Nyquist bin zero, which
    /// is what the model contract requires. Allocation gives us that zero, and
    /// nothing may ever write it.
    /// </summary>
    [Fact]
    public void TheNyquistBinStartsAtZeroSoReuseIsSafe()
    {
        var s = New();
        foreach (var stem in s.SpecL)
        {
            for (var f = 0; f < stem.FrameCount; f++)
            {
                Assert.Equal(0f, stem.Re[f][FreqBins]);
                Assert.Equal(0f, stem.Im[f][FreqBins]);
            }
        }
    }

    /// <summary>Grows only when the model's real output exceeds what we sized from.</summary>
    [Fact]
    public void TheOrtCopyBuffersGrowOnlyWhenTheyMustAndThenStayGrown()
    {
        var s = New();
        var sized = Stems * 4 * FreqBins * TimeFrames;

        var initial = s.SpecOut(sized);
        Assert.Same(initial, s.SpecOut(sized - 100));   // smaller: same buffer

        var grown = s.SpecOut(sized + 1);
        Assert.NotSame(initial, grown);
        Assert.Same(grown, s.SpecOut(sized + 1));       // and it sticks
        Assert.Same(grown, s.SpecOut(sized));
    }

    /// <summary>
    /// The guard for the class doc: nothing in the chunk loop may allocate
    /// again. Asserted on the source because the driver cannot be constructed
    /// without a real ONNX session and a real model file.
    /// </summary>
    [Fact]
    public void TheChunkLoopMethodsAllocateNothing()
    {
        var src = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(),
            "JuceSharp", "src", "JuceSharp.StemSeparation.Onnx", "HtdemucsOnnxInference.cs"));

        foreach (var signature in new[]
                 {
                     "private void ComputeAndPackSpec(",
                     "private (float[] SpecOut, int[] SpecShape, float[] WaveOut, int[] WaveShape) RunModel(",
                     "private void BuildStemChunk(",
                     "private void BuildWaveOnlyStemChunk(",
                 })
        {
            var body = StripLineComments(MethodBody(src, signature));
            Assert.DoesNotContain("new float[", body, StringComparison.Ordinal);
            Assert.DoesNotContain(".ToArray()", body, StringComparison.Ordinal);

            // `new Complex[` used to be the expensive one. Complex is gone from
            // this project entirely, so that assertion would now pass over any
            // source at all: the two types that replaced it are what the loop
            // must not build.
            Assert.DoesNotContain("new SpectrumPlanes(", body, StringComparison.Ordinal);
            Assert.DoesNotContain("new StftWorkspace(", body, StringComparison.Ordinal);
        }
    }

    /// <summary>Drops `//` comments so a comment that merely names an
    /// allocation (the ones explaining why it is gone) cannot fail the scan.</summary>
    private static string StripLineComments(string src)
    {
        var sb = new System.Text.StringBuilder(src.Length);
        foreach (var line in src.Split('\n'))
        {
            var idx = line.IndexOf("//", StringComparison.Ordinal);
            sb.Append(idx >= 0 ? line[..idx] : line).Append('\n');
        }
        return sb.ToString();
    }

    private static string MethodBody(string src, string signature)
    {
        var start = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find '{signature}' — has it been renamed?");
        var open = src.IndexOf('{', start + signature.Length);
        var depth = 0;
        var i = open;
        for (; i < src.Length; i++)
        {
            if (src[i] == '{') depth++;
            else if (src[i] == '}' && --depth == 0) break;
        }
        return src[open..Math.Min(i + 1, src.Length)];
    }
}
