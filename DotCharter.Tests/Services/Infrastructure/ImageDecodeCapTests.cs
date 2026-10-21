using System.Buffers.Binary;
using DotCharter.Services.Export.Images;
using DotCharter.Services.Import.Audio;
using DotCharter.Services.Infrastructure;

namespace DotCharter.Tests.Services.Infrastructure;

/// <summary>
///     A decoder allocates width x height x 4 bytes before it reads a pixel, so a
///     PNG of a few hundred bytes that DECLARES 60,000 x 60,000 is a 14 GB
///     allocation. Every site that decodes a picture somebody else supplied asks
///     <see cref="ImageHeaderProbe.IsDecodable(Stream)" /> first.
///     <para>
///         The over-cap fixtures here are REAL, valid PNGs one pixel past the cap
///         in one direction (8193 x 1: a few dozen bytes, 33 KB decoded) - the
///         decoder would accept them, so only the probe can be what refuses them.
///         A header-only forgery would be refused by a failed decode as well and
///         prove nothing.
///     </para>
/// </summary>
public sealed class ImageDecodeCapTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"dotcharter-imgcap-{Guid.NewGuid():N}");

    public ImageDecodeCapTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    private string WritePng(int width, int height, string name = "cover.png")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, TinyPng.Encode(width, height));
        return path;
    }

    [Fact]
    public void TheSharedCeilingIsTheOneTheDdsReaderUses() =>
        Assert.Equal(8192, ImageHeaderProbe.MaxDecodeDimension);

    [Theory]
    [InlineData(8192, 1, true)]
    [InlineData(1, 8192, true)]
    [InlineData(512, 512, true)]
    [InlineData(8193, 1, false)]
    [InlineData(1, 8193, false)]
    [InlineData(60_000, 60_000, false)]
    [InlineData(0, 10, false)]
    [InlineData(-1, 10, false)]
    public void ThePolicyAcceptsUpToTheCeilingOnEitherEdge(int w, int h, bool expected) =>
        Assert.Equal(expected, ImageHeaderProbe.IsWithinDecodeCap(w, h));

    [Fact]
    public void ARealPngAtTheCeiling_IsDecodable()
    {
        using var stream = new MemoryStream(TinyPng.Encode(8192, 1));
        Assert.True(ImageHeaderProbe.IsDecodable(stream));
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public void ARealPngOnePixelPastTheCeiling_IsRefused()
    {
        using var stream = new MemoryStream(TinyPng.Encode(8193, 1));
        Assert.False(ImageHeaderProbe.IsDecodable(stream));
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public void AHeaderDeclaringSixtyThousandSquare_IsRefusedWithoutADecode()
    {
        var png = TinyPng.Encode(1, 1);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16, 4), 60_000);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20, 4), 60_000);
        using var stream = new MemoryStream(png);
        Assert.False(ImageHeaderProbe.IsDecodable(stream));
    }

    [Fact]
    public void BytesTheProbeDoesNotRecognise_AreRefused()
    {
        using var stream = new MemoryStream(new byte[2048]);
        Assert.False(ImageHeaderProbe.IsDecodable(stream));
    }

    [Fact]
    public void ThePathOverload_AnswersTheSameAndFalseForAMissingFile()
    {
        Assert.True(ImageHeaderProbe.IsDecodable(WritePng(64, 64, "ok.png")));
        Assert.False(ImageHeaderProbe.IsDecodable(WritePng(8193, 1, "big.png")));
        Assert.False(ImageHeaderProbe.IsDecodable(Path.Combine(_dir, "missing.png")));
    }

    // ---- the decode sites that take a path ---------------------------------

    [Fact]
    public void DdsEncoder_RefusesAnOversizedSourceBeforeDecoding()
    {
        var big = WritePng(8193, 1, "big.png");
        var single = Assert.Throws<InvalidDataException>(() => DdsEncoder.EncodeCoverArt(big, 64));
        Assert.Contains("dimensions", single.Message, StringComparison.Ordinal);

        var many = Assert.Throws<InvalidDataException>(() => DdsEncoder.EncodeCoverArtSizes(big, [64, 128]));
        Assert.Contains("dimensions", many.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DdsEncoder_StillEncodesAnOrdinaryCover()
    {
        var dds = DdsEncoder.EncodeCoverArt(WritePng(64, 64, "ok.png"), 64);
        Assert.True(dds.Length > 128);
    }

    [Fact]
    public void AudioMetadataExtractor_RefusesAnOversizedEmbeddedCover() =>
        Assert.Null(AudioMetadataExtractor.ResizeToPng(WritePng(8193, 1, "big.png")));

    [Fact]
    public void AudioMetadataExtractor_StillResizesAnOrdinaryCover()
    {
        var png = AudioMetadataExtractor.ResizeToPng(WritePng(64, 64, "ok.png"));
        Assert.NotNull(png);
        Assert.True(ImageHeaderProbe.TryReadPixelSize(png, out var w, out var h));
        Assert.Equal((512, 512), (w, h));
    }
}
