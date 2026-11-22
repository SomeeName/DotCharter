using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using DotCharter.Services.Export;
using DotCharter.Services.Export.Rocksmith;
using DotCharter.Tests.Services.Export.Rs7.Suite;

namespace DotCharter.Tests.Services.Export.Rsxr;

internal static class RsxrTestSupport
{
    public static byte[] PackageMagic(bool extended) => extended ? [80, 83, 88, 82] : [80, 83, 65, 82];

    public static readonly DateTime ConversionTime = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Unspecified);

    public static Inst Instrument(bool bass, int strings) => (bass, strings) switch
    {
        (true, 4) => Inst.B4,
        (true, 5) => Inst.B5,
        (true, 6) => Inst.B6,
        (false, 6) => Inst.G6,
        (false, 7) => Inst.G7,
        (false, 8) => Inst.G8,
        _ => throw new ArgumentOutOfRangeException(nameof(strings))
    };

    public static BuiltChart Chart(bool bass, int strings)
    {
        var inst = Instrument(bass, strings);
        var singles = Rs7SuiteContent.Singles(new Seq(), inst, [3]);
        var cols = inst.Order.Select((s, i) => new Col(s, 3 + i % 3, 1 + i % 4,
            Tech.Bend | Tech.Vibrato | Tech.Slide | Tech.PalmMute)).ToArray();
        var events = singles.Skip(2).Chord("Full", cols, 5).Done();
        var tuning = Enumerable.Range(0, strings).Select(i => i - 3).ToArray();
        return Rs7TestSuiteBuilder.Build(new ChartSpec("rsxr", "Positional strings and chord techniques.",
            [new Part(inst, tuning, events, [new Sec("riff", 4)])]));
    }

    public static RocksmithBuildIdentity Identity(int arrangements)
    {
        var pinned = RocksmithPlatformPackageTests.PinnedIdentity();
        return pinned with
        {
            Arrangements = [.. Enumerable.Range(1, arrangements).Select(i => new RsArrangementIdentity(new Guid(i, 1, 2, new byte[8]), 1000 + i))],
            GraphItems = [.. Enumerable.Range(1, 8 + 2 * arrangements).Select(i =>
                new RsGraphItemIdentity(new Guid(i, 3, 4, new byte[8]), new Guid(i, 5, 6, new byte[8])))],
            ConversionTime = ConversionTime
        };
    }

    public static Task<RocksmithBuildResult> Build(ChartProject project, double lengthMs, string audio, string output,
        ExportPlatform platform = ExportPlatform.Pc, bool? plain = true, string dlcKey = "RsxrCompare") =>
        new RocksmithPackageBuilder(new FakeAppLogger()).BuildPsarcAsync(ChartProjectCloner.Clone(project),
            new RocksmithBuildOptions
            {
                OutputPath = output, AudioPath = audio, PreviewAudioPath = audio, DlcKey = dlcKey,
                SongLengthMs = lengthMs, IncludeVocals = false,
                Identity = Identity(project.Arrangements.Count(a => RsxrStringLayout.ExportRefusal(a) == null)),
                PlainPackage = plain, Platform = platform
            }, cancellationToken: TestContext.Current.CancellationToken);

    /// <summary>Inspects only unencrypted synthetic archives emitted by these tests.</summary>
    public static Dictionary<string, byte[]> Entries(string path)
    {
        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.AsSpan(0, 4).SequenceEqual(PackageMagic(false)) || bytes.AsSpan(0, 4).SequenceEqual(PackageMagic(true)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(28)));
        var count = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20));
        var blockSize = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(24));
        var table = 32 + 30 * count;
        var data = new List<byte[]>();
        for (var i = 0; i < count; i++)
        {
            var entry = 32 + 30 * i;
            var block = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(entry + 16));
            var length = (int)UInt40(bytes.AsSpan(entry + 20, 5));
            var offset = (int)UInt40(bytes.AsSpan(entry + 25, 5));
            using var output = new MemoryStream();
            while (output.Length < length)
            {
                var stored = (int)BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(table + 2 * block++));
                if (stored == 0) stored = blockSize;
                var expected = Math.Min(blockSize, length - (int)output.Length);
                if (stored == expected)
                {
                    output.Write(bytes, offset, stored);
                }
                else
                {
                    using var zlib = new ZLibStream(new MemoryStream(bytes, offset, stored), CompressionMode.Decompress);
                    zlib.CopyTo(output);
                }

                offset += stored;
            }

            Assert.Equal(length, output.Length);
            data.Add(output.ToArray());
        }

        var names = Encoding.ASCII.GetString(data[0]).Split('\n');
        Assert.Equal(count - 1, names.Length);
        return names.Select((name, i) => (name, value: data[i + 1])).ToDictionary(p => p.name, p => p.value);
    }

    private static ulong UInt40(ReadOnlySpan<byte> bytes)
    {
        var value = 0UL;
        foreach (var b in bytes) value = value << 8 | b;
        return value;
    }

    public static byte[] Inflate(byte[] packed)
    {
        Assert.Equal(0x4A, BinaryPrimitives.ReadInt32LittleEndian(packed));
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(packed.AsSpan(4)));
        using var zlib = new ZLibStream(new MemoryStream(packed, 28, packed.Length - 84), CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        Assert.Equal(BinaryPrimitives.ReadInt32LittleEndian(packed.AsSpan(24)), output.Length);
        return output.ToArray();
    }
}
