using System.Reflection;
using DotCharter.Services.Audio.LivePlay;
using JuceSharp.Dsp;

namespace DotCharter.Tests.Services.Audio.LivePlay;

/// <summary>
/// The live-detection configuration is measured by <c>CqtStreamingEvalTests</c> in
/// JuceSharp.Tests, which cannot reference this project — so both sides read
/// <see cref="LiveCqtConfig"/>, and both sides pin themselves to it in the gate. This is
/// the DotCharter half.
///
/// <para><b>Why it exists.</b> The eval harness used to hand-copy these numbers and fell
/// behind on two of them: it defaulted the sub-octave veto OFF and the noise horizon to the
/// bare 2.23 s frame ring, months after both had been adopted for live play. The harness is
/// <c>Assert.Skip</c>-gated on a dataset, so nothing ever ran it and nothing ever noticed.
/// These assertions need no dataset and no audio.</para>
/// </summary>
public sealed class LiveCqtConfigTests
{
    [Fact]
    public void LiveCqtVerifierConstants_AreTheSharedConfig()
    {
        Assert.Equal(LiveCqtConfig.MinMidi, LiveCqtVerifier.LiveMinMidi);
        Assert.Equal(LiveCqtConfig.BinCount, LiveCqtVerifier.LiveBinCount);
        Assert.Equal(LiveCqtConfig.Hop, LiveCqtVerifier.LiveHop);
        Assert.Equal(LiveCqtConfig.MaxKernelSamples, LiveCqtVerifier.LiveMaxKernelSamples);
        // Live play gates over the streaming idle floor, at its own margin; NoiseMargin is the
        // offline chart lint's.
        Assert.Equal(LiveCqtConfig.StreamingNoiseMargin, LiveCqtVerifier.LiveNoiseMargin);
        Assert.Equal(LiveCqtConfig.SubOctaveVeto, LiveCqtVerifier.LiveSubOctaveVeto);
        Assert.Equal(LiveCqtConfig.NoiseHistorySeconds, LiveCqtVerifier.LiveNoiseHistorySeconds);
    }

    /// <summary>The values are only worth anything if <see cref="LiveCqtVerifier.CreateStreaming"/>
    /// actually passes them — the drift that mattered was a knob that was never wired, not a
    /// constant that was wrong.</summary>
    [Theory]
    [InlineData(44100)]
    [InlineData(48000)]
    public void CreateStreaming_WiresEveryConfiguredValueIntoTheCqt(int sampleRate)
    {
        var verifier = LiveCqtVerifier.CreateStreaming(sampleRate);
        var cqt = CqtOf(verifier);

        Assert.Equal(sampleRate, cqt.SampleRate);
        Assert.Equal(LiveCqtConfig.Hop, cqt.Hop);
        Assert.Equal(LiveCqtConfig.MinMidi, cqt.FMinMidi);
        Assert.Equal(LiveCqtConfig.BinCount, cqt.BinCount);
        Assert.Equal(LiveCqtConfig.MaxKernelSamples, cqt.MaxKernelSamples);
        Assert.Equal(LiveCqtConfig.FloorPercentile, cqt.FloorPercentile);
        Assert.Equal(LiveCqtConfig.NoiseHistoryFrames(sampleRate), cqt.NoiseHistoryFrames);
        // 10 s, not the 384-frame (2.23 s) ring the harness used to assume.
        Assert.True(cqt.NoiseHistoryFrames > 384);

        // Without these the sub-octave veto cannot fire for MIDI 28..39 — the bass low E
        // string, i.e. the register LiveMinMidi was lowered to E1 for.
        Assert.Equal(LiveCqtConfig.SubOctaveBins, cqt.SubOctaveBins);

        // The idle floor the presence gate compares against — without it the gate's floor
        // rises to the playing level during any riff that does not pause.
        Assert.Equal(LiveCqtConfig.IdleSteadyFrames(sampleRate), cqt.IdleSteadyFrames);
        Assert.True(cqt.IdleSteadyFrames > 0);
        Assert.Equal(LiveCqtConfig.IdleSteadyRatio, cqt.IdleSteadyRatio);
        Assert.Equal(LiveCqtConfig.IdleFloorRiseDbPerSecond, cqt.IdleFloorRiseDbPerSecond);
        Assert.True(cqt.LowestBin <= cqt.BinForMidi(LiveCqtConfig.MinMidi) - 12 * cqt.Step,
            "the veto's octave-below bin is not addressable at the bottom of the charted range");
    }

    /// <summary>The offline chart lint mirrors the same canon (DotCharter.Analysis cannot
    /// reference Services either). Verified through the CQT it builds.</summary>
    [Fact]
    public void ShippingConfig_HoldsTheAdoptedAndRejectedSettings()
    {
        Assert.Equal(0.5, LiveCqtConfig.SubOctaveVeto);          // adopted
        Assert.Equal(10.0, LiveCqtConfig.NoiseHistorySeconds);   // adopted
        Assert.Equal(1.0f, LiveCqtConfig.FloorPercentile);       // rejected ⇒ rolling max
        Assert.Equal(0.0, LiveCqtConfig.HarmonicPeakBelowHz);    // rejected
        Assert.Equal(0, LiveCqtConfig.HarmonicComb);             // rejected
    }

    // The verifier owns its CQT privately (nothing in production needs it back); reading it
    // here is a test-only wiring probe.
    private static JuceConstantQ CqtOf(LiveCqtVerifier verifier)
    {
        var field = typeof(LiveCqtVerifier).GetField("_cqt", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<JuceConstantQ>(field!.GetValue(verifier));
    }
}
