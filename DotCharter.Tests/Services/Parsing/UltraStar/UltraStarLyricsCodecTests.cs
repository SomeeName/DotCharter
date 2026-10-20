using DotCharter.Services.Parsing.UltraStar;

namespace DotCharter.Tests.Services.Parsing.UltraStar;

public sealed class UltraStarLyricsCodecTests
{
    // Built with explicit "\n" so the significant trailing spaces on
    // word-ending syllables ("lo " / "world ") are unambiguous.
    private const string Sample =
        "#TITLE:Test Song\n" +
        "#ARTIST:The Band\n" +
        "#MP3:test.mp3\n" +
        "#BPM:120\n" +
        "#GAP:0\n" +
        ": 0 4 0 Hel\n" +   // no trailing space -> mid-word
        ": 4 4 0 lo \n" +   // trailing space -> word end
        "- 8\n" +
        "* 8 4 2 world \n" +
        "E";

    [Fact]
    public void Parse_ReadsHeadersInOrder()
    {
        var song = new UltraStarLyricsParser().Parse(Sample);
        Assert.Equal("Test Song", song.GetHeader("TITLE"));
        Assert.Equal("The Band", song.GetHeader("ARTIST"));
        Assert.Equal(120, song.Bpm);
        Assert.Equal(0, song.GapMs);
        Assert.Equal("TITLE", song.Headers[0].Key);
        Assert.Equal("BPM", song.Headers[3].Key);
    }

    [Fact]
    public void Parse_ReadsNotesWithTypesAndVerbatimText()
    {
        var song = new UltraStarLyricsParser().Parse(Sample);
        Assert.Equal(2, song.Lines.Count);
        Assert.Equal(2, song.Lines[0].Notes.Count);

        var hel = song.Lines[0].Notes[0];
        Assert.Equal(UltraStarNoteType.Normal, hel.Type);
        Assert.Equal(0, hel.StartBeat);
        Assert.Equal(4, hel.LengthBeats);
        Assert.Equal("Hel", hel.Text); // no trailing space -> mid-word

        var lo = song.Lines[0].Notes[1];
        Assert.Equal("lo ", lo.Text); // trailing space preserved -> word end

        var world = song.Lines[1].Notes[0];
        Assert.Equal(UltraStarNoteType.Golden, world.Type);
        Assert.Equal(2, world.Pitch);
        Assert.Equal(8, song.Lines[0].BreakBeat);
    }

    [Fact]
    public void Parse_StopsAtEndMarker()
    {
        var song = new UltraStarLyricsParser().Parse(Sample + "\n: 100 4 0 ignored\n");
        // The trailing note after E must not be parsed.
        Assert.DoesNotContain(song.Lines.SelectMany(l => l.Notes), n => n.Text.Contains("ignored"));
    }

    [Fact]
    public void Parse_BpmAndGapAcceptCommaDecimal()
    {
        var song = new UltraStarLyricsParser().Parse("#BPM:120,5\n#GAP:350,0\n: 0 1 0 x\nE");
        Assert.Equal(120.5, song.Bpm);
        Assert.Equal(350.0, song.GapMs);
    }

    [Fact]
    public void Parse_TwoNumberLineBreak_IsPreserved()
    {
        var song = new UltraStarLyricsParser().Parse(": 0 2 0 a\n- 4 6\n: 6 2 0 b\nE");
        Assert.Equal(4, song.Lines[0].BreakBeat);
        Assert.Equal(6, song.Lines[0].BreakBeat2);
    }

    [Fact]
    public void Parse_AllNoteTypes()
    {
        var song = new UltraStarLyricsParser().Parse(": 0 1 0 a\n* 1 1 0 b\nF 2 1 0 c\nR 3 1 0 d\nG 4 1 0 e\nE");
        var types = song.Lines.SelectMany(l => l.Notes).Select(n => n.Type).ToArray();
        Assert.Equal(
        [
            UltraStarNoteType.Normal, UltraStarNoteType.Golden, UltraStarNoteType.Freestyle,
            UltraStarNoteType.Rap, UltraStarNoteType.RapGolden
        ], types);
    }

    [Fact]
    public void Parse_NegativePitch_IsSupported()
    {
        var song = new UltraStarLyricsParser().Parse(": 0 2 -5 low\nE");
        Assert.Equal(-5, song.Lines[0].Notes[0].Pitch);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   \n  \n")]
    public void Parse_EmptyOrWhitespace_ReturnsEmptySong(string? content)
    {
        var song = new UltraStarLyricsParser().Parse(content);
        Assert.Empty(song.Headers);
        Assert.Empty(song.Lines);
    }

    [Fact]
    public void Parse_MalformedNoteLines_AreSkipped()
    {
        // Missing integers / non-numeric fields must not throw and must be dropped.
        var song = new UltraStarLyricsParser().Parse(": notanumber\n: 0 4\n: 5 2 0 good\nE");
        Assert.Single(song.Lines.SelectMany(l => l.Notes));
        Assert.Equal("good", song.Lines[0].Notes[0].Text);
    }

    [Fact]
    public void Parse_UnknownLines_AreIgnored()
    {
        var song = new UltraStarLyricsParser().Parse("P1\n: 0 1 0 a\nP2\nsome junk\nE");
        Assert.Single(song.Lines.SelectMany(l => l.Notes));
    }

    [Fact]
    public void Write_ProducesParsableStructurallyEqualOutput()
    {
        var parser = new UltraStarLyricsParser();
        var song1 = parser.Parse(Sample);
        var text = new UltraStarLyricsWriter().Write(song1);
        var song2 = parser.Parse(text);
        AssertSongsEqual(song1, song2);
    }

    [Fact]
    public void Write_EndsWithEMarker()
    {
        var song = new UltraStarLyricsParser().Parse(Sample);
        var text = new UltraStarLyricsWriter().Write(song);
        Assert.EndsWith("E\n", text);
    }

    [Fact]
    public void RoundTrip_PreservesTrailingSpacesAndBeats()
    {
        var parser = new UltraStarLyricsParser();
        var text = new UltraStarLyricsWriter().Write(parser.Parse(Sample));
        // "lo " keeps its trailing space; the beat/pitch integers are stable.
        Assert.Contains(": 4 4 0 lo \n", text);
        Assert.Contains("* 8 4 2 world \n", text);
        Assert.Contains("- 8\n", text);
    }

    // ─── Duets and relative mode (#332) ──────────────────────────────────────
    // A duet's body starts with a voice change ("P1") and switches with "P2"; a
    // voice change does NOT add a line break. In #RELATIVE:YES files every line
    // counts from the previous break: "- 8 12" breaks at rel+8 and moves rel by 12.

    private const string Duet =
        "#TITLE:Duet\n" +
        "#BPM:120\n" +
        "#GAP:0\n" +
        "#P1:Anna\n" +
        "#P2:Ben\n" +
        "P1\n" +
        ": 0 4 0 Hel\n" +
        ": 4 4 0 lo \n" +
        "- 10\n" +
        ": 10 4 0 there \n" +
        "P2\n" +
        ": 2 4 5 Hi \n" +
        "- 8\n" +
        ": 12 4 5 you \n" +
        "E";

    [Fact]
    public void Parse_Duet_TagsEveryLineWithItsVoice()
    {
        var song = new UltraStarLyricsParser().Parse(Duet);

        Assert.True(song.IsDuet);
        Assert.Equal([1, 1, 2, 2], song.Lines.Select(l => l.Voice));
        Assert.Equal(["Hel", "lo ", "there ", "Hi ", "you "], song.Lines.SelectMany(l => l.Notes).Select(n => n.Text));
        // P1's last line ends at the voice change, not at a "-".
        Assert.Null(song.Lines[1].BreakBeat);
    }

    [Fact]
    public void RoundTrip_Duet_KeepsTheVoiceChanges()
    {
        var parser = new UltraStarLyricsParser();
        var song1 = parser.Parse(Duet);
        var text = new UltraStarLyricsWriter().Write(song1);

        Assert.Contains("P1\n: 0 4 0 Hel\n", text);
        Assert.Contains(": 10 4 0 there \nP2\n: 2 4 5 Hi \n", text);
        AssertSongsEqual(song1, parser.Parse(text));
    }

    [Fact]
    public void ToAbsoluteBeats_RelativeSong_CountsEachLineFromThePreviousBreak()
    {
        var song = new UltraStarLyricsParser().Parse(
            "#BPM:120\n#GAP:0\n#RELATIVE:yes\n" +
            ": 0 4 0 One \n: 4 4 0 two \n- 8 12\n" +
            ": 0 4 0 Three \n: 4 4 0 four \n- 6 10\n" +
            ": 2 4 0 five \nE");

        Assert.True(song.IsRelative);
        var absolute = song.ToAbsoluteBeats();

        Assert.False(absolute.IsRelative);
        Assert.Null(absolute.GetHeader("RELATIVE"));
        Assert.Equal([0, 4, 12, 16, 24], absolute.Lines.SelectMany(l => l.Notes).Select(n => n.StartBeat));
        Assert.Equal(new int?[] { 8, 18, null }, absolute.Lines.Select(l => l.BreakBeat));
        // The parsed model itself is the file as written, so it still round-trips.
        Assert.Equal([0, 4, 0, 4, 2], song.Lines.SelectMany(l => l.Notes).Select(n => n.StartBeat));
    }

    [Fact]
    public void ToAbsoluteBeats_RelativeDuet_KeepsAnOffsetPerVoice()
    {
        var song = new UltraStarLyricsParser().Parse(
            "#BPM:120\n#RELATIVE:YES\n" +
            "P1\n: 0 2 0 a \n- 4 8\n: 0 2 0 b \n" +
            "P2\n: 1 2 0 c \n- 4 16\n: 1 2 0 d \nE");

        var absolute = song.ToAbsoluteBeats();

        Assert.Equal([0, 8, 1, 17], absolute.Lines.SelectMany(l => l.Notes).Select(n => n.StartBeat));
    }

    private static void AssertSongsEqual(UltraStarSong a, UltraStarSong b)
    {
        Assert.Equal(a.Headers, b.Headers);
        Assert.Equal(a.Lines.Count, b.Lines.Count);
        for (var i = 0; i < a.Lines.Count; i++)
        {
            Assert.Equal(a.Lines[i].Voice, b.Lines[i].Voice);
            Assert.Equal(a.Lines[i].BreakBeat, b.Lines[i].BreakBeat);
            Assert.Equal(a.Lines[i].BreakBeat2, b.Lines[i].BreakBeat2);
            Assert.Equal(a.Lines[i].Notes, b.Lines[i].Notes);
        }
    }
}
