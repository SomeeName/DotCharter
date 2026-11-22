using DotCharter.Services.Audio.LivePlay;
using JuceSharp.Dsp;

namespace DotCharter.Tests.Services.Audio.LivePlay;

/// <summary>Synthetic measurements for simultaneous semitone unisons and one swept string (#656).</summary>
public sealed class CqtUnisonResolutionTests(ITestOutputHelper output)
{
    private const int SampleRate = 44100;

    [Theory]
    [InlineData(40, false)]
    [InlineData(52, false)]
    [InlineData(57, false)]
    [InlineData(64, false)]
    [InlineData(66, false)]
    [InlineData(69, false)]
    [InlineData(40, true)]
    [InlineData(52, true)]
    [InlineData(57, true)]
    [InlineData(64, true)]
    [InlineData(66, true)]
    [InlineData(69, true)]
    public void Simultaneous_semitone_strings_confirm(int midi, bool forgiving)
        => Check(midi, forgiving, sweep: false);

    [Theory]
    [InlineData(52, false)]
    [InlineData(64, false)]
    [InlineData(69, false)]
    [InlineData(52, true)]
    [InlineData(64, true)]
    [InlineData(69, true)]
    public void One_swept_string_cannot_supply_two_requirements(int midi, bool forgiving)
        => Check(midi, forgiving, sweep: true);

    [Theory]
    [InlineData(40, false)]
    [InlineData(52, false)]
    [InlineData(57, false)]
    [InlineData(64, false)]
    [InlineData(66, false)]
    [InlineData(69, false)]
    [InlineData(40, true)]
    [InlineData(52, true)]
    [InlineData(57, true)]
    [InlineData(64, true)]
    [InlineData(66, true)]
    [InlineData(69, true)]
    public void Streaming_simultaneous_semitone_strings_confirm(int midi, bool forgiving)
        => Check(midi, forgiving, sweep: false, streaming: true);

    [Theory]
    [InlineData(52, false)]
    [InlineData(64, false)]
    [InlineData(69, false)]
    [InlineData(52, true)]
    [InlineData(64, true)]
    [InlineData(69, true)]
    public void Streaming_one_swept_string_cannot_supply_two_requirements(int midi, bool forgiving)
        => Check(midi, forgiving, sweep: true, streaming: true);

    /// <summary>
    ///     A unison bend whose bent string leaves the shared pitch as late as the chord
    ///     window allows: both strings sound the same note until 220 ms after the onset, then
    ///     one glides a semitone up within 30 ms, reaching it at the window's 250 ms edge. The
    ///     two peaks coexist only in the window's last frames, and that is enough in both modes.
    /// </summary>
    [Theory]
    [InlineData(52, false, false)]
    [InlineData(64, false, false)]
    [InlineData(69, false, false)]
    [InlineData(52, true, false)]
    [InlineData(64, true, false)]
    [InlineData(69, true, false)]
    [InlineData(52, false, true)]
    [InlineData(64, false, true)]
    [InlineData(69, false, true)]
    [InlineData(52, true, true)]
    [InlineData(64, true, true)]
    [InlineData(69, true, true)]
    public void A_unison_bend_that_separates_late_in_the_window_confirms(int midi, bool forgiving, bool streaming)
        => Check(midi, forgiving, sweep: false, streaming, lateBend: true);

    private void Check(int midi, bool forgiving, bool sweep, bool streaming = false, bool lateBend = false)
    {
        var audio = new float[(int)(SampleRate * 1.2)];
        var phase = 0.0;
        var bentPhase = 0.0;
        for (var i = (int)(SampleRate * 0.2); i < audio.Length; i++)
        {
            var t = (i - SampleRate * 0.2) / SampleRate;
            var hz = 440 * Math.Pow(2, (midi - 69) / 12.0);
            if (sweep)
            {
                phase += 2 * Math.PI * hz * Math.Pow(2, Math.Clamp(t / 0.12, 0, 1) / 12) / SampleRate;
                audio[i] = (float)(Math.Sin(phase) * Math.Exp(-t * 1.2));
            }
            else if (lateBend)
            {
                // A quarter turn apart, so the shared pitch never cancels itself out.
                bentPhase += 2 * Math.PI * hz * Math.Pow(2, Math.Clamp((t - 0.22) / 0.03, 0, 1) / 12) / SampleRate;
                audio[i] = (float)((Math.Sin(2 * Math.PI * hz * t) + Math.Sin(bentPhase + Math.PI / 2)) * 0.5 * Math.Exp(-t * 1.2));
            }
            else
            {
                audio[i] = (float)((Math.Sin(2 * Math.PI * hz * t) +
                    Math.Sin(2 * Math.PI * hz * Math.Pow(2, 1.0 / 12) * t)) * 0.5 * Math.Exp(-t * 1.2));
            }
        }

        var cqt = streaming
            ? new JuceConstantQ(fMinMidi: LiveCqtVerifier.LiveMinMidi,
                binCount: LiveCqtVerifier.LiveBinCount, maxKernelSamples: LiveCqtVerifier.LiveMaxKernelSamples,
                noiseHistoryFrames: LiveCqtConfig.NoiseHistoryFrames(SampleRate), subOctaveBins: LiveCqtConfig.SubOctaveBins)
            : new JuceConstantQ();
        if (streaming)
        {
            for (var offset = 0; offset < audio.Length; offset += 1024)
                cqt.Push(audio.AsSpan(offset, Math.Min(1024, audio.Length - offset)), offset);
        }
        else
        {
            cqt.AnalyzeWhole(audio);
        }
        var verifier = new LiveCqtVerifier(cqt, 1)
        {
            Forgiving = forgiving, ChordStrictness = LiveChordVoicingStrictness.Strict
        };
        var sound = new ExpectedSound { TimeMs = 200, MidiNotes = [midi, midi], ChordBendSemis = [0, 1], IsChord = true };
        var late = verifier.Verify(sound).Confirmed;
        var early = verifier.VerifyEarly(sound).Confirmed;
        var bothPeaks = 0;
        var frames = 0;
        var bin = cqt.BinForMidi(midi);
        for (var f = cqt.FrameOfSeconds(0.16); f <= cqt.FrameOfSeconds(0.45); f++)
        {
            frames++;
            if (cqt.Magnitude(bin, f) > cqt.Magnitude(bin - 1, f) &&
                cqt.Magnitude(bin, f) > cqt.Magnitude(bin + 1, f) &&
                cqt.Magnitude(bin + 2, f) > cqt.Magnitude(bin + 1, f) &&
                cqt.Magnitude(bin + 2, f) > cqt.Magnitude(bin + 3, f))
            {
                bothPeaks++;
            }
        }

        output.WriteLine($"midi={midi}, Hz={440 * Math.Pow(2, (midi - 69) / 12.0):F2}, forgiving={forgiving}, sweep={sweep}, lateBend={lateBend}, streaming={streaming}, late={late}, early={early}, separatePeaks={bothPeaks}/{frames}");
        Assert.Equal(!sweep, late);
        Assert.Equal(!sweep, early);
    }
}
