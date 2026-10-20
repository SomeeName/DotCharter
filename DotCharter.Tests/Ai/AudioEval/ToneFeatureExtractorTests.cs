using DotCharter.Services.Ai.Match;
using DotCharter.Services.Ai.Match.Similarity;

namespace DotCharter.Tests.Ai.AudioEval;

/// <summary>
///     <c>tonefx-v1</c>'s own contract: a fixed length, a stable id, the same answer
///     every time, finite values on degenerate input, and a rate that does not change
///     the answer.
/// </summary>
public sealed class ToneFeatureExtractorTests
{
    private static readonly ToneFeatureExtractor Extractor = new();

    [Fact]
    public void Vector_HasTheDeclaredLengthAndId()
    {
        var features = Extractor.Extract(ReferenceDi.Generate());

        Assert.Equal(ToneFeatureExtractor.ExtractorId, features.ExtractorId);
        Assert.Equal(ToneFeatureLayout.Length, features.Vector.Length);

        // The layout's blocks must tile the vector exactly: a gap is a feature nobody
        // weights and an overlap is one counted twice.
        var covered = 0;
        var at = 0;
        foreach (var block in ToneFeatureLayout.Blocks)
        {
            Assert.Equal(at, block.Offset);
            at += block.Count;
            covered += block.Count;
        }

        Assert.Equal(ToneFeatureLayout.Length, covered);
    }

    [Fact]
    public void SameClip_ProducesBitIdenticalVectors()
    {
        var clip = ReferenceDi.Generate();

        var a = Extractor.Extract(clip);
        var b = Extractor.Extract(clip);

        Assert.Equal(a.Vector, b.Vector);
    }

    [Fact]
    public void EverythingIsFinite_ForSilenceAndForAClipShorterThanTheLongestWindow()
    {
        var silence = new MonoAudio(new float[48_000], 48_000);
        var tiny = new MonoAudio(new float[64], 48_000);

        foreach (var features in new[] { Extractor.Extract(silence), Extractor.Extract(tiny) })
        {
            Assert.Equal(ToneFeatureLayout.Length, features.Vector.Length);
            Assert.All(features.Vector, v => Assert.True(float.IsFinite(v), "a feature was not finite"));
        }
    }

    /// <summary>
    ///     The same performance delivered at 44.1 kHz must still read as the same
    ///     tone. It cannot be bit-identical — resampling is not lossless — and the
    ///     margin here is deliberately modest because the MEASURED cost is not small:
    ///     a 48 → 44.1 → 48 kHz round trip lands at roughly 0.6× a clean-amp-vs-fuzz
    ///     distance, almost all of it in the distortion block, which reads the 2–6 kHz
    ///     band the resampler's passband ripple sits in.
    ///     <para>
    ///         <b>So the production path must never resample, and it does not:</b> the
    ///         stem cache decodes at 48 kHz and <see cref="RigRenderer.RenderRate" /> is
    ///         48 kHz, so both sides of every real comparison are native. This test
    ///         exists to keep that cost BOUNDED and visible, not to claim it is zero.
    ///     </para>
    /// </summary>
    [Fact]
    public void SampleRate_IsNotPartOfTheAnswer()
    {
        var at48 = ReferenceDi.Generate();
        var at441 = MonoResampler.To(at48, 44_100);
        var similarity = new ToneSimilarity(Extractor);

        var rateDistance = similarity.Distance(Extractor.Extract(at48), Extractor.Extract(at441));
        var toneDistance = similarity.Distance(
            Extractor.Extract(ManagedToneStages.Clean(at48)),
            Extractor.Extract(ManagedToneStages.Fuzz(at48)));

        Assert.True(rateDistance < toneDistance * 0.8,
            $"a 48k/44.1k rate change moved the vector by {rateDistance:F4}, which is no longer clearly " +
            $"smaller than a clean-vs-fuzz distance of {toneDistance:F4} — at that point a comparison " +
            "against a stem at another rate is partly a measurement of the rate.");
    }

    /// <summary>
    ///     The frame gate is load-bearing, so it gets its own case: padding a clip
    ///     with silence must not change what it says about the tone. Without the gate
    ///     the silent frames' noise floor is averaged into every band and a sparse
    ///     riff reads as darker and more compressed than a busy one — which is the
    ///     single largest source of content dependence this extractor has.
    /// </summary>
    [Fact]
    public void LeadingAndTrailingSilence_DoesNotChangeTheTone()
    {
        var clip = ManagedToneStages.Clean(ReferenceDi.Generate());
        var padded = new float[clip.Samples.Length + 96_000];
        Array.Copy(clip.Samples, 0, padded, 48_000, clip.Samples.Length);

        var similarity = new ToneSimilarity(Extractor);
        var padDistance = similarity.Distance(
            Extractor.Extract(clip), Extractor.Extract(new MonoAudio(padded, clip.SampleRate)));
        var toneDistance = similarity.Distance(
            Extractor.Extract(clip), Extractor.Extract(ManagedToneStages.Fuzz(ReferenceDi.Generate())));

        Assert.True(padDistance < toneDistance / 5,
            $"two seconds of silence moved the tone by {padDistance:F4} against a real tone change of " +
            $"{toneDistance:F4} — the frame gate is not doing its job.");
    }
}
