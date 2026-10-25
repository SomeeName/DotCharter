namespace DotCharter.Tests.Services.Audio.Lyrics.Probes;

/// <summary>
///     The lyric probes' numbers (#237) are only as good as their arithmetic, and the
///     probes themselves never run in the gate; this pins the arithmetic and the corpus
///     format they read.
/// </summary>
public sealed class LyricProbeReportTests
{
    [Fact]
    public void Stats_AreSignedShares_WithLateMeaningMoreThan50MsLate()
    {
        var stats = LyricProbeReport.Of([-0.200, -0.040, 0.0, 0.030, 0.060, 0.090, 0.150, 0.400]);

        Assert.Equal(8, stats.Count);
        Assert.Equal(37.5, stats.Within50, 6);   // -40, 0, +30
        Assert.Equal(62.5, stats.Within100, 6);  // and +60, +90
        Assert.Equal(50.0, stats.LateShare, 6);  // +60, +90, +150, +400
        Assert.Equal(25.0, stats.EarlyShare, 6); // -200, -40
        Assert.Equal(0.030, stats.Median, 6);    // nearest rank of 8: index 3
        Assert.Equal(0.200, stats.P90Absolute, 6);
    }

    [Fact]
    public void Compare_IsIndexAligned_AndEmptyWhenTheCountsDiffer()
    {
        LyricTruthSyllable[] truth = [new("a", false, false, 1.0, 1.5), new("ka", false, true, 2.0, 2.4)];

        var errors = LyricProbeReport.Compare(truth, [(1.1, 1.4), (1.9, 2.6)]);

        Assert.Equal([0.1, -0.1], errors.Select(e => Math.Round(e.Onset, 6)));
        Assert.Equal([-0.1, 0.2], errors.Select(e => Math.Round(e.End, 6)));
        Assert.Equal([false, true], errors.Select(e => e.PhraseFinal));
        Assert.Empty(LyricProbeReport.Compare(truth, [(1.1, 1.4)]));
    }

    [Fact]
    public void Rows_GroupOnsetsByHowTheSyllableOpens_AndEndsByPhraseFinal()
    {
        List<LyricSyllableError> errors =
        [
            new("a", false, 0.080, 0.0),
            new("ma", false, 0.010, 0.0),
            new("ka", true, -0.020, 0.300),
            new("shi", false, 0.0, 0.0)
        ];

        var rows = LyricProbeReport.Rows(errors).ToDictionary(r => r.Label, r => r.Stats);

        Assert.Equal(4, rows["onset, all"].Count);
        Assert.Equal(1, rows["onset, vowel-initial"].Count);
        Assert.Equal(1, rows["onset, voiced-initial"].Count);
        Assert.Equal(2, rows["onset, unvoiced-initial"].Count);
        Assert.Equal(4, rows["end, all"].Count);
        Assert.Equal(1, rows["end, phrase-final"].Count);
        Assert.Equal(100.0, rows["end, phrase-final"].LateShare, 6);
    }

    [Fact]
    public void Table_IsOneMarkdownRowPerEntry_InMilliseconds()
    {
        var table = LyricProbeReport.Table([("end, all", LyricProbeReport.Of([0.020, 0.120]))]);

        Assert.Contains("| end, all | 2 | +20 ms | 20 ms | 50.0% | 50.0% | 50.0% | 0.0% |", table);
    }

    [Fact]
    public void Corpus_ReadsTheChartedVocalsAndGrid_AndRoundTripsItsSources()
    {
        var folder = Path.Combine(Path.GetTempPath(), "dc-lyric-corpus-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var vocals = Path.Combine(folder, "vocals.xml");
            File.WriteAllText(vocals, """
                <vocals count="3">
                  <vocal time="1.500" note="254" length="0.250" lyric="ka-" />
                  <vocal time="1.000" note="254" length="0.400" lyric="ze+" />
                  <vocal time="2.000" note="254" length="0.100" lyric="a" />
                </vocals>
                """);
            var grid = Path.Combine(folder, "lead.xml");
            File.WriteAllText(grid, """
                <song><ebeats><ebeat time="0.000" measure="1" /><ebeat time="0.500" measure="-1" /></ebeats></song>
                """);

            var syllables = LyricProbeCorpus.ReadVocalXml(vocals);
            var beats = LyricProbeCorpus.ReadBeatXml(grid);

            Assert.Equal(["ze", "ka", "a"], syllables.Select(s => s.Text));
            Assert.Equal(new LyricTruthSyllable("ka", true, false, 1.5, 1.75), syllables[1]);
            Assert.True(syllables[0].EndsLine);
            Assert.Equal("ze\nka-a ", LyricProbeCorpus.ToPasteText(syllables));
            Assert.Equal([new LyricCorpusBeat(0.0, true), new LyricCorpusBeat(0.5, false)], beats);

            var source = new LyricTruthSource("A Song: Live", "english", "mix.wav", [.. beats], [.. syllables]);
            LyricProbeCorpus.WriteSource(folder, source);
            var read = Assert.Single(LyricProbeCorpus.ReadSources(folder));
            Assert.Equal(source.Title, read.Title);
            Assert.Equal(source.Syllables, read.Syllables);
            Assert.Equal(source.Beats, read.Beats);
            Assert.Equal("a-song-live", LyricProbeCorpus.Slugify(source.Title));

            var song = new LyricCorpusSong("a-song-live", source.Title, source.Language, source.Beats, source.Syllables);
            var markers = song.BeatMarkers();
            Assert.Equal([0.0, 500.0], markers.Select(b => b.Time));
            Assert.Equal(120.0, markers[0].Bpm, 6);
            Assert.True(markers[0].IsMeasure);

            var audio = LyricProbeCorpus.AudioPath(folder, song.Slug, "mix");
            LyricProbeCorpus.WriteAudio(audio, [0.25f, -0.5f]);
            Assert.Equal([0.25f, -0.5f], LyricProbeCorpus.ReadAudio(audio));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
