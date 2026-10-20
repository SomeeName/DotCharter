using DotCharter.Core.Domain.Vocals;
using DotCharter.Services.Parsing.Lrc;

namespace DotCharter.Tests.Services.Parsing.Lrc;

public sealed class LrcLyricsWriterTests
{
    private static List<BeatMarker> Beats(int count = 40, double bpm = 120)
    {
        var ms = 60000.0 / bpm;
        return [.. Enumerable.Range(0, count).Select(i => new BeatMarker { Time = i * ms, Bpm = bpm, IsMeasure = i % 4 == 0 })];
    }

    private static VocalTrack TrackOf(params (int beat, Fraction frac, string text, bool wordPart)[] syllables)
    {
        var track = new VocalTrack();
        var phrase = new LyricPhrase();
        foreach (var (beat, frac, text, wordPart) in syllables)
        {
            var pos = new FractionalPosition(beat, frac);
            phrase.Lyrics.Add(new LyricEvent
            {
                Position = pos,
                EndPosition = pos + new Fraction(1, 2),
                Text = text,
                IsWordPart = wordPart,
                IsPhraseEnd = false
            });
        }
        if (phrase.Lyrics.Count > 0)
        {
            phrase.Position = phrase.Lyrics[0].Position;
            phrase.EndPosition = phrase.Lyrics[^1].EndPosition;
            phrase.Lyrics[^1].IsPhraseEnd = true;
        }
        track.Phrases.Add(phrase);
        return track;
    }

    [Fact]
    public void Write_EmitsEnhancedLineWithLineAndSyllableStamps()
    {
        var track = TrackOf(
            (2, Fraction.Zero, "Hel", true),
            (3, Fraction.Zero, "lo", false));

        var lrc = new LrcLyricsWriter().Write(track, Beats());

        // Line stamp equals first syllable start (beat 2 @ 120bpm = 1.000s).
        Assert.Contains("[00:01.00]", lrc);
        Assert.Contains("<00:01.00>Hel", lrc);
        Assert.Contains("<00:01.50>lo", lrc);
    }

    [Fact]
    public void Write_EncodesWordPartWithDash_AndWordBreakWithSpace()
    {
        var track = TrackOf(
            (0, Fraction.Zero, "he", true),   // joins next -> dash
            (1, Fraction.Zero, "llo", false), // ends word -> space
            (2, Fraction.Zero, "world", false));

        var lrc = new LrcLyricsWriter().Write(track, Beats());
        var line = lrc.Trim();

        Assert.Contains(">he-<", line);   // syllable joined to next
        Assert.Contains(">llo <", line);  // word break before next word
        Assert.DoesNotContain("world-", line); // final syllable never gets a trailing dash
    }

    [Fact]
    public void Write_OrdersPhrasesByTime()
    {
        var track = new VocalTrack();
        var late = new LyricPhrase { Position = new FractionalPosition(8) };
        late.Lyrics.Add(new LyricEvent { Position = new FractionalPosition(8), EndPosition = new FractionalPosition(9), Text = "late", IsPhraseEnd = true });
        var early = new LyricPhrase { Position = new FractionalPosition(1) };
        early.Lyrics.Add(new LyricEvent { Position = new FractionalPosition(1), EndPosition = new FractionalPosition(2), Text = "early", IsPhraseEnd = true });
        track.Phrases.Add(late);
        track.Phrases.Add(early);

        var lrc = new LrcLyricsWriter().Write(track, Beats());
        Assert.True(lrc.IndexOf("early", StringComparison.Ordinal) < lrc.IndexOf("late", StringComparison.Ordinal));
    }

    [Fact]
    public void Write_SkipsEmptyPhrasesAndBlankSyllables()
    {
        var track = new VocalTrack();
        track.Phrases.Add(new LyricPhrase()); // empty phrase
        var phrase = new LyricPhrase { Position = new FractionalPosition(1) };
        phrase.Lyrics.Add(new LyricEvent { Position = new FractionalPosition(1), EndPosition = new FractionalPosition(2), Text = "   " });
        phrase.Lyrics.Add(new LyricEvent { Position = new FractionalPosition(2), EndPosition = new FractionalPosition(3), Text = "real", IsPhraseEnd = true });
        track.Phrases.Add(phrase);

        var lrc = new LrcLyricsWriter().Write(track, Beats());
        // Exactly one line, only the non-blank syllable.
        var lines = lrc.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
        Assert.Contains("real", lines[0]);
    }

    [Fact]
    public void Write_IncludesIdTagsWhenProvided()
    {
        var track = TrackOf((0, Fraction.Zero, "hi", false));
        var lrc = new LrcLyricsWriter().Write(track, Beats(), title: "My Song", artist: "The Band");
        Assert.Contains("[ti:My Song]", lrc);
        Assert.Contains("[ar:The Band]", lrc);
    }

    [Fact]
    public void Write_EmptyTrack_ProducesNoLyricLines()
    {
        var lrc = new LrcLyricsWriter().Write(new VocalTrack(), Beats());
        Assert.DoesNotContain("<", lrc);
    }

    [Fact]
    public void WriteThenParse_PreservesSyllableStartsAndText_WithinTolerance()
    {
        var beats = Beats();
        var track = TrackOf(
            (2, Fraction.Zero, "Hel", true),
            (2, new Fraction(1, 2), "lo", false),
            (4, Fraction.Zero, "world", false));

        var lrc = new LrcLyricsWriter().Write(track, beats);
        var phrases = new LrcLyricsParser().Parse(lrc, beats);

        Assert.Single(phrases);
        var original = track.Phrases[0].Lyrics;
        var roundTripped = phrases[0].Lyrics;
        Assert.Equal(original.Count, roundTripped.Count);
        for (var i = 0; i < original.Count; i++)
        {
            Assert.Equal(original[i].Text, roundTripped[i].Text);
            Assert.Equal(original[i].IsWordPart, roundTripped[i].IsWordPart);
            var origMs = TimelineMath.CalculateTimeMs(original[i].Position, beats);
            var rtMs = TimelineMath.CalculateTimeMs(roundTripped[i].Position, beats);
            Assert.True(Math.Abs(origMs - rtMs) <= 15.0,
                $"syllable {i} start drifted {origMs}ms -> {rtMs}ms");
        }
    }

    [Fact]
    public void ParseThenWriteThenParse_StartTimesStable_WithinTolerance()
    {
        var beats = Beats();
        const string input = """
                             [00:01.00]<00:01.00>Hel-<00:01.50>lo <00:02.00>world
                             [00:03.00]<00:03.00>bye
                             """;

        var parser = new LrcLyricsParser();
        var first = parser.Parse(input, beats);
        var lrc = new LrcLyricsWriter().Write(first.ToList(), beats);
        var second = parser.Parse(lrc, beats);

        Assert.Equal(first.Count, second.Count);
        for (var p = 0; p < first.Count; p++)
        {
            Assert.Equal(first[p].Lyrics.Count, second[p].Lyrics.Count);
            for (var i = 0; i < first[p].Lyrics.Count; i++)
            {
                Assert.Equal(first[p].Lyrics[i].Text, second[p].Lyrics[i].Text);
                var a = TimelineMath.CalculateTimeMs(first[p].Lyrics[i].Position, beats);
                var b = TimelineMath.CalculateTimeMs(second[p].Lyrics[i].Position, beats);
                Assert.True(Math.Abs(a - b) <= 15.0, $"phrase {p} syllable {i} start drifted {a} -> {b}");
            }
        }
    }
}
