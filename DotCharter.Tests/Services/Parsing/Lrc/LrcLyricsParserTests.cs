using DotCharter.Services.Parsing.Lrc;

namespace DotCharter.Tests.Services.Parsing.Lrc;

public sealed class LrcLyricsParserTests
{
    private static List<BeatMarker> Beats(int count = 20, double bpm = 120)
    {
        var ms = 60000.0 / bpm;
        return [.. Enumerable.Range(0, count).Select(i => new BeatMarker { Time = i * ms, Bpm = bpm, IsMeasure = i % 4 == 0 })];
    }

    // A NEGATIVE offset makes the lyrics appear LATER: 1.00 s - (-500 ms) = 1.5 s.
    [Fact]
    public void Parse_SimpleLrc_SplitsWordsAndAppliesOffset()
    {
        var parser = new LrcLyricsParser();
        const string content = """
                               [ar:Artist]
                               [offset:-500]
                               [00:01.00]he-llo wor-ld
                               [00:03.00][00:05.00]bye
                               """;

        var phrases = parser.Parse(content, Beats());

        Assert.Equal(3, phrases.Count);
        Assert.Equal(1500, phrases[0].Position.GetPositionMs(Beats()), 0);
        Assert.Collection(phrases[0].Lyrics,
            // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Local
            lyric =>
            {
                Assert.Equal("he", lyric.Text);
                Assert.True(lyric.IsWordPart);
            },
            // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Local
            lyric =>
            {
                Assert.Equal("llo", lyric.Text);
                Assert.False(lyric.IsWordPart);
            },
            // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Local
            lyric =>
            {
                Assert.Equal("wor", lyric.Text);
                Assert.True(lyric.IsWordPart);
            },
            // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Local
            lyric =>
            {
                Assert.Equal("ld", lyric.Text);
                Assert.False(lyric.IsWordPart);
            });
        Assert.Equal(3500, phrases[1].Position.GetPositionMs(Beats()), 0);
        Assert.Equal(5500, phrases[2].Position.GetPositionMs(Beats()), 0);
    }

    [Fact]
    public void Parse_EnhancedLrc_UsesInlineTimestamps()
    {
        var parser = new LrcLyricsParser();
        const string content = """
                               [00:01.00]<00:01.00>Hel <00:01.50>lo
                               [00:03.00]world
                               """;

        var beats = Beats();
        var phrases = parser.Parse(content, beats);

        Assert.Equal(2, phrases.Count);
        Assert.Collection(phrases[0].Lyrics,
            lyric => Assert.Equal(1000, lyric.Position.GetPositionMs(beats), 0),
            lyric => Assert.Equal(1500, lyric.Position.GetPositionMs(beats), 0));
        Assert.Equal("Hel", phrases[0].Lyrics[0].Text);
        Assert.Equal("lo", phrases[0].Lyrics[1].Text);
    }

    // ─── [offset:] (#331) ─────────────────────────────────────────────────────
    // The LRC definition: "+ causing lyrics to appear sooner", i.e. effective
    // time = stamp - offset — what foo_openlyrics and LyricsX do. It moves the
    // <word> stamps of an enhanced line by the same amount as the line stamp.

    [Fact]
    public void Parse_PositiveOffset_MakesTheLyricsAppearSooner()
    {
        var beats = Beats();
        var phrases = new LrcLyricsParser().Parse("[offset:+250]\n[00:02.00]hello", beats);

        Assert.Equal(1750, Assert.Single(phrases).Position.GetPositionMs(beats), 0);
    }

    [Theory]
    [InlineData(500, 9500)]
    [InlineData(-500, 10500)]
    public void Parse_EnhancedLine_OffsetMovesTheWordsWithTheirLine(int offsetMs, double expectedLineMs)
    {
        var beats = Beats(count: 40);
        var content = $"""
                       [ti:Offset test]
                       [offset:{offsetMs}]
                       [00:10.00]<00:10.00>one <00:10.40>two
                       [00:12.00]<00:12.00>three
                       """;

        var phrases = new LrcLyricsParser().Parse(content, beats);

        // A position is quantised to the beat grid, so allow less than one grid step.
        const double gridToleranceMs = 3;
        Assert.Equal(2, phrases.Count);
        var words = phrases[0].Lyrics;
        Assert.Equal(["one", "two"], words.Select(w => w.Text));
        Assert.Equal(expectedLineMs, phrases[0].Position.GetPositionMs(beats), gridToleranceMs);
        Assert.Equal(expectedLineMs, words[0].Position.GetPositionMs(beats), gridToleranceMs);
        // The words keep their own 400 ms spacing instead of stacking on the line start.
        Assert.Equal(expectedLineMs + 400, words[1].Position.GetPositionMs(beats), gridToleranceMs);
        Assert.Equal(expectedLineMs + 2000, phrases[1].Lyrics[0].Position.GetPositionMs(beats), gridToleranceMs);
    }
}
