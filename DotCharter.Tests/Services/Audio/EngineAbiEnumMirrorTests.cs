using DotCharter.Tone.Audio;
using DotCharter.Tone.Audio.Equalizer;
using JuceSharp;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
/// JuceSharp 0.8.x gave the four ABI enumerations real names
/// (<see cref="JuceEqBandType"/>, <see cref="JuceEqRoute"/>,
/// <see cref="JuceClickType"/>, <see cref="JuceDropTuneMode"/>) next to the
/// <c>int</c> overloads the app had been passing magic numbers to. The call
/// sites now cast to the enum, which reads better and picks the typed
/// overload — but a CAST is not a check. The app keeps its own mirrors,
/// because these values are persisted (<c>appconfig.json</c>, the EQ section)
/// and a domain enum may not be renumbered by a submodule bump; what must
/// never happen is the two drifting APART, which no compiler and no cast would
/// notice.
/// <para>
/// This is the same shape as the range mirroring the audio-engine guide
/// already requires of <c>EqBand</c>: "change either side and change both".
/// </para>
/// </summary>
public class EngineAbiEnumMirrorTests
{
    /// <summary>
    /// Every band shape the app can send, against the ABI value it is cast to.
    /// A silent renumber native-side turns "high pass" into "notch" on a bus
    /// the user is monitoring through.
    /// </summary>
    [Fact]
    public void EqBandTypeMirrorsTheAbiEnum()
    {
        Assert.Equal((int)JuceEqBandType.HighPass, (int)EqBandType.HighPass);
        Assert.Equal((int)JuceEqBandType.LowShelf, (int)EqBandType.LowShelf);
        Assert.Equal((int)JuceEqBandType.Bell, (int)EqBandType.Bell);
        Assert.Equal((int)JuceEqBandType.HighShelf, (int)EqBandType.HighShelf);
        Assert.Equal((int)JuceEqBandType.LowPass, (int)EqBandType.LowPass);
        Assert.Equal((int)JuceEqBandType.Notch, (int)EqBandType.Notch);

        // ...and neither side has grown a member the other has not. A new
        // native shape the app never offers is fine; a new native shape that
        // RENUMBERS is not, and only a count comparison catches the case where
        // one was inserted rather than appended.
        Assert.Equal(Enum.GetValues<JuceEqBandType>().Length, Enum.GetValues<EqBandType>().Length);
    }

    /// <summary>
    /// The route names deliberately differ — the app calls the guitar bus
    /// <c>GuitarIn</c> where the ABI calls it <c>Input</c> — so this mapping
    /// cannot be checked by name and has to be spelled out.
    /// </summary>
    [Fact]
    public void EqRouteMirrorsTheAbiEnum()
    {
        Assert.Equal((int)JuceEqRoute.Song, (int)EqRoute.Song);
        Assert.Equal((int)JuceEqRoute.Input, (int)EqRoute.GuitarIn);
        Assert.Equal((int)JuceEqRoute.Both, (int)EqRoute.Both);
        Assert.Equal(Enum.GetValues<JuceEqRoute>().Length, Enum.GetValues<EqRoute>().Length);
    }

    /// <summary>
    /// <c>IAudioPlaybackService.TryScheduleClick</c> takes a raw int all the
    /// way from the UI (<c>AudioPlaybackCoordinator</c> passes
    /// <c>beat.IsMeasure ? 1 : 0</c>, the calibration clap passes 2), and
    /// JuceAudioPlaybackService casts it straight to <see cref="JuceClickType"/>.
    /// Those three literals are the contract.
    /// </summary>
    [Fact]
    public void TheClickLiteralsOnTheAudioServiceMeanWhatTheAbiThinksTheyMean()
    {
        Assert.Equal(0, (int)JuceClickType.Beat);
        Assert.Equal(1, (int)JuceClickType.Downbeat);
        Assert.Equal(2, (int)JuceClickType.Clap);
    }

    /// <summary>
    /// <see cref="DropTunerEngineMap"/> is the app's persisted scale for the
    /// live-input pitch shifter and is documented as "identical to the native
    /// mode". Now that the boundary casts to <see cref="JuceDropTuneMode"/>,
    /// that sentence is load-bearing rather than reassuring.
    /// </summary>
    [Fact]
    public void DropTunerEngineMapMirrorsTheAbiEnum()
    {
        Assert.Equal((int)JuceDropTuneMode.Tight, DropTunerEngineMap.Tight);
        Assert.Equal((int)JuceDropTuneMode.Balanced, DropTunerEngineMap.Balanced);
        Assert.Equal((int)JuceDropTuneMode.Smooth, DropTunerEngineMap.Smooth);

        // The engine's own default is Balanced, and so is the app's fallback
        // for an absent or legacy config string ("rubberband", "timedomain").
        Assert.Equal((int)JuceDropTuneMode.Balanced, DropTunerEngineMap.Default);
        Assert.Equal(DropTunerEngineMap.Default, DropTunerEngineMap.FromConfigString("rubberband"));

        // ToNativeMode CLAMPS, so a persisted value from a newer build cannot
        // be cast into an enum member that does not exist.
        foreach (var mode in new[] { -5, 0, 1, 2, 99 })
            Assert.True(Enum.IsDefined((JuceDropTuneMode)DropTunerEngineMap.ToNativeMode(mode)));
    }
}
