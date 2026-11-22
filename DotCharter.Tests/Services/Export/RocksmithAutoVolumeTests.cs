using DotCharter.Services.Export.Audio;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Export;

/// <summary>
///     Auto volume against DLC Builder's own meter. The expected numbers were produced by running
///     Rocksmith2014.NET's <c>LufsMeter</c> (Rocksmith2014.Audio/Volume) in F# on the SAME generated
///     signal, fed 25,000 samples per channel at a time, with the SIMD sum replaced by a plain one.
/// </summary>
public sealed class RocksmithAutoVolumeTests
{
    /// <summary>A fixed signal: swelling sines (220 Hz x channel number) plus seeded noise, rounded to float32.</summary>
    private static float[] Signal(int sampleRate, int channels, double seconds, double amplitude)
    {
        var frames = (int)(seconds * sampleRate);
        var random = new Random(1234);
        var perChannel = new double[channels][];
        for (var channel = 0; channel < channels; channel++)
        {
            perChannel[channel] = new double[frames];
            for (var i = 0; i < frames; i++)
            {
                var t = (double)i / sampleRate;
                var envelope = 0.5 + 0.5 * Math.Sin(2.0 * Math.PI * 0.7 * t);
                perChannel[channel][i] = amplitude * envelope * Math.Sin(2.0 * Math.PI * (220.0 * (channel + 1)) * t) +
                    0.05 * (random.NextDouble() - 0.5);
            }
        }

        var interleaved = new float[frames * channels];
        for (var i = 0; i < frames; i++)
        {
            for (var channel = 0; channel < channels; channel++) interleaved[i * channels + channel] = (float)perChannel[channel][i];
        }

        return interleaved;
    }

    [Theory]
    [InlineData(48000, 2, 0.3, -14.505317453436, 0.5)]
    [InlineData(44100, 2, 0.3, -14.508783555295, 0.5)]
    [InlineData(48000, 1, 0.03, -32.500417127537, 18.5)]
    public void LoudnessMatchesDlcBuilder_AndDefaultVolumeTargetsMinus14(int rate, int channels, double amplitude, double loudness, double volume)
    {
        var samples = Signal(rate, channels, 5.0, amplitude);

        Assert.Equal(loudness, RocksmithAutoVolume.MeasureLoudness(samples, channels, rate), 6);
        Assert.Equal(volume, RocksmithAutoVolume.FromSamples(samples, channels, rate));
    }

    [Fact]
    public void QuietSongWithALoudSample_RoundsTheBoostDownToStayBelowFullScale()
    {
        var samples = Signal(48000, 2, 5.0, 0.03);
        samples[^1] = -0.835f;

        Assert.Equal(1.5, RocksmithAutoVolume.FromSamples(samples, 2, 48000));
    }

    [Fact]
    public void LoudMaster_IsTurnedDown()
    {
        var samples = Signal(48000, 2, 5.0, 0.9);
        var loudness = RocksmithAutoVolume.MeasureLoudness(samples, 2, 48000);

        Assert.True(loudness > -14);
        Assert.Equal(Math.Round(-14 - loudness, 1, MidpointRounding.AwayFromZero), RocksmithAutoVolume.FromSamples(samples, 2, 48000));
    }

    [Fact]
    public void Silence_ReadsZero() =>
        Assert.Equal(0.0, RocksmithAutoVolume.FromSamples(new float[48000 * 2 * 3], 2, 48000));

    [Theory]
    [InlineData(-20, -5.5)]
    [InlineData(-10, 4.5)]
    public void TheTargetRangeEndpoints_SetTheGain(int targetLufs, double expected)
    {
        var samples = Signal(48000, 2, 5.0, 0.3);

        Assert.Equal(expected, RocksmithAutoVolume.FromSamples(samples, 2, 48000, targetLufs));
    }

    [Fact]
    public void ADotWem_IsMeasuredThroughTheWavBesideIt_OrRefused()
    {
        var folder = Directory.CreateTempSubdirectory("dotcharter-autovol-").FullName;
        try
        {
            var wem = Path.Combine(folder, "song.wem");
            File.WriteAllBytes(wem, [1, 2, 3]);

            Assert.Throws<NotSupportedException>(() => RocksmithAutoVolume.SourceFor(wem));

            var wav = Path.Combine(folder, "song.wav");
            File.WriteAllBytes(wav, [0]);
            Assert.Equal(wav, RocksmithAutoVolume.SourceFor(wem));
            Assert.Equal(wav, RocksmithAutoVolume.SourceFor(wav));
        }
        finally
        {
            BestEffortDelete.Directory(folder);
        }
    }
}
