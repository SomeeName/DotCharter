using System.Buffers.Binary;
using DotCharter.Services.Export.Sng;
using DotCharter.Services.Export.Sng.Model;
using DotCharter.Services.Infrastructure.Psarc.Write;

namespace DotCharter.Tests.Services.Export;

/// <summary>The pins of <see cref="SngWriterBytePinTests" />; the synthetic model and the pin helpers are in <c>SngWriterBytePinTests.Fixtures.cs</c>, which the plugin's reader tests share.</summary>
public sealed partial class SngWriterBytePinTests
{
    [Fact]
    public void The_plain_body_of_the_synthetic_sng_is_pinned()
    {
        var body = RocksmithSngWriter.WriteUnpacked(BuildSyntheticSng());
        Assert.Equal(body, RocksmithSngWriter.WriteUnpacked(BuildSyntheticSng()));
        AssertPinned(PinnedBody, body, "plain body");
    }

    /// <summary>The symbol sections are written only after vocals.</summary>
    [Fact]
    public void A_body_without_vocals_omits_the_symbol_sections()
    {
        var body = RocksmithSngWriter.WriteUnpacked(WithoutVocals());
        AssertPinned(PinnedBodyWithoutVocals, body, "plain body without vocals");
        Assert.True(body.Length < RocksmithSngWriter.WriteUnpacked(BuildSyntheticSng()).Length);
    }

    [Fact]
    public void The_packed_container_is_pinned_and_lays_out_header_payload_and_trailer()
    {
        var body = RocksmithSngWriter.WriteUnpacked(BuildSyntheticSng());
        var packed = SngPacker.Pack(body, TestRocksmithKeys.SyntheticSngPc, FixedIv);

        Assert.Equal(packed, SngPacker.Pack(body, TestRocksmithKeys.SyntheticSngPc, FixedIv));
        Assert.Equal(0x4Au, BinaryPrimitives.ReadUInt32LittleEndian(packed));
        Assert.Equal(SngFlags.Compressed | SngFlags.Encrypted, BinaryPrimitives.ReadUInt32LittleEndian(packed.AsSpan(4)));
        Assert.Equal(FixedIv, packed[8..24]);
        Assert.All(packed[^56..], b => Assert.Equal(0, b));
        AssertPinned(PinnedPackedPc, packed, "packed SNG");

        // CTR under another key: same header and length, another payload.
        var mac = SngPacker.Pack(body, TestRocksmithKeys.SyntheticSngMac, FixedIv);
        Assert.Equal(packed[..24], mac[..24]);
        Assert.Equal(packed.Length, mac.Length);
        Assert.NotEqual(packed, mac);
        AssertPinned(PinnedPackedMac, mac, "packed SNG, Mac key");
    }

    [Fact]
    public void The_plain_container_is_pinned_and_carries_the_body_length_and_a_zlib_stream()
    {
        var body = RocksmithSngWriter.WriteUnpacked(BuildSyntheticSng());
        var plain = SngPacker.PackPlain(body);

        Assert.Equal(SngFlags.Compressed, BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(4)));
        Assert.All(plain[8..24], b => Assert.Equal(0, b));
        Assert.Equal(body.Length, BinaryPrimitives.ReadInt32LittleEndian(plain.AsSpan(24)));
        Assert.Equal(0x78, plain[28]);
        Assert.All(plain[^56..], b => Assert.Equal(0, b));
        AssertPinned(PinnedPlainPc, plain, "plain SNG");
    }

    /// <summary>The writer's entry points are the packer under the user's keys.</summary>
    [Fact]
    public void The_writer_packs_with_the_users_pc_and_mac_keys()
    {
        using var keys = TestRocksmithKeys.UseSynthetic();
        var sng = BuildSyntheticSng();
        var body = RocksmithSngWriter.WriteUnpacked(sng);

        Assert.Equal(SngPacker.Pack(body, TestRocksmithKeys.SyntheticSngPc, FixedIv),
            Write(s => RocksmithSngWriter.WritePackedPc(sng, s, FixedIv)));
        Assert.Equal(SngPacker.Pack(body, TestRocksmithKeys.SyntheticSngMac, FixedIv),
            Write(s => RocksmithSngWriter.WritePackedMac(sng, s, FixedIv)));
        Assert.Equal(SngPacker.PackPlain(body), Write(s => RocksmithSngWriter.WritePlainPc(sng, s)));
    }

    /// <summary>Without an IV the writer draws one per SNG: same layout, another payload.</summary>
    [Fact]
    public void Without_an_iv_each_pack_draws_its_own()
    {
        using var keys = TestRocksmithKeys.UseSynthetic();
        var sng = BuildSyntheticSng();
        var first = Write(s => RocksmithSngWriter.WritePackedPc(sng, s));
        var second = Write(s => RocksmithSngWriter.WritePackedPc(sng, s));

        Assert.Equal(first.Length, second.Length);
        Assert.Equal(first[..8], second[..8]);
        Assert.NotEqual(first[8..24], second[8..24]);
        Assert.Equal(SngPacker.Pack(RocksmithSngWriter.WriteUnpacked(sng), TestRocksmithKeys.SyntheticSngPc, first[8..24]), first);
    }

    [Fact]
    public void Without_keys_the_writer_refuses_to_encrypt()
    {
        using var keys = TestRocksmithKeys.UseNoKeys();
        Assert.Throws<InvalidOperationException>(() => Write(s => RocksmithSngWriter.WritePackedPc(BuildSyntheticSng(), s)));
        Assert.Throws<InvalidOperationException>(() => PsarcTocEncryptor.Encrypt(new byte[32]));
    }

    /// <summary>The TOC encryptor is AES-CFB with a zero IV, cut to the input length.</summary>
    [Fact]
    public void The_toc_encryption_is_pinned()
    {
        var toc = Enumerable.Range(0, 77).Select(i => (byte)(i * 13)).ToArray();
        var encrypted = PsarcTocEncryptor.Encrypt(toc, TestRocksmithKeys.SyntheticToc);

        Assert.Equal(toc.Length, encrypted.Length);
        Assert.NotEqual(toc, encrypted);
        AssertPinned(PinnedToc, encrypted, "encrypted TOC");
        using var keys = TestRocksmithKeys.UseSynthetic();
        Assert.Equal(encrypted, PsarcTocEncryptor.Encrypt(toc));
    }

    /// <summary>A whole archive, plain and encrypted, from fixed entries: a small one and one
    /// that spans two 64 KiB blocks.</summary>
    [Theory]
    [InlineData(false, PinnedArchivePlain)]
    [InlineData(true, PinnedArchiveEncrypted)]
    public void A_psarc_from_fixed_entries_is_pinned(bool encryptToc, string pinned)
    {
        using var keys = TestRocksmithKeys.UseSynthetic();
        var archive = WriteArchive(encryptToc);
        Assert.Equal(archive, WriteArchive(encryptToc));
        Assert.Equal("PSAR"u8.ToArray(), archive[..4]);
        AssertPinned(pinned, archive, encryptToc ? "encrypted archive" : "plain archive");
    }
}
