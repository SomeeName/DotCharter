using System.Text;
using System.Xml;
using System.Xml.Linq;
using DotCharter.Services.Import.Audio;
using DotCharter.Services.Parsing;

namespace DotCharter.Tests.Services.Import.Audio;

/// <summary>
///     An audio file's tags become project metadata, and the <c>.rscp</c> is
///     XML — which has no way to spell a control character. ID3 text frames are
///     NUL-terminated and taggers routinely leave a trailing copy behind;
///     ffprobe hands it straight back inside its JSON (as <c>\u0000</c>, which
///     <c>JsonDocument</c> decodes into a real one), and <c>string.Trim()</c>
///     does not remove it — a NUL is not whitespace. The import then succeeded,
///     the chart opened, and every later SAVE threw out of <c>XmlWriter</c> with
///     an <c>ArgumentException</c> about a hexadecimal value that named no
///     field.
/// </summary>
public sealed class AudioMetadataControlCharacterTests
{
    private const char Nul = '\u0000';
    private const char ByteOrderMark = '\uFEFF';
    private const char ZeroWidthJoiner = '\u200D';

    /// <summary>The harm, stated directly: this is what
    /// <c>RscpChartSerializer.Save</c> does with the value — an
    /// <c>XmlWriter</c> at its default <c>CheckCharacters = true</c>.</summary>
    private static void WriteThroughXml(string value)
    {
        var settings = new XmlWriterSettings { OmitXmlDeclaration = true, Encoding = new UTF8Encoding(false) };
        using var stream = new MemoryStream();
        using var writer = XmlWriter.Create(stream, settings);
        new XDocument(new XElement("chart", new XAttribute("songName", value))).Save(writer);
    }

    [Fact]
    public void A_tag_with_a_trailing_nul_would_make_every_save_throw()
    {
        Assert.Throws<ArgumentException>(() => WriteThroughXml("My Song" + Nul));
        WriteThroughXml("My Song"); // …and the cleaned form does not.
    }

    [Fact]
    public void ParseFfprobeJson_strips_the_control_characters_xml_cannot_carry()
    {
        // \u0000 = the trailing NUL an ID3 text frame leaves behind;
        // \u0003 = an ID3 encoding byte a broken tagger left in the payload;
        // \u001F = a stray unit separator.
        const string json = """
        {
          "format": {
            "tags": {
              "title": "My Song\u0000",
              "artist": "\u0003The Band",
              "album": "The\u001FAlbum",
              "genre": "Rock\u0000\u0000"
            }
          }
        }
        """;

        var meta = AudioMetadataExtractor.ParseFfprobeJson(json);

        Assert.Equal("My Song", meta.Title);
        Assert.Equal("The Band", meta.Artist);
        Assert.Equal("TheAlbum", meta.Album);
        Assert.Equal("Rock", meta.Genre);

        // The point of the whole exercise: each of these now survives a save.
        foreach (var value in new[] { meta.Title, meta.Artist, meta.Album, meta.Genre })
            WriteThroughXml(value);
    }

    /// <summary>A BOM inside a UTF-8 text frame does not throw — it is worse
    /// than that, it is invisible: U+FEFF is a FORMAT character, not
    /// whitespace, so <c>Trim()</c> keeps it and the artist sorts, groups and
    /// de-duplicates as a name nobody typed.</summary>
    [Fact]
    public void ParseFfprobeJson_strips_a_byte_order_mark_that_trim_would_keep()
    {
        Assert.False(char.IsWhiteSpace(ByteOrderMark), "the premise: a BOM is not whitespace");
        Assert.NotEqual("The Band", (ByteOrderMark + "The Band").Trim());

        const string json = """
        { "format": { "tags": { "artist": "\uFEFFThe Band", "title": "My\u200BSong" } } }
        """;

        var meta = AudioMetadataExtractor.ParseFfprobeJson(json);

        Assert.Equal("The Band", meta.Artist);
        Assert.Equal("MySong", meta.Title);
    }

    /// <summary>A tag that is NOTHING but junk must read as absent, so the
    /// filename-derived fallback takes over rather than a blank title being
    /// treated as authored.</summary>
    [Fact]
    public void A_tag_made_only_of_control_characters_reads_as_absent()
    {
        const string json = """
        { "format": { "tags": { "title": "\u0000\u0000 ", "artist": "Real" } } }
        """;

        var meta = AudioMetadataExtractor.ParseFfprobeJson(json);

        Assert.Equal("", meta.Title);
        Assert.Equal("Real", meta.Artist);
    }

    // ---- the shared cleaner ------------------------------------------------

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("plain", "plain")]
    [InlineData("  padded  ", "padded")]
    [InlineData("keeps\ttab", "keeps\ttab")]
    [InlineData("keeps\nnewline", "keeps\nnewline")]
    [InlineData("del\u007Fc1\u009F", "delc1")]
    [InlineData("zero\u200Dwidth", "zerowidth")]
    [InlineData("\uFEFFbom", "bom")]
    public void Clean_keeps_what_xml_allows_and_drops_the_rest(string? input, string expected) => Assert.Equal(expected, MetadataText.Clean(input));

    [Fact]
    public void Clean_is_the_identity_on_ordinary_text()
    {
        const string value = "Motörhead — Ace of Spades (feat. \"Fast\" Eddie)";
        Assert.Equal(value, MetadataText.Clean(value));
        Assert.Equal("ab", MetadataText.Clean("a" + ZeroWidthJoiner + "b"));
    }
}
