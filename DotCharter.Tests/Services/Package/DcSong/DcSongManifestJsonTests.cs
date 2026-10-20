using System.Text;
using DotCharter.Services.Package.DcSong;

namespace DotCharter.Tests.Services.Package.DcSong;

/// <summary>The manifest's wire format: a round trip, and the shapes the strict parse refuses.</summary>
public sealed class DcSongManifestJsonTests
{
    private static readonly string Hash = new('a', 64);

    // Lists, not collection expressions: the parse returns List<T>, and the deep
    // comparison checks the runtime type too.
    private static DcSongManifest Sample() => new(1, 0, "DotCharter 0.68.0",
        new DcSongInfo("DCArtistSong", "0123456789abcdef0123456789abcdef", "Song", "Artist", "Album", "1997",
            "Charter", 183_456.25, true,
            new List<DcSongArrangementInfo>
            {
                new("Lead", "Lead", "E_STANDARD"), new("Bass", "Bass", "DROP_D")
            }),
        new DcSongAudioInfo(DcSongAudioCodec.Opus, false, 192, 45_864.5, 28_000),
        new List<DcSongFileEntry>
        {
            new("chart.rscp", DcSongFileRole.Chart, 123_456, Hash),
            new("audio/song.opus", DcSongFileRole.Audio, 4_400_000, Hash),
            new("audio/preview.opus", DcSongFileRole.Preview, 600_000, Hash),
            new("images/cover.png", DcSongFileRole.Cover, 90_000, Hash, MediaType: DcSongImages.Png),
            new("images/banner.jpg", DcSongFileRole.Banner, 80_000, Hash, MediaType: DcSongImages.Jpeg),
            new("stems/drums.opus", DcSongFileRole.Stem, 4_000_000, Hash, StemName: "drums")
        });

    [Fact]
    public void AManifest_RoundTripsThroughItsJson()
    {
        var original = Sample();
        var parsed = DcSongManifestJson.Parse(DcSongManifestJson.Write(original));

        var diffs = DeepCompare.Diff("manifest", original, parsed, new HashSet<string>());
        Assert.True(diffs.Count == 0, string.Join("\n", diffs));
        Assert.Equal(original.Files, parsed.Files);
        Assert.Equal(original.Song.Arrangements, parsed.Song.Arrangements);
    }

    [Fact]
    public void TheWireFormat_SpellsItsVersionAndRolesAsTheSpecSays()
    {
        var json = Encoding.UTF8.GetString(DcSongManifestJson.Write(Sample()));
        Assert.Contains("\"format\": \"dcsong\"", json, StringComparison.Ordinal);
        Assert.Contains("\"formatVersion\": \"1.0\"", json, StringComparison.Ordinal);
        Assert.Contains("\"role\": \"stem\"", json, StringComparison.Ordinal);
        Assert.Contains("\"codec\": \"opus\"", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""[]""")]
    [InlineData("""{ "format": "zip", "formatVersion": "1.0" }""")]
    [InlineData("""{ "format": "dcsong", "formatVersion": "one" }""")]
    [InlineData("""{ "format": "dcsong", "formatVersion": "0.9" }""")]
    [InlineData("""{ "format": "dcsong", "formatVersion": "1.0" }""")]
    [InlineData("""not json""")]
    public void AManifestThatIsNotOne_IsRefusedAsInvalid(string json) =>
        Assert.Equal(DcSongReadFailure.ManifestInvalid,
            Assert.Throws<DcSongReadException>(() => DcSongManifestJson.Parse(Encoding.UTF8.GetBytes(json))).Reason);

    [Theory]
    [InlineData("chart.rscp", "audio", "a member stored under another role's path")]
    [InlineData("audio/song.flac", "audio", "FLAC audio in an Opus package")]
    [InlineData("stems/../x.opus", "stem", "a climbing path")]
    public void AFileEntryThatBreaksTheLayout_IsRefused(string path, string role, string why)
    {
        var json = Encoding.UTF8.GetString(DcSongManifestJson.Write(Sample()))
            .Replace("\"path\": \"audio/song.opus\"", $"\"path\": \"{path}\"", StringComparison.Ordinal)
            .Replace("\"role\": \"audio\"", $"\"role\": \"{role}\"", StringComparison.Ordinal);
        var ex = Assert.Throws<DcSongReadException>(() => DcSongManifestJson.Parse(Encoding.UTF8.GetBytes(json)));
        Assert.True(ex.Reason is DcSongReadFailure.ManifestInvalid or DcSongReadFailure.UnsafePath, why);
    }

    [Fact]
    public void ACodecThatDisagreesWithLossless_IsRefused()
    {
        var json = Encoding.UTF8.GetString(DcSongManifestJson.Write(Sample()))
            .Replace("\"lossless\": false", "\"lossless\": true", StringComparison.Ordinal);
        Assert.Equal(DcSongReadFailure.ManifestInvalid,
            Assert.Throws<DcSongReadException>(() => DcSongManifestJson.Parse(Encoding.UTF8.GetBytes(json))).Reason);
    }

    [Theory]
    [InlineData("1.0", true)]
    [InlineData("1.12", true)]
    [InlineData("1", false)]
    [InlineData("1.0.0", false)]
    [InlineData("-1.0", false)]
    [InlineData(" 1.0", false)]
    public void TheVersion_IsExactlyMajorDotMinor(string text, bool valid) =>
        Assert.Equal(valid, DcSongFormat.TryParseVersion(text, out _, out _));
}
