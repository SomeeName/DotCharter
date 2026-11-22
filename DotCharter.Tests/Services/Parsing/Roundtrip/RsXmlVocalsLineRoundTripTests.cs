using DotCharter.Core.Domain.Vocals;
using DotCharter.Services.Export.Validation;
using DotCharter.Services.Parsing.Xml.Vocals;

namespace DotCharter.Tests.Services.Parsing.Roundtrip;

/// <summary>
///     Model → Rocksmith vocals XML → model keeps the same lyric LINES: a phrase end on a word
///     part, a line end whose text ends in a hyphen, a joined line across a long silence, a line whose
///     syllables are not in time order, two lines that overlap in time and text that ends in a
///     marker the vocals XML cannot keep as text (reported) or keeps behind the writer's own
///     line end (not reported).
/// </summary>
public sealed class RsXmlVocalsLineRoundTripTests
{
    [Fact]
    public void A_phrase_end_set_on_a_word_part_survives_the_round_trip_as_its_own_line()
    {
        var project = Project([Line(Syllable(2, "hel", wordPart: true), Syllable(3, "lo"))]);
        var track = project.VocalTracks[0];
        var history = new HistoryService(new FakeAppLogger());
        var hel = track.Phrases[0].Lyrics[0];

        Assert.True(history.Do(new SetLyricPhraseEndAction(track.Phrases, track.Phrases[0], hel, true)));

        Assert.False(hel.IsWordPart);
        Assert.Equal(["hel", "lo"], Lines(RoundTrip(project)));

        history.Undo();
        Assert.True(hel.IsWordPart);
        Assert.Equal(["hel lo"], Lines(track));
        history.Redo();
        Assert.False(hel.IsWordPart);
    }

    [Fact]
    public void A_stored_syllable_that_is_both_a_word_part_and_a_phrase_end_exports_as_a_phrase_end()
    {
        var both = Syllable(2, "hel", wordPart: true);
        both.IsPhraseEnd = true;
        var project = Project([Line(both), Line(Syllable(4, "lo"))]);

        var written = new RsXmlVocalsWriter().Write(project).Root!.Elements("vocal")
            .Select(v => v.Attribute("lyric")!.Value);

        Assert.Equal(["hel+", "lo+"], written);
        Assert.Equal(["hel", "lo"], Lines(RoundTrip(project)));
    }

    [Fact]
    public void A_line_end_whose_text_ends_in_a_hyphen_keeps_its_line_and_its_text()
    {
        var project = Project([Line(Syllable(2, "la"), Syllable(3, "hel-")), Line(Syllable(5, "lo"))]);

        var written = new RsXmlVocalsWriter().Write(project).Root!.Elements("vocal")
            .Select(v => v.Attribute("lyric")!.Value);

        Assert.Equal(["la", "hel-+", "lo+"], written);
        Assert.Equal(["la hel-", "lo"], Lines(RoundTrip(project)));
    }

    [Fact]
    public void Lines_that_overlap_in_time_keep_their_membership_through_the_vocals_xml()
    {
        var project = Project([
            Line(Syllable(2, "a"), Syllable(8, "b")),
            Line(Syllable(4, "c"), Syllable(6, "d"))
        ]);

        Assert.Equal(["a b", "c d"], Lines(RoundTrip(project)));
    }

    [Fact]
    public void The_export_check_blocks_lines_that_overlap_in_time()
    {
        var project = Project([
            Line(Syllable(2, "a"), Syllable(8, "b")),
            Line(Syllable(4, "c"), Syllable(6, "d"))
        ]);

        var finding = Assert.Single(new ChartExportValidator().Validate(project),
            f => f.MessageKey == ChartExportValidator.KeyLyricsOutOfOrder);

        Assert.Equal(ExportFindingSeverity.Error, finding.Severity);
        Assert.Equal("c d", finding.Detail);
    }

    [Fact]
    public void The_export_check_passes_lines_that_follow_each_other()
    {
        var project = Project([Line(Syllable(2, "a"), Syllable(4, "b")), Line(Syllable(4, "c"), Syllable(6, "d"))]);

        Assert.DoesNotContain(new ChartExportValidator().Validate(project),
            f => f.MessageKey == ChartExportValidator.KeyLyricsOutOfOrder);
    }

    [Fact]
    public void A_blank_syllable_that_ends_a_line_keeps_the_line_break()
    {
        var blank = Syllable(3, "");
        var project = Project([Line(Syllable(2, "a"), blank, Syllable(4, "b"))]);
        blank.IsPhraseEnd = true;

        Assert.Equal(["a", "b"], Lines(RoundTrip(project)));
    }

    [Fact]
    public void A_joined_line_across_a_long_silence_stays_one_line()
    {
        var project = Project([Line(Syllable(2, "one"), Syllable(30, "two"))]);

        Assert.Equal(["one two"], Lines(RoundTrip(project)));
    }

    [Fact]
    public void A_file_without_any_phrase_end_marker_still_splits_at_a_long_silence()
    {
        var phrases = RsVocalPhraseBuilder.Build(
            [new RsVocalSyllable(0, 500, "one", 60), new RsVocalSyllable(1000, 1500, "two", 60),
                new RsVocalSyllable(9000, 9500, "three", 60)],
            ms => TimelineMath.PositionFromMs(ms, Beats()));

        Assert.Equal(["one two", "three"], phrases.Select(p => string.Join(" ", p.Lyrics.Select(l => l.Text))));
    }

    [Fact]
    public void A_line_whose_syllables_are_out_of_time_order_keeps_its_membership()
    {
        var project = Project([
            Line(Syllable(2, "a"), Syllable(6, "b"), Syllable(4, "c")),
            Line(Syllable(8, "d"))
        ]);

        Assert.Equal(["a b c", "d"], Lines(RoundTrip(project)));
    }

    [Fact]
    public void The_export_check_blocks_a_line_whose_syllables_are_out_of_time_order()
    {
        var project = Project([Line(Syllable(2, "a"), Syllable(6, "b"), Syllable(4, "c"))]);

        var finding = Assert.Single(new ChartExportValidator().Validate(project),
            f => f.MessageKey == ChartExportValidator.KeyLyricsOutOfOrder);

        Assert.Equal(ExportFindingSeverity.Error, finding.Severity);
        Assert.Equal("a b c", finding.Detail);
    }

    [Fact]
    public void The_export_check_passes_a_line_in_time_order()
    {
        var project = Project([Line(Syllable(2, "a"), Syllable(4, "b"))]);

        Assert.DoesNotContain(new ChartExportValidator().Validate(project),
            f => f.MessageKey == ChartExportValidator.KeyLyricsOutOfOrder);
    }

    [Theory]
    [InlineData("a+", false, "a", "b c")]
    [InlineData("a-", false, "a b c", null)]
    public void Text_ending_in_a_marker_inside_a_line_reads_back_as_different_lines(string text, bool wordPart,
        string firstLine, string? secondLine)
    {
        var project = Project([Line(Syllable(2, text, wordPart), Syllable(4, "b"), Syllable(6, "c"))]);

        Assert.Equal(secondLine == null ? [firstLine] : [firstLine, secondLine], Lines(RoundTrip(project)));
        Assert.NotEqual([$"{text} b c"], Lines(RoundTrip(project)));
        var finding = Assert.Single(new ChartExportValidator().Validate(project),
            f => f.MessageKey == ChartExportValidator.KeyLyricMarkerText);
        Assert.Equal(ExportFindingSeverity.Error, finding.Severity);
        Assert.Equal(text, finding.Detail);
    }

    [Theory]
    [InlineData("a-", false, true)]
    [InlineData("a+", false, true)]
    [InlineData("a-b", false, false)]
    [InlineData("a", true, false)]
    public void The_export_check_passes_a_trailing_marker_its_flag_already_carries(string text, bool wordPart,
        bool lineEnd)
    {
        var lyric = Syllable(2, text, wordPart);
        var project = Project([Line(Syllable(1, "x"), lyric), Line(Syllable(6, "y"))]);
        lyric.IsPhraseEnd = lineEnd;
        if (!lineEnd) project.VocalTracks[0].Phrases[0].Lyrics.Add(Syllable(4, "z"));
        project.VocalTracks[0].Phrases[0].Lyrics[^1].IsPhraseEnd = true;

        Assert.DoesNotContain(new ChartExportValidator().Validate(project),
            f => f.MessageKey == ChartExportValidator.KeyLyricMarkerText);
        Assert.Equal(2, RoundTrip(project).Phrases.Count);
    }

    [Theory]
    [InlineData("go-")]
    [InlineData("a+")]
    public void The_last_syllable_of_a_line_keeps_a_trailing_marker_without_its_phrase_end_flag(string text)
    {
        var last = Syllable(2, text);
        var project = Project([Line(Syllable(1, "x"), last), Line(Syllable(6, "y"))]);
        last.IsPhraseEnd = false;

        Assert.DoesNotContain(new ChartExportValidator().Validate(project),
            f => f.MessageKey == ChartExportValidator.KeyLyricMarkerText);
        Assert.Equal([$"x {text}", "y"], Lines(RoundTrip(project)));
    }

    [Fact]
    public void A_word_part_whose_text_ends_in_a_plus_keeps_its_text_and_its_line()
    {
        var project = Project([Line(Syllable(2, "a+", wordPart: true), Syllable(4, "b"))]);

        var read = RoundTrip(project).Phrases.Single().Lyrics[0];
        Assert.Equal("a+", read.Text);
        Assert.True(read.IsWordPart);
        Assert.DoesNotContain(new ChartExportValidator().Validate(project),
            f => f.MessageKey == ChartExportValidator.KeyLyricMarkerText);
    }

    [Fact]
    public void A_word_part_whose_text_ends_in_a_hyphen_is_reported_as_it_reads_back_without_it()
    {
        var project = Project([Line(Syllable(2, "a-", wordPart: true), Syllable(4, "b"))]);

        var read = RoundTrip(project).Phrases.Single().Lyrics[0];
        Assert.Equal("a", read.Text);
        Assert.True(read.IsWordPart);
        var finding = Assert.Single(new ChartExportValidator().Validate(project),
            f => f.MessageKey == ChartExportValidator.KeyLyricMarkerText);
        Assert.Equal("a-", finding.Detail);
    }

    private static List<BeatMarker> Beats()
        => [.. Enumerable.Range(0, 64).Select(i => new BeatMarker { Time = i * 500, Bpm = 120, IsMeasure = i % 4 == 0 })];

    private static ChartProject Project(IEnumerable<LyricPhrase> phrases)
    {
        var project = new ChartProject();
        foreach (var beat in Beats()) project.Beats.Add(beat);
        var track = new VocalTrack { Name = "Vocals" };
        foreach (var phrase in phrases) track.Phrases.Add(phrase);
        project.VocalTracks.Add(track);
        return project;
    }

    private static LyricPhrase Line(params LyricEvent[] lyrics)
    {
        var phrase = new LyricPhrase
        {
            Position = lyrics.Min(l => l.Position), EndPosition = lyrics.Max(l => l.EndPosition)
        };
        foreach (var lyric in lyrics) phrase.Lyrics.Add(lyric);
        phrase.Lyrics[^1].IsPhraseEnd = true;
        return phrase;
    }

    private static LyricEvent Syllable(int beat, string text, bool wordPart = false) => new()
    {
        Position = new FractionalPosition(beat), EndPosition = new FractionalPosition(beat + 1),
        Text = text, IsWordPart = wordPart, Note = 60
    };

    private static VocalTrack RoundTrip(ChartProject project)
        => new RsXmlVocalsReader().ReadDocument(new RsXmlVocalsWriter().Write(project), "vocals.xml", project.Beats)
            .VocalTracks.Single();

    private static List<string> Lines(VocalTrack track)
        => [.. track.Phrases.Select(p => string.Join(" ", p.Lyrics.Select(l => l.Text)))];
}
