using DotCharter.Tone.Audio;
using DotCharter.Tone.Audio.ChartSynth;
using JuceSharp;

namespace DotCharter.Tests.Services.Audio.ChartSynth;

/// <summary>
///     The service API with NO engine: every request is recorded (the rebuild
///     replays it), and nothing pretends to have reached an engine.
/// </summary>
public sealed class ChartSynthServiceTests
{
    [Fact]
    public void WithoutAnEngine_RequestsAreRecorded_AndNothingClaimsToBeLive()
    {
        using var svc = new JuceAudioPlaybackService(FakeLocalization.English());
        IChartSynthEngine synth = svc;
        Assert.False(synth.IsChartSynthAvailable);
        Assert.False(synth.IsChartSynthEnabled);

        synth.SetChartSynthEnabled(true);
        synth.ChartSynthGain = 9f;                 // clamped
        synth.RouteInstrumentToRig = true;
        Assert.True(synth.IsChartSynthEnabled);
        Assert.Equal(4f, synth.ChartSynthGain);
        Assert.True(synth.CanRouteInstrumentToRig);
        Assert.True(synth.RouteInstrumentToRig);

        var upload = new ChartNoteUpload { Notes = new JuceChartNote[1], Bends = [], Fingerprint = 42 };
        Assert.False(synth.UploadChartNotes(upload));
        Assert.Null(synth.UploadedChartFingerprint);   // an upload no engine took is not remembered
        Assert.False(synth.TryGetChartSynthStatus(out _));
    }

    [Fact]
    public void WithoutAnEngine_TheInstrumentsAreRecorded_AndComeFromTheConfig()
    {
        using var svc = new JuceAudioPlaybackService(FakeLocalization.English());
        IChartSynthEngine synth = svc;
        Assert.Equal(ChartSynthInstrumentSelection.DefaultGuitar, synth.GuitarInstrument);
        Assert.Equal(ChartSynthInstrumentSelection.DefaultBass, synth.BassInstrument);
        if (synth.GetChartSynthModelInfo(ChartSynthModel.Telecaster) == null)
        {
            Assert.Skip("native DLL not loadable here");
            return;
        }
        var tele = new ChartSynthInstrument(ChartSynthModel.Telecaster, ChartSynthPickup.Neck, false);
        Assert.True(synth.TrySetChartSynthInstrument(tele));
        Assert.Equal(tele, synth.GuitarInstrument);
        Assert.Equal(ChartSynthInstrumentSelection.DefaultBass, synth.BassInstrument);
        // A pickup the model does not have is refused and changes nothing.
        Assert.False(synth.TrySetChartSynthInstrument(tele with { Pickup = ChartSynthPickup.MiddleBridge }));
        Assert.Equal(tele, synth.GuitarInstrument);
        // A coil split on a model without a humbucker is dropped, not stored.
        Assert.True(synth.TrySetChartSynthInstrument(tele with { CoilSplit = true }));
        Assert.False(synth.GuitarInstrument.CoilSplit);
    }

    [Fact]
    public void TheNullTransport_HasNoChartSynth()
        => Assert.False(typeof(IChartSynthEngine).IsAssignableFrom(typeof(NullAudioPlaybackService)));
}
