namespace DotCharter.Tests.Services.Export.Audio;

/// <summary>The pure decision that skips an encoding fixture when the ffmpeg build has no such encoder.</summary>
public sealed class PcmDecoderFixtureEncoderTests
{
    private const string RedistList = """
        Encoders:
         V..... = Video
         A..... = Audio
         ------
         A....D libmp3lame           libmp3lame MP3 (MPEG audio layer 3) (codec mp3)
         A....D libopus              libopus Opus (codec opus)
         A....D pcm_s16le            PCM signed 16-bit little-endian
        """;

    private const string FullList = RedistList + "\n A....D aac                  AAC (Advanced Audio Coding)\n A....D libvorbis            libvorbis (codec vorbis)\n";

    [Fact]
    public void A_decode_only_redist_list_has_no_aac_but_has_opus()
    {
        Assert.False(PcmDecoderFixture.CanEncode(RedistList, "aac"));
        Assert.True(PcmDecoderFixture.CanEncode(RedistList, "libopus"));
    }

    [Fact]
    public void A_full_list_has_aac()
    {
        Assert.True(PcmDecoderFixture.CanEncode(FullList, "aac"));
        Assert.True(PcmDecoderFixture.CanEncode(FullList.Replace("\n", "\r\n"), "libvorbis"));
    }

    [Fact]
    public void A_name_that_is_only_part_of_another_encoder_is_not_that_encoder()
    {
        Assert.False(PcmDecoderFixture.CanEncode(RedistList, "opus"));
        Assert.False(PcmDecoderFixture.CanEncode(RedistList, "pcm"));
        Assert.False(PcmDecoderFixture.CanEncode(RedistList, "mp3"));
    }

    [Fact]
    public void Empty_text_encodes_nothing() => Assert.False(PcmDecoderFixture.CanEncode("", "aac"));
}
