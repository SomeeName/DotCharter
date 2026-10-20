using DotCharter.Services.Export.Audio;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Export.Audio;

/// <summary>
///     <see cref="PcmAudioLoader.WriteWav" /> feeds the Wwise conversion path for
///     non-wav sources — its output must round-trip bit-exactly through the
///     loader (48 kHz stereo 16-bit needs no conform pass, so any decoder
///     backend returns the samples verbatim).
/// </summary>
public sealed class PcmAudioWavWriterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter_wavwriter_").FullName;

    public void Dispose() => BestEffortDelete.Directory(_dir);

    [Fact]
    public void WriteWav_round_trips_through_the_loader()
    {
        // Samples stay below 16384: the native decoder scales short->float by
        // 32768 but float->short by 32767, so only that range survives every
        // decoder backend bit-exactly.
        var samples = new short[48_000 * 2]; // 1 s stereo
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (short)((i * 37) % 32000 - 16000);
        var audio = new PcmAudio(samples, 2, 48_000);

        var path = Path.Combine(_dir, "roundtrip.wav");
        PcmAudioLoader.WriteWav(path, audio);

        var warnings = new List<string>();
        var loaded = PcmAudioLoader.Load(path, warnings);

        Assert.Equal(2, loaded.Channels);
        Assert.Equal(48_000, loaded.SampleRate);
        Assert.Equal(samples, loaded.Samples);
    }
}
