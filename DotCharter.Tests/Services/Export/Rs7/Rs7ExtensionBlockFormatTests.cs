using System.Buffers.Binary;
using DotCharter.Services.Export.Sng;
using DotCharter.Services.Export.Sng.Model;

namespace DotCharter.Tests.Services.Export.Rs7;

/// <summary>The tests of <see cref="Rs7ExtensionBlockFormatTests" /> that need no package reader; its fixtures are in <c>Rs7ExtensionBlockFormatTests.Fixtures.cs</c>.</summary>
public sealed partial class Rs7ExtensionBlockFormatTests
{
    [Fact]
    public void The_seven_string_example_is_byte_identical() =>
        Assert.Equal(Hex(SevenStringExample), RsExtensionBlockWriter.Serialize(SevenStringBlock()));

    /// <summary>The model is general (8 strings, two columns) even though the exporter
    /// refuses an 8-string guitar today.</summary>
    [Fact]
    public void The_eight_string_example_is_byte_identical() =>
        Assert.Equal(Hex(EightStringExample), RsExtensionBlockWriter.Serialize(EightStringBlock()));

    [Fact]
    public void Sections_are_written_STRS_CHDT_CHNT_even_when_empty()
    {
        var bytes = RsExtensionBlockWriter.Serialize(RsExtensionBlock.ForStrings(7));

        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12)));
        Assert.Equal((uint)bytes.Length, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)));
        Assert.Equal(["STRS", "CHDT", "CHNT"], SectionIds(bytes));
        // STRS and CHDT critical, CHNT not.
        Assert.Equal([1, 1, 0], SectionFlags(bytes));
    }

    [Fact]
    public void Bends_are_written_with_a_zero_reserved_word()
    {
        var bytes = RsExtensionBlockWriter.Serialize(BendBlock());

        Assert.Equal(16 + 12 + 5 + 12 + 4 + 12 + 4 + 9 + 24, bytes.Length);
        Assert.All(new[] { bytes.Length - 16, bytes.Length - 4 },
            at => Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at))));
        // Time and step of each point, little-endian floats, before its reserved word.
        Assert.Equal(1.5f, BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(bytes.Length - 24)));
        Assert.Equal(1f, BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(bytes.Length - 20)));
        Assert.Equal(1.75f, BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(bytes.Length - 12)));
        Assert.Equal(0f, BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(bytes.Length - 8)));
    }
}
