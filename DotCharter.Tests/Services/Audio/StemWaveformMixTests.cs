using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Services.Audio.Waveform;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
///     The waveform of what the MIXER is playing.
///     <para>
///         Owner request: "a toggle in the mixer to make the waveform reflect
///         the actual mixed waveform … when stems and only vocals not muted it
///         should only show that vocals, or when mixed vocals and bass it should
///         show that mixed waveform." Charting to a stem you cannot see is the
///         problem — separate out the guitar and the timeline still draws the
///         whole band's transients.
///     </para>
///     <para>
///         Summing the peaks is not a convenient approximation: source
///         separation is DEFINED by the stems summing back to the input, so at
///         unity gain the six htdemucs stems reconstruct the mix.
///     </para>
/// </summary>
public sealed class StemWaveformMixTests
{
    // ---- audibility ---------------------------------------------------------

    [Fact]
    public void EverythingUnmutedIsAudible()
    {
        var channels = new[] { Stem("vocals"), Stem("bass"), Stem("drums") };
        Assert.Equal(3, StemWaveformMix.Audible(channels).Count);
    }

    [Fact]
    public void AMutedChannelIsNot()
    {
        var channels = new[] { Stem("vocals"), Stem("bass", muted: true) };
        var audible = StemWaveformMix.Audible(channels);
        Assert.Equal("vocals", Assert.Single(audible).Name);
    }

    /// <summary>The reported case, exactly: only vocals unmuted.</summary>
    [Fact]
    public void OnlyVocalsUnmuted_LeavesOnlyVocals()
    {
        var channels = new[]
        {
            Stem("vocals"), Stem("bass", muted: true), Stem("drums", muted: true),
            Stem("guitar", muted: true), Stem("other", muted: true), Stem("piano", muted: true)
        };

        Assert.Equal("vocals", Assert.Single(StemWaveformMix.Audible(channels)).Name);
    }

    [Fact]
    public void VocalsAndBassUnmuted_LeavesBoth()
    {
        var channels = new[] { Stem("vocals"), Stem("bass"), Stem("drums", muted: true) };
        Assert.Equal(new[] { "vocals", "bass" }, StemWaveformMix.Audible(channels).Select(c => c.Name));
    }

    /// <summary>A solo silences everything that is not soloed.</summary>
    [Fact]
    public void ASoloSilencesEverythingUnsoloed()
    {
        var channels = new[] { Stem("vocals"), Stem("bass", soloed: true), Stem("drums") };
        Assert.Equal("bass", Assert.Single(StemWaveformMix.Audible(channels)).Name);
    }

    /// <summary>
    ///     …but a MUTED channel stays silent even when it is soloed, because that
    ///     is what the engine does.
    ///     <para>
    ///         This class and the type both used to say "solo wins over mute, the
    ///         way every mixer works", and about this app that was simply false:
    ///         <c>JuceAudioPlaybackService.ApplyStemGainsLocked</c> silences a stem
    ///         when <c>UserMuted || (anySolo &amp;&amp; !UserSoloed)</c> — mute
    ///         first. The drawing therefore showed a stem nothing was playing.
    ///     </para>
    ///     <para>
    ///         Reachable from a FILE rather than from the strips: the two setters
    ///         clear each other, but a <c>.rscp</c> stores <c>stem/@muted</c> and
    ///         <c>stem/@solo</c> independently and <c>BuildStrips</c> seeds the
    ///         strip from both without reconciling them.
    ///     </para>
    /// </summary>
    [Fact]
    public void AMutedChannelIsSilentEvenWhenItIsSoloed()
    {
        var channels = new[]
        {
            Stem("vocals", muted: true, soloed: true),
            Stem("bass", soloed: true),
            Stem("drums")
        };

        Assert.Equal("bass", Assert.Single(StemWaveformMix.Audible(channels)).Name);
    }

    /// <summary>The degenerate case of the same rule: the ONLY soloed channel is
    /// muted, so nothing is audible — not that one channel.</summary>
    [Fact]
    public void AMutedSoleSoloLeavesNothingAudible()
    {
        var channels = new[] { Stem("vocals", muted: true, soloed: true), Stem("bass") };

        Assert.Empty(StemWaveformMix.Audible(channels));
    }

    [Fact]
    public void AChannelAtZeroGainIsSilent()
    {
        var channels = new[] { Stem("vocals", gain: 0), Stem("bass") };
        Assert.Equal("bass", Assert.Single(StemWaveformMix.Audible(channels)).Name);
    }

    [Fact]
    public void EverythingMutedIsSilence() =>
        Assert.Empty(StemWaveformMix.Audible([Stem("vocals", muted: true), Stem("bass", muted: true)]));

    // ---- mixing -------------------------------------------------------------

    /// <summary>
    ///     The property the whole feature rests on: stems at unity sum back to
    ///     the mix they were separated from.
    /// </summary>
    [Fact]
    public void StemsAtUnitySumBackToTheMix()
    {
        var a = Wave(0.2f, 0.5f, 0.1f);
        var b = Wave(0.3f, 0.2f, 0.4f);

        var mixed = StemWaveformMix.Mix([(a, 1.0), (b, 1.0)]);

        Assert.Equal(0.5f, mixed.Mipmaps[0][0], 5);
        Assert.Equal(0.7f, mixed.Mipmaps[0][1], 5);
        Assert.Equal(0.5f, mixed.Mipmaps[0][2], 5);
    }

    [Fact]
    public void GainScalesAChannelsContribution()
    {
        var mixed = StemWaveformMix.Mix([(Wave(0.4f, 0.8f), 0.5)]);

        Assert.Equal(0.2f, mixed.Mipmaps[0][0], 5);
        Assert.Equal(0.4f, mixed.Mipmaps[0][1], 5);
    }

    /// <summary>A mixer that overdrives its master draws a flat top, which is
    /// what it sounds like.</summary>
    [Fact]
    public void TheSumIsClampedToFullScale()
    {
        var mixed = StemWaveformMix.Mix([(Wave(0.8f), 1.0), (Wave(0.9f), 1.0)]);
        Assert.Equal(1f, mixed.Mipmaps[0][0], 5);
    }

    [Fact]
    public void OneChannelAtUnityIsThatChannel()
    {
        var one = Wave(0.2f, 0.5f, 0.1f);
        var mixed = StemWaveformMix.Mix([(one, 1.0)]);
        Assert.Equal(one.Mipmaps[0], mixed.Mipmaps[0]);
    }

    /// <summary>Stems can differ in length by a block; the mix is as long as the
    /// longest and the short one simply stops contributing.</summary>
    [Fact]
    public void TheMixIsAsLongAsItsLongestSource()
    {
        var mixed = StemWaveformMix.Mix([(Wave(0.2f, 0.2f, 0.2f, 0.2f), 1.0), (Wave(0.1f, 0.1f), 1.0)]);

        Assert.Equal(4, mixed.Mipmaps[0].Length);
        Assert.Equal(0.3f, mixed.Mipmaps[0][0], 5);
        Assert.Equal(0.2f, mixed.Mipmaps[0][3], 5);
    }

    /// <summary>
    ///     A source generated at a different base block width is REFUSED rather
    ///     than resampled — the pyramid's own cache rule is that a differing
    ///     width is a miss, never something to reinterpret.
    /// </summary>
    [Fact]
    public void ASourceAtADifferentBlockWidthIsRefused()
    {
        var normal = Wave(0.4f, 0.4f);
        var foreign = WaveformSamples.FromBasePeaks([0.5f, 0.5f], WaveformSamples.GenerationBlockMs * 2);

        var mixed = StemWaveformMix.Mix([(normal, 1.0), (foreign, 1.0)]);

        Assert.Equal(0.4f, mixed.Mipmaps[0][0], 5);
        Assert.Equal(WaveformSamples.GenerationBlockMs, mixed.BaseBlockMs, 9);
    }

    [Fact]
    public void NothingAudibleDrawsNothing()
    {
        Assert.False(StemWaveformMix.Mix([]).HasData);
        Assert.False(StemWaveformMix.Mix([(Wave(0.5f), 0.0)]).HasData);
    }

    /// <summary>
    ///     The mix goes through <c>FromBasePeaks</c>, so it gets BOTH pyramids
    ///     and the same halving rule a generated waveform gets — the type's own
    ///     "single derivation" contract.
    /// </summary>
    [Fact]
    public void TheMixCarriesBothPyramids()
    {
        var mixed = StemWaveformMix.Mix([(Wave(Enumerable.Repeat(0.3f, 100).ToArray()), 1.0),
            (Wave(Enumerable.Repeat(0.2f, 100).ToArray()), 1.0)]);

        Assert.True(mixed.Mipmaps.Length > 1, "the mix has no pyramid above level 0");
        Assert.Equal(mixed.Mipmaps.Length, mixed.Body.Length);
        Assert.Equal(0.5f, mixed.Mipmaps[1][0], 5); // max-of-pair over two equal values
    }

    private static StemMixChannel Stem(string name, double gain = 1.0, bool muted = false, bool soloed = false) =>
        new(name, IsMasterMix: false, gain, muted, soloed);

    private static WaveformSamples Wave(params float[] peaks) =>
        WaveformSamples.FromBasePeaks(peaks, WaveformSamples.GenerationBlockMs);
}
