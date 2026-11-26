using System.Globalization;
using System.Xml.Linq;
using DotCharter.Core.Domain.Vocals;
using DotCharter.Services.Audio.Lyrics;
using DotCharter.Services.Export.Lyrics;
using DotCharter.Services.Parsing.Xml.Vocals;

namespace DotCharter.Tests.Services.Export.Lyrics;

/// <summary>
///     Every Rocksmith vocals XML writer (the lyric sync's <see cref="RsVocalsSecondsWriter" />
///     and the chart's <see cref="RsXmlVocalsWriter" />) writes no length 0 and strictly rising
///     starts (#1039). Editor on Fire merges a zero-length piece into the next one when both
///     start at the same millisecond, across a line end too, and then refuses the file;
///     <see cref="EofLyricImportFaults" /> is a port of the checks in its <c>EndLyricLine</c>.
/// </summary>
public sealed class RsVocalTimingTests
{
    [Fact]
    public void A_zero_length_line_end_followed_by_the_next_line_at_the_same_start_is_spread()
    {
        var document = RsVocalsSecondsWriter.Write(
        [
            new LyricSyncSyllable(0, "ki", false, false, 0, 4.000, 4.500, 0.5, false),
            new LyricSyncSyllable(1, "ta", false, true, 0, 5.000, 5.000, 0.5, false),
            new LyricSyncSyllable(2, "mo", false, false, 1, 5.000, 5.400, 0.5, false),
            new LyricSyncSyllable(3, "ra", false, true, 1, 6.000, 6.300, 0.5, false),
        ]);

        var vocals = Vocals(document);
        AssertEofAccepts(vocals);
        Assert.Equal(("4.000", "0.500", "ki"), vocals[0]);
        Assert.Equal(("5.000", "0.025", "ta+"), vocals[1]);
        Assert.Equal(("5.025", "0.375", "mo"), vocals[2]);
        Assert.Equal(("6.000", "0.300", "ra+"), vocals[3]);
        Assert.Equal(["ki ta", "mo ra"], Lines(document));
    }

    [Fact]
    public void A_zero_length_syllable_followed_by_its_continuation_at_the_same_start_is_spread()
    {
        var document = RsVocalsSecondsWriter.Write(
        [
            new LyricSyncSyllable(0, "lo", false, false, 0, 7.000, 7.800, 0.5, false),
            new LyricSyncSyllable(1, "zu", true, false, 0, 8.000, 8.000, 0.5, false),
            new LyricSyncSyllable(2, "pel", false, false, 0, 8.000, 8.300, 0.5, false),
            new LyricSyncSyllable(3, "wim", false, true, 0, 9.000, 9.200, 0.5, false),
        ]);

        var vocals = Vocals(document);
        AssertEofAccepts(vocals);
        Assert.Equal(("7.000", "0.800", "lo"), vocals[0]);
        Assert.Equal(("8.000", "0.025", "zu-"), vocals[1]);
        Assert.Equal(("8.025", "0.275", "pel"), vocals[2]);
        Assert.Equal(("9.000", "0.200", "wim+"), vocals[3]);
        Assert.Equal(["lo zupel wim"], Lines(document));
    }

    [Fact]
    public void The_chart_writer_spreads_both_cases_and_leaves_the_chart_alone()
    {
        // 120 BPM: beat n starts at n * 500 ms.
        var lineEnd = Syllable(2, 2, "ta");
        var continued = Syllable(6, 6, "zu", wordPart: true);
        var project = Project(
            Line(Syllable(1, 2, "ki"), lineEnd),
            Line(Syllable(2, 3, "mo"), Syllable(4, 5, "ra"), continued, Syllable(6, 7, "pel")));

        var document = new RsXmlVocalsWriter().Write(project);

        var vocals = Vocals(document);
        AssertEofAccepts(vocals);
        Assert.Equal(("0.500", "0.500", "ki"), vocals[0]);
        Assert.Equal(("1.000", "0.025", "ta+"), vocals[1]);
        Assert.Equal(("1.025", "0.475", "mo"), vocals[2]);
        Assert.Equal(("2.000", "0.500", "ra"), vocals[3]);
        Assert.Equal(("3.000", "0.025", "zu-"), vocals[4]);
        Assert.Equal(("3.025", "0.475", "pel+"), vocals[5]);

        Assert.Equal(lineEnd.Position, lineEnd.EndPosition);
        Assert.Equal(continued.Position, continued.EndPosition);

        var read = new RsXmlVocalsReader().ReadDocument(document, "vocals.xml", project.Beats).VocalTracks.Single();
        Assert.Equal(["ki ta", "mo ra zupel"], Lines(read));
    }

    [Fact]
    public void A_zero_length_syllable_with_its_own_start_gets_the_minimum_or_the_gap()
    {
        int[] times = [1000, 1010, 2000];
        int[] lengths = [0, 0, 0];

        VocalTiming.Normalize(times, lengths);

        Assert.Equal([1000, 1010, 2000], times);
        Assert.Equal([10, VocalTiming.MinLengthMs, VocalTiming.MinLengthMs], lengths);
    }

    [Fact]
    public void More_shared_starts_than_room_still_rise_strictly_and_never_cross()
    {
        int[] times = [1000, 1000, 1000, 1002, 1500];
        int[] lengths = [0, 0, 300, 100, 50];

        VocalTiming.Normalize(times, lengths);

        for (var i = 1; i < times.Length; i++) Assert.True(times[i] > times[i - 1], $"start {i} does not rise");
        Assert.All(lengths, l => Assert.True(l > 0));
        for (var i = 0; i + 1 < times.Length; i++)
            Assert.True(times[i] + lengths[i] <= times[i + 1], $"entry {i} crosses the next start");
        Assert.Equal(1500, times[4]);
        Assert.Equal(50, lengths[4]);
    }

    /// <summary>An earlier start is a line out of time order, which the export check blocks; it is not moved.</summary>
    [Fact]
    public void An_earlier_start_is_left_where_it_is_sung()
    {
        int[] times = [2000, 1000, 3000];
        int[] lengths = [400, 0, 300];

        VocalTiming.Normalize(times, lengths);

        Assert.Equal([2000, 1000, 3000], times);
        Assert.Equal([400, VocalTiming.MinLengthMs, 300], lengths);
    }

    [Fact]
    public void Clean_timing_is_written_unchanged()
    {
        int[] times = [0, 500, 900, 1400];
        int[] lengths = [400, 400, 600, 10];

        VocalTiming.Normalize(times, lengths);

        Assert.Equal([0, 500, 900, 1400], times);
        Assert.Equal([400, 400, 600, 10], lengths);
    }

    private static void AssertEofAccepts(List<(string Time, string Length, string Lyric)> vocals)
    {
        Assert.Empty(EofLyricImportFaults(vocals));
        Assert.All(vocals, v => Assert.NotEqual("0.000", v.Length));
    }

    /// <summary>
    ///     The checks Editor on Fire's <c>EndLyricLine</c> (foflc <c>Lyric_storage.c</c>) runs over
    ///     consecutive pieces, the previous line's last piece included: a start before the previous
    ///     one aborts; an overlap is shortened to the next start; a duration of 0 at the next
    ///     piece's start merges the two pieces (what breaks a line end); and a line's last piece of
    ///     length 0 fails <c>RecountLineVars</c>. Times are whole milliseconds, as EOF reads them.
    /// </summary>
    private static List<string> EofLyricImportFaults(List<(string Time, string Length, string Lyric)> vocals)
    {
        var faults = new List<string>();
        var pieces = vocals.Select(v => (Start: Ms(v.Time), Duration: Ms(v.Length), v.Lyric)).ToList();
        for (var i = 0; i < pieces.Count; i++)
        {
            var (start, duration, lyric) = pieces[i];
            if (i + 1 == pieces.Count)
            {
                if (duration == 0) faults.Add($"{lyric}: last piece of length 0");
                continue;
            }

            var next = pieces[i + 1];
            if (start > next.Start) faults.Add($"{next.Lyric}: starts before {lyric}");
            if (start + duration > next.Start) duration = next.Start - start;
            if (duration == 0 && start == next.Start) faults.Add($"{lyric}: merged into {next.Lyric}");
        }

        return faults;
    }

    private static long Ms(string seconds) =>
        (long)Math.Round(double.Parse(seconds, CultureInfo.InvariantCulture) * 1000.0);

    private static List<(string Time, string Length, string Lyric)> Vocals(XDocument document) =>
    [
        .. document.Root!.Elements("vocal").Select(v =>
            (v.Attribute("time")!.Value, v.Attribute("length")!.Value, v.Attribute("lyric")!.Value))
    ];

    private static List<string> Lines(XDocument document) =>
        Lines(new RsXmlVocalsReader().ReadDocument(document, "vocals.xml").VocalTracks.Single());

    private static List<string> Lines(VocalTrack track) =>
    [
        .. track.Phrases.Select(p => string.Concat(p.Lyrics.Select((l, i) =>
            l.Text + (l.IsWordPart || i == p.Lyrics.Count - 1 ? "" : " "))))
    ];

    private static ChartProject Project(params LyricPhrase[] phrases)
    {
        var project = new ChartProject();
        foreach (var beat in Enumerable.Range(0, 32).Select(i => new BeatMarker { Time = i * 500, Bpm = 120 }))
            project.Beats.Add(beat);
        var track = new VocalTrack { Name = "Vocals" };
        foreach (var phrase in phrases) track.Phrases.Add(phrase);
        project.VocalTracks.Add(track);
        return project;
    }

    private static LyricPhrase Line(params LyricEvent[] lyrics)
    {
        var phrase = new LyricPhrase { Position = lyrics[0].Position, EndPosition = lyrics[^1].EndPosition };
        foreach (var lyric in lyrics) phrase.Lyrics.Add(lyric);
        phrase.Lyrics[^1].IsPhraseEnd = true;
        return phrase;
    }

    private static LyricEvent Syllable(int beat, int endBeat, string text, bool wordPart = false) => new()
    {
        Position = new FractionalPosition(beat), EndPosition = new FractionalPosition(endBeat),
        Text = text, IsWordPart = wordPart, Note = 60
    };
}
