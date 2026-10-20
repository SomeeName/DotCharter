using DotCharter.Tone.Audio.Dsp;

namespace DotCharter.Tests.Services.Audio;

public sealed class DspParamFrequencyIdTests
{
    [Theory]
    [InlineData("cutoffHz")]
    [InlineData("freqHz")]
    [InlineData("toneKhz")]
    [InlineData("Hz")]
    public void AnIdEndingInHz_IsAFrequency(string id) => Assert.True(DspParamInfo.IsFrequencyId(id));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("z")]
    [InlineData("hzMix")]
    [InlineData("gain")]
    [InlineData("cutoffHZ")]
    public void AnythingElse_IsNot(string? id) => Assert.False(DspParamInfo.IsFrequencyId(id));
}
