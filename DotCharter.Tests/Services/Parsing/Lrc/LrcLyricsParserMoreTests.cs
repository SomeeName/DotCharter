using DotCharter.Services.Parsing.Lrc;

namespace DotCharter.Tests.Services.Parsing.Lrc;

public sealed class LrcLyricsParserMoreTests
{
    private static List<BeatMarker> Beats(int count = 60, double bpm = 120)
    {
        var ms = 60000.0 / bpm;
        return [.. Enumerable.Range(0, count).Select(i => new BeatMarker { Time = i * ms, Bpm = bpm, IsMeasure = i % 4 == 0 })];
    }

    [Fact]
    public void Parse_NullOrEmptyContent_ReturnsEmpty()
    {
        var parser = new LrcLyricsParser();
        Assert.Empty(parser.Parse(null, Beats()));
        Assert.Empty(parser.Parse("", Beats()));
        Assert.Empty(parser.Parse("   \n\t  ", Beats()));
    }

    [Fact]
    public void Parse_NullBeats_Throws()
    {
        var parser = new LrcLyricsParser();
        Assert.Throws<ArgumentNullException>(() => parser.Parse("[00:01.00]hi", null!));
    }

    [Fact]
    public void Parse_EmptyBeats_ReturnsEmpty()
    {
        var parser = new LrcLyricsParser();
        Assert.Empty(parser.Parse("[00:01.00]hi", []));
    }

    [Fact]
    public void Parse_LineWithoutTimestamp_IsIgnored()
    {
        var parser = new LrcLyricsParser();
        const string content = "no timestamp here\n[00:01.00]hello";
        var phrases = parser.Parse(content, Beats());
        Assert.Single(phrases);
    }

    [Fact]
    public void Parse_LineWithOnlyTimestamps_IsIgnored()
    {
        var parser = new LrcLyricsParser();
        const string content = "[00:01.00]   \n[00:02.00]hello";
        var phrases = parser.Parse(content, Beats());
        Assert.Single(phrases);
    }

    [Fact]
    public void Parse_OffsetThatMovesALineBeforeZero_DropsIt()
    {
        var parser = new LrcLyricsParser();
        // A positive offset makes lyrics appear sooner (stamp - offset):
        // 500 ms - 1000 = -500, dropped; 2000 - 1000 = 1000, kept.
        const string content = "[offset:1000]\n[00:00.50]drop\n[00:02.00]keep";
        var phrases = parser.Parse(content, Beats());
        Assert.Single(phrases);
        Assert.Equal(1000, phrases[0].Position.GetPositionMs(Beats()), 0);
    }

    [Fact]
    public void Parse_SortsLinesByTime_RegardlessOfFileOrder()
    {
        var parser = new LrcLyricsParser();
        const string content = "[00:03.00]third\n[00:01.00]first\n[00:02.00]second";
        var phrases = parser.Parse(content, Beats());
        Assert.Equal(3, phrases.Count);
        Assert.Equal("first", phrases[0].Lyrics[0].Text);
        Assert.Equal("second", phrases[1].Lyrics[0].Text);
        Assert.Equal("third", phrases[2].Lyrics[0].Text);
    }

    [Fact]
    public void Parse_LastLyricInPhrase_IsMarkedAsPhraseEnd()
    {
        var parser = new LrcLyricsParser();
        const string content = "[00:01.00]a b c\n[00:05.00]end";
        var phrases = parser.Parse(content, Beats());
        Assert.False(phrases[0].Lyrics[0].IsPhraseEnd);
        Assert.False(phrases[0].Lyrics[1].IsPhraseEnd);
        Assert.True(phrases[0].Lyrics[2].IsPhraseEnd);
    }

    [Fact]
    public void Parse_TimestampWithColonFraction_IsIgnored()
    {
        var parser = new LrcLyricsParser();
        // [mm:ss:ff] form — outer regex matches but inner parser only accepts
        // mm:ss[.ms], so the colon-fraction stamp is dropped silently.
        const string content = "[00:01:50]bad\n[00:02.00]good";
        var phrases = parser.Parse(content, Beats());
        Assert.Single(phrases);
        Assert.Equal("good", phrases[0].Lyrics[0].Text);
    }

    [Fact]
    public void Parse_TimestampWithSingleDigitFraction_TreatedAsTenths()
    {
        var parser = new LrcLyricsParser();
        const string content = "[00:01.5]hi";
        var phrases = parser.Parse(content, Beats());
        Assert.Equal(1500, phrases[0].Position.GetPositionMs(Beats()), 0);
    }

    [Fact]
    public void Parse_BadlyFormedTimestamp_IsIgnored()
    {
        var parser = new LrcLyricsParser();
        // single number, no colon — regex won't match at all
        const string content = "[123]invalid\n[00:02.00]good";
        var phrases = parser.Parse(content, Beats());
        Assert.Single(phrases);
        Assert.Equal("good", phrases[0].Lyrics[0].Text);
    }

    [Fact]
    public void Parse_CrLfLineEndings_AreNormalized()
    {
        var parser = new LrcLyricsParser();
        const string content = "[00:01.00]one\r\n[00:02.00]two";
        var phrases = parser.Parse(content, Beats());
        Assert.Equal(2, phrases.Count);
    }
}
