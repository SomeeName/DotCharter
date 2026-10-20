using DotCharter.Tone.UI.Controls.Dsp;

namespace DotCharter.Tests.UI;

/// <summary>
///     The tone-rig design prints a parameter's unit ONCE: "Rate" over the
///     dial, "0.8" on the plate and "Hz" beside it — never "Rate (Hz)" over a
///     plate that says "0.8 Hz". <see cref="DspControlText.SplitUnit" /> is
///     the one place that decides what a native name's trailing parenthesis
///     is, and the whitelist is what keeps "Feedback (BBD)" a name.
/// </summary>
public sealed class RigParamCaptionTests
{
    [Theory]
    [InlineData("Rate (Hz)", "Rate", "Hz")]
    [InlineData("Depth (ms)", "Depth", "ms")]
    [InlineData("Wet HPF (Hz)", "Wet HPF", "Hz")]
    [InlineData("Input (dB)", "Input", "dB")]
    [InlineData("Mix (%)", "Mix", "%")]
    [InlineData("Low Cut (kHz)", "Low Cut", "kHz")]
    public void AUnitSuffix_MovesOffTheCaption(string name, string caption, string unit)
    {
        var (c, u) = DspControlText.SplitUnit(name);
        Assert.Equal(caption, c);
        Assert.Equal(unit, u);
    }

    [Theory]
    [InlineData("Feedback (BBD)")]
    [InlineData("Voices")]
    [InlineData("Mode (Auto Swell)")]
    [InlineData("(Hz)")]
    [InlineData("")]
    public void AParenthesisThatIsNotAUnit_StaysOnTheCaption(string name)
    {
        var (c, u) = DspControlText.SplitUnit(name);
        Assert.Equal(name, c);
        Assert.Null(u);
    }
}
