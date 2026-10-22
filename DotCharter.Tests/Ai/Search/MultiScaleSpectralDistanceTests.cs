using DotCharter.Services.Ai.Match;
using DotCharter.Services.Ai.Match.Search;

namespace DotCharter.Tests.Ai.Search;

/// <summary>
///     The matcher takes the target's spectra ONCE per match and hands them to every render's
///     distance (<see cref="MultiScaleSpectralDistance.Spectra" />). That must be the same
///     number, bit for bit, as measuring the two signals against each other scale by scale:
///     a match's distances, floor and scores all rest on it. The expected value is computed
///     here from <see cref="MultiScaleSpectralDistance.AverageSpectrum" />, so it is
///     independent of the code that takes the shortcut.
/// </summary>
public sealed class MultiScaleSpectralDistanceTests
{
    private static double Expected(MonoAudio a, MonoAudio b)
    {
        var total = 0.0;
        var used = 0;
        foreach (var n in MultiScaleSpectralDistance.Scales)
        {
            var sa = MultiScaleSpectralDistance.AverageSpectrum(a.Samples, n);
            var sb = MultiScaleSpectralDistance.AverageSpectrum(b.Samples, n);
            if (sa is null || sb is null) continue;
            var acc = 0.0;
            for (var i = 0; i < sa.Length; i++) acc += Math.Abs(Math.Log(sa[i] + 1e-8) - Math.Log(sb[i] + 1e-8));
            total += acc / sa.Length;
            used++;
        }

        return used == 0 ? 0.0 : total / used;
    }

    private static void AssertBothForms(MonoAudio wet, MonoAudio target)
    {
        var expected = Expected(wet, target);
        Assert.Equal(expected, MultiScaleSpectralDistance.Distance(wet, target));
        Assert.Equal(expected, MultiScaleSpectralDistance.Distance(wet, MultiScaleSpectralDistance.Spectra(target)));
    }

    [Fact]
    public void ASharedTargetSpectrum_GivesTheTwoSignalDistance_ToTheLastBit()
    {
        var wet = SearchFakes.Di(1.2, seed: 7);
        var target = SearchFakes.Di(1.2, seed: 11);

        Assert.True(Expected(wet, target) > 0.0, "the two signals must differ for the comparison to mean anything");
        AssertBothForms(wet, target);
    }

    [Fact]
    public void ASignalAgainstItself_IsZeroEitherWay()
    {
        var audio = SearchFakes.Di(1.2, seed: 7);

        Assert.Equal(0.0, Expected(audio, audio));
        AssertBothForms(audio, audio);
    }

    [Theory]
    [InlineData(300)]
    [InlineData(700)]
    [InlineData(1500)]
    public void ATargetShorterThanSomeWindows_SkipsThoseScalesInBothForms(int targetSamples)
    {
        // 512 / 1024 / 2048-sample windows: a target of 300 samples fits none, 700 fits one,
        // 1500 fits two. A scale the target cannot hold is skipped, never read as zero.
        var wet = SearchFakes.Di(1.2, seed: 7);
        var full = SearchFakes.Di(1.2, seed: 11);
        var target = new MonoAudio(full.Samples[..targetSamples], full.SampleRate);

        AssertBothForms(wet, target);
        Assert.Equal(targetSamples >= 512, Expected(wet, target) > 0.0);
    }
}
