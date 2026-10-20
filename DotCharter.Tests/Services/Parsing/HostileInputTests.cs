using System.Xml.Linq;
using DotCharter.Services.Parsing.Images;
using DotCharter.Services.Parsing.Xml.Song;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Parsing;

/// <summary>
///     Every one of these inputs is reachable from a file the user merely
///     DOWNLOADED — a CDLC, a psarc-embedded arrangement, album art scanned in
///     bulk during a library import. A reader that turns a header field into an
///     allocation size or a loop bound without checking it first is a
///     denial-of-service primitive, and a crash mid-import is a real bug even
///     when nothing is "exploited".
/// </summary>
public sealed class HostileInputTests
{
    private static XDocument SongWithTuning(string stringsAttr) => XDocument.Parse(
        $"""
         <song>
           <title>t</title>
           <arrangement>Lead</arrangement>
           <tuning strings="{stringsAttr}" string0="0" string1="0" />
         </song>
         """);

    [Theory]
    [InlineData("2000000000")]
    [InlineData("2147483647")]
    [InlineData("-5")]
    [InlineData("0")]
    public void RsXml_AnAbsurdStringCount_DoesNotDriveAnAllocationLoop(string strings)
    {
        // 200 bytes of XML used to spin two billion iterations building
        // TuningPitches. The rscp parser has clamped this field all along.
        var reader = new RsXmlSongReader();

        var project = reader.ReadDocument(SongWithTuning(strings), "hostile.xml");

        var arrangement = Assert.Single(project.Arrangements);
        Assert.InRange(arrangement.StringCount, 1, 12);
        Assert.Equal(arrangement.StringCount, arrangement.TuningPitches.Count);
    }

    [Fact]
    public void RsXml_AnOrdinaryStringCountIsUntouched()
    {
        var project = new RsXmlSongReader().ReadDocument(SongWithTuning("6"), "fine.xml");

        Assert.Equal(6, Assert.Single(project.Arrangements).StringCount);
    }

    private static byte[] DdsHeader(uint width, uint height)
    {
        // Just enough of a DDS header to reach the dimension check: magic,
        // size, flags, then height/width at offsets 12 and 16.
        var data = new byte[148];
        "DDS "u8.CopyTo(data);
        BitConverter.GetBytes(124u).CopyTo(data, 4);
        BitConverter.GetBytes(height).CopyTo(data, 12);
        BitConverter.GetBytes(width).CopyTo(data, 16);
        BitConverter.GetBytes(0x4u).CopyTo(data, 80); // DDPF_FOURCC
        "DXT1"u8.CopyTo(data.AsSpan(84));
        return data;
    }

    [Theory]
    [InlineData(65535u, 65535u)]
    [InlineData(16384u, 16384u)]
    [InlineData(0u, 512u)]
    [InlineData(512u, 0u)]
    public void Dds_AbsurdDimensions_AreRefusedNotAllocated(uint width, uint height)
    {
        // A 148-byte file claiming 16384x16384 asked for a gigabyte, BEFORE
        // anything checked the payload could possibly contain that many pixels.
        // `checked` only turned the extreme case into an OverflowException; it
        // never stopped a merely-absurd allocation from succeeding. Album art
        // is converted in bulk during a library scan, so this ran unattended.
        var dir = Directory.CreateTempSubdirectory("dotcharter_dds_").FullName;
        try
        {
            var ddsPath = Path.Combine(dir, "hostile.dds");
            var pngPath = Path.Combine(dir, "out.png");
            File.WriteAllBytes(ddsPath, DdsHeader(width, height));

            Assert.False(DdsImageConverter.TryConvertToPng(ddsPath, pngPath));
            Assert.False(File.Exists(pngPath));
        }
        finally
        {
            BestEffortDelete.Directory(dir);
        }
    }

    [Fact]
    public void Dds_ATruncatedPayloadAtASaneSizeFailsCleanly()
    {
        // Sane dimensions, missing pixel data: a clean false, never an
        // unhandled exception escaping into the library scan.
        var dir = Directory.CreateTempSubdirectory("dotcharter_dds_").FullName;
        try
        {
            var ddsPath = Path.Combine(dir, "truncated.dds");
            File.WriteAllBytes(ddsPath, DdsHeader(512, 512));

            Assert.False(DdsImageConverter.TryConvertToPng(ddsPath, Path.Combine(dir, "out.png")));
        }
        finally
        {
            BestEffortDelete.Directory(dir);
        }
    }
}
