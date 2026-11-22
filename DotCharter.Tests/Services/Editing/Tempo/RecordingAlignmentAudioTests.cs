using DotCharter.Services.Editing.Tempo.RecordingAlignment;
using DotCharter.Tests.Services.Export.Audio;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Editing.Tempo;

public sealed class RecordingAlignmentAudioTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dc-recording-stems").FullName;

    public void Dispose() => BestEffortDelete.Directory(_dir);

    private string WriteAudio(string name, int seconds)
    {
        var path = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        const int rate = 48000;
        var samples = seconds * rate;
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + samples * 2);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(rate);
        writer.Write(rate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(samples * 2);
        for (var i = 0; i < samples; i++) writer.Write((short)1000);
        return path;
    }

    [Theory]
    [InlineData(1, true, false)]
    [InlineData(12, true, false)]
    [InlineData(1, true, true)]
    [InlineData(12, true, true)]
    [InlineData(1, false, true)]
    [InlineData(12, false, true)]
    [InlineData(1, false, false)]
    [InlineData(12, false, false)]
    public void EveryInstrumentStemMustMatchTheMixDuration(int differentSeconds, bool native, bool ffmpeg)
    {
        var mix = WriteAudio("song-silence2.wav", 11);
        WriteAudio("stems/guitar.wav", 11);
        WriteAudio("stems/bass.wav", 11);
        var ct = TestContext.Current.CancellationToken;

        var matching = RecordingAlignmentAudio.Load(mix, ct, loadAudio: path => PcmDecoderFixture.Load(path, native, ffmpeg));
        Assert.Equal(RecordingAlignmentSource.Stems, matching.Source);
        Assert.All(matching.Mono, sample => Assert.InRange(sample, 0.05f, 0.07f));

        WriteAudio("stems/bass.wav", differentSeconds);
        var result = RecordingAlignmentAudio.Load(mix, ct, loadAudio: path => PcmDecoderFixture.Load(path, native, ffmpeg));

        Assert.Equal(RecordingAlignmentSource.Mix, result.Source);
        Assert.Equal(11 * result.SampleRate, result.Mono.Length);
        Assert.All(result.Mono, sample => Assert.InRange(sample, 0.02f, 0.04f));
    }
}
