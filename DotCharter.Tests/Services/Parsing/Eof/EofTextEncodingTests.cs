using DotCharter.Core.Domain.Vocals;
using DotCharter.Services.Parsing.Eof;

namespace DotCharter.Tests.Services.Parsing.Eof;

/// <summary>
///     #917: a lyric shown as <c>didnâ€™t</c> in Editor on Fire looked like our
///     writer's UTF-8 read as Windows-1252. EOF never changes Allegro's text format, so
///     its current format is the default <c>U_UTF8</c>; <c>eof_save_song_string_pf</c>
///     writes <c>ustrsize(text)</c> as a u16 and then those raw UTF-8 bytes, and
///     <c>eof_load_song_string_pf</c> copies the bytes back unchanged. Extended ASCII
///     (Windows-1252) only appears on EOF's lyric IMPORT path, which re-encodes every
///     byte of the imported file from Windows-1252 to UTF-8. These tests pin our
///     strings to EOF's byte layout so a "fix" to Windows-1252 fails here.
/// </summary>
public sealed class EofTextEncodingTests
{
    private static readonly List<BeatMarker> Beats = [.. Enumerable.Range(0, 64).Select(i => new BeatMarker { Time = i * 500.0 })];

    /// <summary>What <c>eof_save_song_string_pf</c> writes: u16 LE byte count, then the UTF-8 bytes, no NUL.</summary>
    [Theory]
    [InlineData("é", new byte[] { 0x02, 0x00, 0xC3, 0xA9 })]
    [InlineData("ü", new byte[] { 0x02, 0x00, 0xC3, 0xBC })]
    [InlineData("n’t", new byte[] { 0x05, 0x00, 0x6E, 0xE2, 0x80, 0x99, 0x74 })]
    [InlineData("かな", new byte[] { 0x06, 0x00, 0xE3, 0x81, 0x8B, 0xE3, 0x81, 0xAA })]
    public void A_string_is_written_as_EOF_saves_it(string text, byte[] expected)
    {
        var w = new EofPackWriter();
        w.WriteString(text);

        Assert.Equal(expected, w.ToArray());
    }

    /// <summary>A vocal body with the lyric bytes EOF itself saves reads back as the same text.</summary>
    [Fact]
    public void An_EOF_saved_lyric_reads_back_unchanged()
    {
        byte[] lyric = [0x64, 0x69, 0x64, 0x6E, 0xE2, 0x80, 0x99, 0x74]; // "didn’t" as EOF holds it.
        var w = new EofPackWriter();
        w.WriteU8(0); // tone set
        w.WriteU32(1);
        w.WriteU16((ushort)lyric.Length);
        w.WriteBytes(lyric);
        w.WriteU8(0); // lyric set
        w.WriteU8(60); // pitch
        w.WriteU32(1000);
        w.WriteU32(400);
        w.WriteU32(0); // flags
        w.WriteU16(0); // section types
        w.WriteU32(0); // custom data blocks
        var reader = new EofPackReader(w.ToArray());

        var tracks = EofVocalTrackParser.Parse(reader, Beats);

        Assert.Equal(0, reader.Remaining);
        Assert.Equal("didn’t", tracks.Single().Phrases.Single().Lyrics.Single().Text);
    }

    /// <summary>Accents, a typographic apostrophe and kana survive a whole .eof export and import.</summary>
    [Fact]
    public void Non_ascii_lyrics_and_tags_round_trip_through_an_eof_file()
    {
        string[] texts = ["café", "Grüße", "didn’t", "かな"];
        var project = EofExportTestCharts.BuildRepresentativeProject();
        project.Chart.Artist = "Zoë’s Band";
        var phrase = project.VocalTracks[0].Phrases[0];
        phrase.Lyrics.Clear();
        for (var i = 0; i < texts.Length; i++)
        {
            phrase.Lyrics.Add(new LyricEvent
            {
                Position = EofExportTestCharts.Pos(4 + i),
                EndPosition = EofExportTestCharts.Pos(4 + i, 96),
                Text = texts[i],
                Note = 60,
            });
        }

        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-eof-text-{Guid.NewGuid():N}.eof");
        File.WriteAllBytes(path, EofExporter.Export(project).Bytes);
        try
        {
            var importer = new EofImporter();
            var ids = importer.GetAvailableTracks(path).Select(t => t.Id).ToList();
            var result = importer.ImportTracksWithMetadata(path, ids);

            Assert.Equal("Zoë’s Band", result.Metadata.Artist);
            Assert.Equal(texts, result.VocalTracks.Single().Phrases.SelectMany(p => p.Lyrics).Select(l => l.Text));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
