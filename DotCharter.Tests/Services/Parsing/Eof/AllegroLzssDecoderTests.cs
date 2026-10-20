using DotCharter.Services.Parsing.Eof;

namespace DotCharter.Tests.Services.Parsing.Eof;

public class AllegroLzssDecoderTests
{
    [Fact]
    public void Decode_NoMagic_ReturnsInputVerbatim()
    {
        var input = new byte[] { 1, 2, 3, 4, 5 };
        var output = AllegroLzssDecoder.Decode(input);
        Assert.Equal(input, output);
    }

    [Fact]
    public void Decode_NopackMagic_StripsHeaderBytes()
    {
        // 'slh.' — Allegro no-pack magic, payload should be returned verbatim.
        var input = new byte[] { 0x73, 0x6C, 0x68, 0x2E, 0xAA, 0xBB, 0xCC };
        var output = AllegroLzssDecoder.Decode(input);
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC }, output);
    }

    [Fact]
    public void Decode_PackedLiteralRun_DecompressesToOriginalBytes()
    {
        // 'slh!' magic + one flag byte (0xFF — eight literal flags) + eight literal bytes.
        var input = new byte[]
        {
            0x73, 0x6C, 0x68, 0x21,
            0xFF,
            (byte)'A', (byte)'B', (byte)'C', (byte)'D',
            (byte)'E', (byte)'F', (byte)'G', (byte)'H'
        };
        var output = AllegroLzssDecoder.Decode(input);
        Assert.Equal("ABCDEFGH"u8.ToArray(), output);
    }

    [Fact]
    public void Decode_TooShortInput_ReturnsInputVerbatim()
    {
        var input = new byte[] { 1, 2, 3 };
        Assert.Same(input, AllegroLzssDecoder.Decode(input));
    }

    [Fact]
    public void Decode_PartialMagic_ReturnsInputVerbatim()
    {
        // 'slh' + something else — neither pack nor no-pack magic.
        var input = new byte[] { 0x73, 0x6C, 0x68, 0xFF, 0xAA };
        Assert.Same(input, AllegroLzssDecoder.Decode(input));
    }

    [Fact]
    public void Decode_PackedRoundTrip_MixedLiteralsAndBackrefs_Decodes()
    {
        // 'slh!' magic + flags=0xF8 (5 literals then 3 back-refs) + literals "DotChr"+EOS.
        // Then a back-ref pos=2, len=Threshold+1=3 copying from window offset 2.
        var input = new byte[]
        {
            0x73, 0x6C, 0x68, 0x21,
            0x07,                     // flags: 0b00000111 → three literals then back-refs
            (byte)'X', (byte)'Y', (byte)'Z',
            0x00, 0x00                // back-ref pos=0, len=3 → "   "
        };
        var output = AllegroLzssDecoder.Decode(input);
        Assert.Equal("XYZ   "u8.ToArray(), output);
    }

    /// <summary>
    ///     A crafted `.eof` is an ordinary decompression bomb: the loop consumes
    ///     its input monotonically, so the output was not infinite, but its worst
    ///     case is 8 match pairs (16 bytes) plus a flag byte producing 8 × 18 =
    ///     144 bytes — ~8.5× — and <c>ToArray()</c> then DOUBLES the peak. A
    ///     200 MB file therefore cost ~1.7 GB of stream plus a ~1.7 GB copy with
    ///     nothing between it and an OOM.
    ///     <para>
    ///         The ceiling is passed in here so the gate does not have to
    ///         allocate the shipped 256 MiB to watch it bite; the shipped VALUE is
    ///         asserted below.
    ///     </para>
    /// </summary>
    [Fact]
    public void Decode_PayloadThatInflatesPastTheCeiling_IsRefused()
    {
        // Every flag byte 0x00 => eight back-references, each 2 source bytes
        // producing 18 output bytes: the format's maximum amplification.
        var input = new List<byte> { 0x73, 0x6C, 0x68, 0x21 };
        for (var group = 0; group < 512; group++)
        {
            input.Add(0x00); // eight back-refs
            for (var pair = 0; pair < 8; pair++)
            {
                input.Add(0x00); // pos = 0
                input.Add(0x0F); // len = 15 + Threshold + 1 = 18
            }
        }

        var source = input.ToArray();
        // The point of the fixture: ~8.5 KB in, ~74 KB out.
        Assert.True(source.Length < 10 * 1024);

        var ex = Assert.Throws<InvalidDataException>(
            () => AllegroLzssDecoder.Decode(source, maxDecodedBytes: 4096));
        Assert.Contains("decompressed past", ex.Message, StringComparison.Ordinal);

        // …and the SAME payload under a ceiling that fits still decodes whole.
        var decoded = AllegroLzssDecoder.Decode(source, maxDecodedBytes: 1024 * 1024);
        Assert.Equal(512 * 8 * 18, decoded.Length);
    }

    /// <summary>The shipped ceiling: two orders of magnitude above a real
    /// Editor-on-Fire project, so it only ever bites a crafted file.</summary>
    [Fact]
    public void TheShippedCeilingIsGenerousButFinite() => Assert.Equal(256 * 1024 * 1024, AllegroLzssDecoder.MaxDecodedBytes);

    /// <summary>An ordinary payload must be untouched by the ceiling.</summary>
    [Fact]
    public void Decode_OrdinaryPayload_IsUnaffectedByTheCeiling()
    {
        var input = new byte[]
        {
            0x73, 0x6C, 0x68, 0x21,
            0xFF,
            (byte)'A', (byte)'B', (byte)'C', (byte)'D',
            (byte)'E', (byte)'F', (byte)'G', (byte)'H'
        };
        Assert.Equal("ABCDEFGH"u8.ToArray(), AllegroLzssDecoder.Decode(input));
    }

    [Fact]
    public void Decode_PackedBackReferenceFromInitialWindow_ProducesSpaces()
    {
        // The LZSS sliding window is initialised with 0x20 (' ') for the first N-F bytes.
        // A back-reference to position 0, length 3 (Threshold+1=3) should emit "   ".
        // Flag byte 0x00 → all eight pairs are back-references; we only consume one pair then break on EOS.
        var input = new byte[]
        {
            0x73, 0x6C, 0x68, 0x21,
            0x00, // all back-refs
            0x00, 0x00 // pos=0, len=Threshold+1=3
        };
        var output = AllegroLzssDecoder.Decode(input);
        Assert.Equal("   "u8.ToArray(), output);
    }
}
