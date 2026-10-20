using DotCharter.Services.Ai.Match;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Ai;

/// <summary>
///     <see cref="AudioClipLoading.TryLoad" />, over real files: the tone
///     designer's bundled reference DI reads through it, and every way a file can
///     fail to be a clip is "no clip", never an exception out of the Match command.
/// </summary>
public sealed class AudioClipLoadingTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter-clip-load-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_dir);

    [Fact]
    public void A16BitStereoWav_LoadsAsMono_AtItsOwnRate()
    {
        var path = Path.Combine(_dir, "di.wav");
        // Two frames, L/R: (0.5, 0.5) and (-0.5, 0.25).
        WritePcm16(path, 2, 44_100, [0.5f, 0.5f, -0.5f, 0.25f]);

        var clip = new AudioClipLoader().TryLoad(path);

        Assert.NotNull(clip);
        Assert.Equal(44_100, clip.SampleRate);
        Assert.Equal(2, clip.Samples.Length);
        Assert.InRange(clip.Samples[0], 0.499f, 0.501f);
        Assert.InRange(clip.Samples[1], -0.126f, -0.124f);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("missing.wav")]
    public void ABlankOrMissingPath_IsNoClip(string? name)
    {
        var path = name is { Length: > 0 } ? Path.Combine(_dir, name) : name;

        Assert.Null(new AudioClipLoader().TryLoad(path));
    }

    [Fact]
    public void AFileThatIsNotAudio_IsNoClip()
    {
        var path = Path.Combine(_dir, "notes.wav");
        File.WriteAllText(path, "this is not RIFF");

        Assert.Null(new AudioClipLoader().TryLoad(path));
    }

    [Fact]
    public void AWavWithNoFrames_IsNoClip()
    {
        var path = Path.Combine(_dir, "empty.wav");
        WritePcm16(path, 1, 48_000, []);

        Assert.Null(new AudioClipLoader().TryLoad(path));
    }

    private static void WritePcm16(string path, short channels, int rate, float[] interleaved)
    {
        using var stream = File.Create(path);
        using var w = new BinaryWriter(stream);
        var dataBytes = interleaved.Length * 2;
        w.Write("RIFF"u8.ToArray());
        w.Write(36 + dataBytes);
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray());
        w.Write(16);
        w.Write((short)1);
        w.Write(channels);
        w.Write(rate);
        w.Write(rate * channels * 2);
        w.Write((short)(channels * 2));
        w.Write((short)16);
        w.Write("data"u8.ToArray());
        w.Write(dataBytes);
        foreach (var sample in interleaved) w.Write((short)Math.Round(sample * 32767));
    }
}
