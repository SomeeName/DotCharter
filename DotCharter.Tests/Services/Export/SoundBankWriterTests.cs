using System.Buffers.Binary;
using DotCharter.Services.Export.Audio;

namespace DotCharter.Tests.Services.Export;

/// <summary>The tests of <see cref="SoundBankWriterTests" /> that need no package reader; its fixtures are in <c>SoundBankWriterTests.Fixtures.cs</c>.</summary>
public partial class SoundBankWriterTests
{
    [Fact]
    public void Chunk_order_and_header_fields_match_the_reference()
    {
        using var audio = PatternedStream(MainDataLength);
        using var output = new MemoryStream();

        SoundBankWriter.Generate("testsong", audio, output, 0f, isPreview: false);

        var chunks = ReadChunks(output.ToArray());
        Assert.Equal(new[] { "BKHD", "DIDX", "DATA", "HIRC", "STID" }, chunks.Select(c => c.Name));

        // BKHD: version 91, bank id, language 0, HasFeedback 0, then three
        // int32s of PC-platform padding (getHeaderPaddingSize PC = 3).
        var bkhd = chunks[0].Data;
        Assert.Equal(28, bkhd.Length);
        Assert.Equal(91u, BinaryPrimitives.ReadUInt32LittleEndian(bkhd));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bkhd.AsSpan(8))); // Language ID
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bkhd.AsSpan(12))); // HasFeedback
        Assert.All(bkhd.Skip(16), b => Assert.Equal(0, b)); // padding

        // The bank id in BKHD is repeated inside the STID chunk, which also
        // carries the ASCII name "Song_{name}" with a one-byte length prefix.
        var bankId = BinaryPrimitives.ReadUInt32LittleEndian(bkhd.AsSpan(4));
        var stid = chunks[4].Data;
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(stid)); // String Type
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(stid.AsSpan(4))); // NumNames
        Assert.Equal(bankId, BinaryPrimitives.ReadUInt32LittleEndian(stid.AsSpan(8)));
        Assert.Equal("Song_testsong".Length, stid[12]);
        Assert.Equal("Song_testsong", System.Text.Encoding.ASCII.GetString(stid, 13, stid[12]));
    }

    [Fact]
    public void Deterministic_input_produces_expected_didx_and_data()
    {
        var wemBytes = new byte[MainDataLength + 5000];
        for (var i = 0; i < wemBytes.Length; i++)
            wemBytes[i] = (byte)(i * 31 + 7);

        using var audio = new MemoryStream(wemBytes);
        using var output = new MemoryStream();

        var returnedId = SoundBankWriter.Generate("deterministic", audio, output, -3.5f, isPreview: false);

        var chunks = ReadChunks(output.ToArray());

        // DIDX: [uint32 fileId][int32 offset = 0][int32 length = 51200].
        var didx = chunks.Single(c => c.Name == "DIDX").Data;
        Assert.Equal(12, didx.Length);
        Assert.Equal(returnedId, BinaryPrimitives.ReadUInt32LittleEndian(didx).ToString());
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(didx.AsSpan(4)));
        Assert.Equal(MainDataLength, BinaryPrimitives.ReadInt32LittleEndian(didx.AsSpan(8)));

        // DATA: the first 51200 bytes of the wem, verbatim.
        var data = chunks.Single(c => c.Name == "DATA").Data;
        Assert.Equal(wemBytes.AsSpan(0, MainDataLength).ToArray(), data);

        // Cross-check the HIRC sound object using the reference's readVolume
        // offsets: 46 bytes past the object type byte sit numParam = 3 and
        // the parameter types [Volume, 46, 47] followed by the volume float.
        var hirc = chunks.Single(c => c.Name == "HIRC").Data;
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(hirc)); // numObjects
        Assert.Equal(2, hirc[4]); // first object is the Sound (HierarchyID.Sound)
        var sound = hirc.AsSpan(4 + 1 + 4); // skip count, type byte, length
        Assert.Equal(3, sound[42]); // numParam
        Assert.Equal(0, sound[43]); // param 1: Volume
        Assert.Equal(46, sound[44]);
        Assert.Equal(47, sound[45]);
        Assert.Equal(-3.5f, BinaryPrimitives.ReadSingleLittleEndian(sound[46..]));
    }

    [Fact]
    public void Preview_bank_embeds_72000_bytes_and_zero_pads_short_audio()
    {
        var wemBytes = new byte[1000];
        for (var i = 0; i < wemBytes.Length; i++)
            wemBytes[i] = (byte)(i + 1);

        using var audio = new MemoryStream(wemBytes);
        using var output = new MemoryStream();

        SoundBankWriter.Generate("song_Preview", audio, output, 0f, isPreview: true);

        var chunks = ReadChunks(output.ToArray());
        var didx = chunks.Single(c => c.Name == "DIDX").Data;
        Assert.Equal(PreviewDataLength, BinaryPrimitives.ReadInt32LittleEndian(didx.AsSpan(8)));

        // Short input is zero-padded to the full data length, mirroring the
        // reference reader's ReadBytes behavior.
        var data = chunks.Single(c => c.Name == "DATA").Data;
        Assert.Equal(PreviewDataLength, data.Length);
        Assert.Equal(wemBytes, data.Take(wemBytes.Length).ToArray());
        Assert.All(data.Skip(wemBytes.Length), b => Assert.Equal(0, b));
    }
}
