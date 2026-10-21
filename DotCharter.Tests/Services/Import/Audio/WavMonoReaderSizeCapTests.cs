using DotCharter.Services.Import.Audio;
using DotCharter.Services.Infrastructure;

namespace DotCharter.Tests.Services.Import.Audio;

/// <summary>
///     The whole WAV is read, then a float per frame is allocated on top, so the
///     length the file system reports is checked before the read. The cap is
///     lowered through the internal overload so no big file is needed.
/// </summary>
public sealed class WavMonoReaderSizeCapTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter-wavcap-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private string WriteWav(int frames)
    {
        var data = new byte[frames * 2];
        var path = Path.Combine(_dir, $"{frames}.wav");
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8);
        w.Write(36 + data.Length);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(48000);
        w.Write(96000);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(data.Length);
        w.Write(data);
        w.Flush();
        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    [Fact]
    public void AFileOverTheCap_IsRefusedAsTooLargeBeforeItIsRead()
    {
        var path = WriteWav(frames: 1000); // ~2 KB

        var ex = Assert.Throws<ImportFileTooLargeException>(() => WavMonoReader.Read(path, maxBytes: 1024));

        Assert.Contains("too large", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1024, ex.Cap);
    }

    [Fact]
    public void AFileInsideTheCap_StillDecodes()
    {
        var path = WriteWav(frames: 1000);

        var (samples, rate) = WavMonoReader.Read(path, maxBytes: 1024 * 1024);

        Assert.Equal(1000, samples.Length);
        Assert.Equal(48000, rate);
    }

    [Fact]
    public void ThePublicOverload_UsesTheSharedCap()
    {
        var path = WriteWav(frames: 100);
        Assert.Equal(100, WavMonoReader.Read(path).Samples.Length);
        Assert.Equal(512L * 1024 * 1024, ImportSizeLimits.WavFileBytes);
    }
}
